using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Terminal.Transfer;

namespace Gdterm.Tests.Terminal
{
    /// <summary>
    /// Zmodem 接收子集测试——自编码 ZBIN 帧对测（帧格式自洽 + 落盘正确 + 应答常量）。
    /// 不证明对得上 lrzsz，真机联调仍需 Windows 手验。
    /// </summary>
    public static class ZmodemReceiverTests
    {
        public static void Run()
        {
            Crc16KnownVector();
            HexReplyFormat();
            FileReceiveRoundtrip();
            Crc32DowngradeNak();
        }

        private static void Crc16KnownVector()
        {
            // CRC16-XMODEM("123456789") = 0x31C3（标准向量）
            var data = Encoding.ASCII.GetBytes("123456789");
            Assert.Equal((ushort)0x31C3, ZmodemReceiver.Crc16(data, 0, data.Length), "crc16-xmodem-vector");
        }

        private static void HexReplyFormat()
        {
            var r = ZmodemReceiver.BuildHexReply(9, 0); // ZRPOS(0)
            string s = Encoding.ASCII.GetString(r);
            Assert.True(s.StartsWith("**" + ((char)0x18).ToString() + "B"), "hex-reply-head");
            Assert.Contains(s, "09", "hex-reply-type");
        }

        private static byte[] EncodeZbin(byte ftype, byte[] p, byte[] payload, byte endCode)
        {
            var out_ = new List<byte>();
            out_.Add(0x2A); out_.Add(0x2A); out_.Add(0x18); out_.Add(0x41); // **ZDLE 'A'
            var head = new byte[5];
            head[0] = ftype;
            Array.Copy(p, 0, head, 1, 4);
            foreach (var b in head) Escape(out_, b);
            ushort crc = ZmodemReceiver.Crc16(head, 0, 5);
            Escape(out_, (byte)(crc & 0xFF));
            Escape(out_, (byte)((crc >> 8) & 0xFF));
            if (payload != null)
            {
                foreach (var b in payload) Escape(out_, b);
                out_.Add(0x18); out_.Add((byte)(endCode ^ 0x40));
                var c2 = new byte[] { endCode };
                ushort c = ZmodemReceiver.Crc16(c2, 0, 1);
                Escape(out_, (byte)(c & 0xFF));
                Escape(out_, (byte)((c >> 8) & 0xFF));
            }
            return out_.ToArray();
        }

        private static void Escape(List<byte> into_, byte b)
        {
            if (b == 0x18 || b == 0x11 || b == 0x13 || b == 0x91 || b == 0x93 || b == 0x40)
            { into_.Add(0x18); into_.Add((byte)(b ^ 0x40)); }
            else into_.Add(b);
        }

        private static void FileReceiveRoundtrip()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-zm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                using (var rx = new ZmodemReceiver())
                {
                    rx.Start(tmp);
                    Assert.True(rx.HasReply, "start-queues-zrinit");
                    rx.TakeReply(); // 消费 ZRINIT

                    // ZRQINIT（ZHEX 空包）
                    var rq = new List<byte> { 0x2A, 0x2A, 0x18, 0x42 };
                    rq.AddRange(Encoding.ASCII.GetBytes("00" + "00000000" + "0000" + "\r\n"));
                    // CRC 占位（解析器不校验 ZHEX CRC，直接识类型）
                    bool consumed = rx.Feed(rq.ToArray(), 0, rq.Count);
                    Assert.True(consumed, "zrqinit-consumed");

                    // ZFILE：文件名 + "11 0 0" 元数据
                    byte[] nameBytes = Encoding.ASCII.GetBytes("hello.txt");
                    byte[] metaBytes = Encoding.ASCII.GetBytes("11 0 0 0");
                    byte[] meta = new byte[nameBytes.Length + 1 + metaBytes.Length];
                    Array.Copy(nameBytes, 0, meta, 0, nameBytes.Length);
                    meta[nameBytes.Length] = 0;
                    Array.Copy(metaBytes, 0, meta, nameBytes.Length + 1, metaBytes.Length);
                    var zfile = EncodeZbin(4, new byte[4], meta, 0x6B); // ZCRCW
                    rx.Feed(zfile, 0, zfile.Length);
                    Assert.Equal("hello.txt", rx.CurrentFileName, "zfile-name");
                    Assert.Equal(11L, rx.CurrentFileSize, "zfile-size");

                    // ZDATA：11 字节
                    byte[] data = Encoding.ASCII.GetBytes("hello world");
                    var zdata = EncodeZbin(10, new byte[4], data, 0x6B);
                    rx.Feed(zdata, 0, zdata.Length);
                    Assert.Equal(11L, rx.BytesReceived, "zdata-received");

                    // ZEOF（offset=11）
                    var zeof = EncodeZbin(11, new byte[] { 11, 0, 0, 0 }, null, 0);
                    rx.Feed(zeof, 0, zeof.Length);
                    string saved = Path.Combine(tmp, "hello.txt");
                    Assert.True(File.Exists(saved), "file-landed");
                    Assert.Equal("hello world", File.ReadAllText(saved), "file-content");

                    // ZFIN
                    var zfin = EncodeZbin(8, new byte[4], null, 0);
                    rx.Feed(zfin, 0, zfin.Length);
                    Assert.Equal(ZmodemReceiver.RecvState.Done, rx.State, "state-done");
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        private static void Crc32DowngradeNak()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-zm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                using (var rx = new ZmodemReceiver())
                {
                    rx.Start(tmp);
                    rx.TakeReply();
                    // ZBIN32 头 → ZNAK
                    var f = new byte[] { 0x2A, 0x2A, 0x18, 0x43, 0x00 };
                    rx.Feed(f, 0, f.Length);
                    Assert.True(rx.HasReply, "zbin32-nak-queued");
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
