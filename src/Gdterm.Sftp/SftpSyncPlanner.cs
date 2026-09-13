using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Sftp.Models;

namespace Gdterm.Sftp
{
    /// <summary>
    /// 单文件同步决策。
    /// </summary>
    public enum SyncAction
    {
        /// <summary>跳过：两端一致</summary>
        Skip,
        /// <summary>传输：目标缺失或源更新</summary>
        Transfer,
    }

    /// <summary>
    /// 单个文件的同步计划项（相对路径口径）。
    /// </summary>
    public sealed class SyncPlanItem
    {
        public string RelativePath { get; set; }
        public string LocalFullPath { get; set; }
        public string RemoteFullPath { get; set; }
        public SyncAction Action { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// SFTP 文件夹同步决策器——纯函数（无连接依赖，可单元测试）。
    /// 口径：按相对路径对齐；大小不同→传输；大小相同且源 mtime 更新（容差 2 秒）→传输；否则跳过。
    /// 目录只建不删（不同步删除，避免误删）。
    /// </summary>
    public static class SftpSyncPlanner
    {
        private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

        /// <summary>比较单个文件，返回动作与原因（供测试与 UI 提示）。</summary>
        public static SyncAction Decide(long sourceSize, DateTime sourceMtimeUtc, bool targetExists, long targetSize, DateTime targetMtimeUtc, out string reason)
        {
            if (!targetExists) { reason = "目标缺失"; return SyncAction.Transfer; }
            if (sourceSize != targetSize) { reason = "大小不同"; return SyncAction.Transfer; }
            if (sourceMtimeUtc - targetMtimeUtc > MtimeTolerance) { reason = "源更新"; return SyncAction.Transfer; }
            reason = "一致";
            return SyncAction.Skip;
        }

        /// <summary>规划上传：本地目录树 vs 远端平铺 listing（调用方按目录递归取 listing 并拼相对路径）。</summary>
        public static List<SyncPlanItem> PlanUpload(string localDir, string remoteBase, IDictionary<string, SftpFileInfo> remoteByRel)
        {
            var plan = new List<SyncPlanItem>();
            if (string.IsNullOrEmpty(localDir) || !Directory.Exists(localDir)) return plan;
            foreach (var file in Directory.EnumerateFiles(localDir, "*", SearchOption.AllDirectories))
            {
                string rel = RelPath(localDir, file);
                if (rel == null) continue;
                var fi = new FileInfo(file);
                SftpFileInfo remote = null;
                bool exists = remoteByRel != null && remoteByRel.TryGetValue(Norm(rel), out remote);
                string reason;
                var action = Decide(fi.Length, fi.LastWriteTimeUtc, exists, exists ? remote.SizeBytes : 0,
                    exists ? remote.LastModified.ToUniversalTime() : DateTime.MinValue, out reason);
                plan.Add(new SyncPlanItem
                {
                    RelativePath = rel,
                    LocalFullPath = file,
                    RemoteFullPath = remoteBase.TrimEnd('/') + "/" + rel.Replace(Path.DirectorySeparatorChar, '/'),
                    Action = action,
                    Reason = reason,
                });
            }
            return plan;
        }

        /// <summary>规划下载：远端 listing（相对路径口径） vs 本地目录树。</summary>
        public static List<SyncPlanItem> PlanDownload(IDictionary<string, SftpFileInfo> remoteByRel, string localDir, string remoteBase)
        {
            var plan = new List<SyncPlanItem>();
            if (remoteByRel == null) return plan;
            foreach (var kv in remoteByRel)
            {
                var remote = kv.Value;
                if (remote == null || remote.IsDirectory) continue;
                string rel = kv.Key;
                string local = Path.Combine(localDir, rel.Replace('/', Path.DirectorySeparatorChar));
                bool exists = File.Exists(local);
                var fi = exists ? new FileInfo(local) : null;
                string reason;
                var action = Decide(remote.SizeBytes, remote.LastModified.ToUniversalTime(), exists,
                    exists ? fi.Length : 0, exists ? fi.LastWriteTimeUtc : DateTime.MinValue, out reason);
                plan.Add(new SyncPlanItem
                {
                    RelativePath = rel,
                    LocalFullPath = local,
                    RemoteFullPath = remoteBase.TrimEnd('/') + "/" + rel,
                    Action = action,
                    Reason = reason,
                });
            }
            return plan;
        }

        internal static string Norm(string rel)
        {
            return (rel ?? "").Replace(Path.DirectorySeparatorChar, '/').TrimStart('/');
        }

        internal static string RelPath(string baseDir, string full)
        {
            try
            {
                var b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var f = Path.GetFullPath(full);
                if (!f.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return null;
                return f.Substring(b.Length);
            }
            catch { return null; }
        }
    }
}
