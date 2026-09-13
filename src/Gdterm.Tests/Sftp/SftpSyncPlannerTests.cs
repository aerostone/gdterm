using System;
using System.Collections.Generic;
using System.IO;
using Gdterm.Sftp;
using Gdterm.Sftp.Models;

namespace Gdterm.Tests.Sftp
{
    /// <summary>
    /// 同步决策器测试——纯函数 + 本地文件系统，不连网。
    /// </summary>
    public static class SftpSyncPlannerTests
    {
        public static void Run()
        {
            DecideMissing();
            DecideSizeDiff();
            DecideSourceNewer();
            DecideSame();
            DecideMtimeTolerance();
            PlanUploadDownload();
        }

        private static void DecideMissing()
        {
            string reason;
            var a = SftpSyncPlanner.Decide(10, DateTime.UtcNow, false, 0, DateTime.MinValue, out reason);
            Assert.Equal(SyncAction.Transfer, a, "missing-target-transfers");
        }

        private static void DecideSizeDiff()
        {
            string reason;
            var now = DateTime.UtcNow;
            // 大小不同→传输（即使 mtime 一致也不跳过；此前此处重复断言 mtime 容差，size-diff 无覆盖）
            var a = SftpSyncPlanner.Decide(100, now, true, 200, now, out reason);
            Assert.Equal(SyncAction.Transfer, a, "size-diff-transfers");
            Assert.Equal("大小不同", reason, "size-diff-reason");
        }

        private static void DecideSourceNewer()
        {
            string reason;
            var now = DateTime.UtcNow;
            var a = SftpSyncPlanner.Decide(10, now, true, 10, now.AddMinutes(-5), out reason);
            Assert.Equal(SyncAction.Transfer, a, "source-newer-transfers");
        }

        private static void DecideSame()
        {
            string reason;
            var now = DateTime.UtcNow;
            var a = SftpSyncPlanner.Decide(10, now, true, 10, now, out reason);
            Assert.Equal(SyncAction.Skip, a, "same-skips");
        }

        private static void DecideMtimeTolerance()
        {
            string reason;
            var now = DateTime.UtcNow;
            var a = SftpSyncPlanner.Decide(10, now, true, 10, now.AddSeconds(-1), out reason);
            Assert.Equal(SyncAction.Skip, a, "mtime-within-tolerance-skips");
        }

        private static void PlanUploadDownload()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-synctest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                File.WriteAllText(Path.Combine(tmp, "a.txt"), "hello");
                Directory.CreateDirectory(Path.Combine(tmp, "sub"));
                File.WriteAllText(Path.Combine(tmp, "sub", "b.txt"), "world");

                var plan = SftpSyncPlanner.PlanUpload(tmp, "/remote", new Dictionary<string, SftpFileInfo>());
                Assert.Equal(2, plan.Count, "plan-upload-all-count");

                var remote = new Dictionary<string, SftpFileInfo>(StringComparer.Ordinal);
                var fi = new FileInfo(Path.Combine(tmp, "a.txt"));
                remote["a.txt"] = new SftpFileInfo { Name = "a.txt", FullPath = "/remote/a.txt", IsDirectory = false, SizeBytes = fi.Length, LastModified = fi.LastWriteTimeUtc };
                plan = SftpSyncPlanner.PlanUpload(tmp, "/remote", remote);
                int transfer = 0, skip = 0;
                foreach (var item in plan) { if (item.Action == SyncAction.Transfer) transfer++; else skip++; }
                Assert.Equal(1, transfer, "plan-upload-one-transfer");
                Assert.Equal(1, skip, "plan-upload-one-skip");

                var dl = SftpSyncPlanner.PlanDownload(remote, tmp, "/remote");
                Assert.Equal(1, dl.Count, "plan-download-count");
                Assert.Equal(SyncAction.Skip, dl[0].Action, "plan-download-existing-skips");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
