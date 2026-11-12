using System;
using System.IO;
using System.Text;
using Gdterm.Core.Recording;

namespace Gdterm.Tests.Core
{
    /// <summary>
    /// 会话录制格式的往返断言。这个格式是**审计证据的容器**：写坏了就是"回放出来的不是当时发生的事"，
    /// 比不录制更危险（假证据）。所以边界全测：转义、二进制控制序列、坏行跳过、版本拒绝。
    /// </summary>
    public static class SessionRecordingTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] SessionRecording");

            string dir = Path.Combine(Path.GetTempPath(), "gdterm-rec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RoundTrip(dir);
                BadLinesAreSkipped(dir);
                NotARecordingIsRejected(dir);
                EmptyPayloadsIgnored(dir);
                FutureVersionRejected(dir);
                HeaderEscaping(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void RoundTrip(string dir)
        {
            string path = Path.Combine(dir, "rt.gtr");
            var header = new SessionRecordingHeader
            {
                Columns = 120,
                Rows = 30,
                Host = "10.0.0.11",
                ConnectionName = "prod-web",
                StartedUtc = new DateTime(2026, 11, 12, 2, 0, 0, DateTimeKind.Utc)
            };

            // 真实内容：ANSI 清屏 + 中文 + 控制字节 0x1b/0x07 + 高位字节 0xff
            var e1 = Encoding.UTF8.GetBytes("\x1b[2J\x1b[H$ 目录列表\n");
            var e2 = new byte[] { 0x1b, 0x5d, 0x30, 0x3b, 0xff, 0x07 };
            var i1 = Encoding.UTF8.GetBytes("ls -la\r");

            using (var w = new SessionRecordingWriter(path, header))
            {
                w.Append(SessionFrameKind.Output, e1);
                w.Append(SessionFrameKind.Input, i1);
                w.Append(SessionFrameKind.Output, e2);
                w.Flush();
            }

            var rec = SessionRecording.Load(path);
            Assert.Equal(3, rec.Frames.Count, "frame count");
            Assert.Equal(0, rec.SkippedLines, "no skipped lines in clean file");

            Assert.Equal(SessionFrameKind.Output, rec.Frames[0].Kind, "frame0 kind");
            Assert.Equal(SessionFrameKind.Input, rec.Frames[1].Kind, "frame1 kind");
            Assert.Equal(SessionFrameKind.Output, rec.Frames[2].Kind, "frame2 kind");

            // 字节必须逐字节一致：多一层编码转换就是回放错乱
            AssertBytesEqual(e1, rec.Frames[0].Data, "frame0 bytes");
            AssertBytesEqual(i1, rec.Frames[1].Data, "frame1 bytes");
            AssertBytesEqual(e2, rec.Frames[2].Data, "frame2 bytes");

            Assert.Equal(120, rec.Header.Columns, "header cols");
            Assert.Equal(30, rec.Header.Rows, "header rows");
            Assert.Equal("10.0.0.11", rec.Header.Host, "header host");
            Assert.Equal("prod-web", rec.Header.ConnectionName, "header name");
            Assert.Equal(new DateTime(2026, 11, 12, 2, 0, 0, DateTimeKind.Utc), rec.Header.StartedUtc,
                "header started");
            Assert.Equal(1, rec.Header.Version, "header version");

            // 时间轴必须单调不减（回放调度器按它排序，乱序会倒放）
            Assert.True(rec.Frames[0].OffsetMs <= rec.Frames[1].OffsetMs, "offset monotonic 0<=1");
            Assert.True(rec.Frames[1].OffsetMs <= rec.Frames[2].OffsetMs, "offset monotonic 1<=2");

            Assert.Equal((long)(e1.Length + e2.Length), rec.OutputBytes, "output bytes only counts output");
            Assert.True(rec.EndedUtc.HasValue, "footer end time parsed");

            // 尾部 duration 参与总时长（比最后一帧偏移更能说明"录了多久"）
            Assert.True(rec.DurationMs >= rec.Frames[2].OffsetMs, "duration >= last offset");
        }

        private static void BadLinesAreSkipped(string dir)
        {
            string path = Path.Combine(dir, "bad.gtr");
            var lines = new[]
            {
                "GTR1",
                "{\"v\":1,\"cols\":80,\"rows\":24,\"host\":\"h\",\"name\":\"n\",\"started\":\"2026-11-12T02:00:00Z\"}",
                "o 0 " + Convert.ToBase64String(Encoding.ASCII.GetBytes("ok")),
                "o notanumber b2s=",          // 时间不是数字
                "o 10 !!!notbase64!!!",       // base64 坏了
                "x 20 b2s=",                  // 未知方向
                "   ",                        // 空白
                "o 30",                       // 字段不够
                "i 40 " + Convert.ToBase64String(Encoding.ASCII.GetBytes("cmd")),
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(false));

            var rec = SessionRecording.Load(path);
            Assert.Equal(2, rec.Frames.Count, "only good frames survive");
            Assert.Equal(5, rec.SkippedLines, "bad lines counted (not silently dropped)");
            Assert.Equal(SessionFrameKind.Input, rec.Frames[1].Kind, "surviving input frame");
        }

        private static void NotARecordingIsRejected(string dir)
        {
            string path = Path.Combine(dir, "plain.log");
            File.WriteAllText(path, "hello world\r\nsecond line\r\n");
            string error;
            var rec = SessionRecording.TryLoad(path, out error);
            Assert.True(rec == null, "non-recording rejected");
            Assert.True(!string.IsNullOrEmpty(error), "rejection carries a reason: " + error);
            Assert.True(error.Contains("GTR1"), "reason mentions the magic: " + error);

            // 不存在的文件同样走 TryLoad 不抛
            string error2;
            Assert.True(SessionRecording.TryLoad(Path.Combine(dir, "nope.gtr"), out error2) == null,
                "missing file returns null");
            Assert.True(!string.IsNullOrEmpty(error2), "missing file carries a reason");
        }

        private static void EmptyPayloadsIgnored(string dir)
        {
            string path = Path.Combine(dir, "empty.gtr");
            using (var w = new SessionRecordingWriter(path, new SessionRecordingHeader()))
            {
                w.Append(SessionFrameKind.Output, new byte[0]);
                w.Append(SessionFrameKind.Output, null);
                w.Append(SessionFrameKind.Output, new byte[] { 1, 2, 3 }, 1, 0);
            }
            var rec = SessionRecording.Load(path);
            Assert.Equal(0, rec.Frames.Count, "empty payloads not written");
            Assert.Equal(0L, rec.OutputBytes, "no output bytes");
            Assert.Equal(0L, rec.DurationMs, "duration of empty recording is 0");
        }

        private static void FutureVersionRejected(string dir)
        {
            string path = Path.Combine(dir, "future.gtr");
            File.WriteAllLines(path, new[]
            {
                "GTR1",
                "{\"v\":9,\"cols\":80,\"rows\":24}"
            }, new UTF8Encoding(false));

            string error;
            Assert.True(SessionRecording.TryLoad(path, out error) == null, "future version rejected");

            // 缺头行也要拒绝（而不是当成 0 帧继续跑）
            string path2 = Path.Combine(dir, "nohead.gtr");
            File.WriteAllText(path2, "GTR1\n", new UTF8Encoding(false));
            string error2;
            Assert.True(SessionRecording.TryLoad(path2, out error2) == null, "missing header line rejected");
        }

        private static void HeaderEscaping(string dir)
        {
            // 主机名/连接名里出现引号、反斜杠、换行时不能破坏头部 JSON（否则整个文件读不回来）
            string path = Path.Combine(dir, "esc.gtr");
            var header = new SessionRecordingHeader
            {
                Host = "host\"with\\quote",
                ConnectionName = "名 字\t带制表\n换行"
            };
            using (var w = new SessionRecordingWriter(path, header))
                w.Append(SessionFrameKind.Output, new byte[] { 0x41 });

            var rec = SessionRecording.Load(path);
            Assert.Equal("host\"with\\quote", rec.Header.Host, "escaped host round-trips");
            Assert.Equal("名 字\t带制表\n换行", rec.Header.ConnectionName, "escaped name round-trips");
        }

        private static void AssertBytesEqual(byte[] expected, byte[] actual, string what)
        {
            Assert.True(actual != null, what + " not null");
            Assert.Equal(expected.Length, actual.Length, what + " length");
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    Assert.True(false, what + " byte " + i + ": expected 0x" + expected[i].ToString("x2")
                        + " got 0x" + actual[i].ToString("x2"));
                    return;
                }
            }
        }
    }
}
