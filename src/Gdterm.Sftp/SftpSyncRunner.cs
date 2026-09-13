using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Sftp.Models;

namespace Gdterm.Sftp
{
    /// <summary>
    /// SFTP 文件夹同步执行结果。
    /// </summary>
    public sealed class SyncRunResult
    {
        public int Total;
        public int Transferred;
        public int Skipped;
        public int Failed;
        public List<string> Errors = new List<string>();
    }

    /// <summary>
    /// SFTP 文件夹同步编排——只用 ISftpService 已验证 API
    /// （ListDirectoryAsync/CreateDirectoryAsync/UploadResumeAsync/DownloadResumeAsync），
    /// 不赌 SynchronizeDirectories 签名。目录只建不删。
    /// </summary>
    public static class SftpSyncRunner
    {
        /// <summary>递归取远端目录树（相对路径→SftpFileInfo，不含 . ..）。</summary>
        public static async Task<Dictionary<string, SftpFileInfo>> ListRemoteTreeAsync(ISftpService sftp, string remoteBase, CancellationToken ct)
        {
            var map = new Dictionary<string, SftpFileInfo>(StringComparer.Ordinal);
            await CollectRemoteAsync(sftp, remoteBase.TrimEnd('/'), "", map, ct);
            return map;
        }

        private static async Task CollectRemoteAsync(ISftpService sftp, string remoteBase, string rel, Dictionary<string, SftpFileInfo> map, CancellationToken ct)
        {
            string dir = string.IsNullOrEmpty(rel) ? remoteBase : remoteBase + "/" + rel;
            IList<SftpFileInfo> items;
            try { items = await sftp.ListDirectoryAsync(dir, ct); }
            catch { return; } // 目录不存在→视为空（调用方建目录）
            if (items == null) return;
            foreach (var f in items)
            {
                ct.ThrowIfCancellationRequested();
                if (f == null || f.Name == "." || f.Name == "..") continue;
                string child = string.IsNullOrEmpty(rel) ? f.Name : rel + "/" + f.Name;
                map[child] = f;
                if (f.IsDirectory) await CollectRemoteAsync(sftp, remoteBase, child, map, ct);
            }
        }

        /// <summary>确保远端目录存在（已存在则继续——双栏面板同款模式）。</summary>
        public static async Task EnsureRemoteDirAsync(ISftpService sftp, string remoteDir, CancellationToken ct)
        {
            try { await sftp.CreateDirectoryAsync(remoteDir, ct); }
            catch
            {
                try { await sftp.ListDirectoryAsync(remoteDir, ct); }
                catch (Exception ex) { throw new Exception("无法创建远程目录 " + remoteDir + ": " + ex.Message, ex); }
            }
        }

        /// <summary>执行上传同步计划（目录先建，再逐文件续传）。</summary>
        public static async Task<SyncRunResult> RunUploadAsync(ISftpService sftp, List<SyncPlanItem> plan, IProgress<FileTransferProgress> perFile, CancellationToken ct)
        {
            var result = new SyncRunResult { Total = plan != null ? plan.Count : 0 };
            if (plan == null) return result;
            var madeDirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (item == null) continue;
                if (item.Action == SyncAction.Skip) { result.Skipped++; continue; }
                try
                {
                    string dir = item.RemoteFullPath;
                    int slash = dir.LastIndexOf('/');
                    if (slash > 0)
                    {
                        string parent = dir.Substring(0, slash);
                        if (madeDirs.Add(parent)) await EnsureRemoteDirAsync(sftp, parent, ct);
                    }
                    bool done = await sftp.UploadResumeAsync(item.LocalFullPath, item.RemoteFullPath, perFile, ct);
                    if (done) result.Transferred++; else result.Skipped++;
                }
                catch (Exception ex) { result.Failed++; result.Errors.Add(item.RelativePath + ": " + ex.Message); }
            }
            return result;
        }

        /// <summary>执行下载同步计划（本地目录先建，再逐文件续传）。</summary>
        public static async Task<SyncRunResult> RunDownloadAsync(ISftpService sftp, List<SyncPlanItem> plan, IProgress<FileTransferProgress> perFile, CancellationToken ct)
        {
            var result = new SyncRunResult { Total = plan != null ? plan.Count : 0 };
            if (plan == null) return result;
            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (item == null) continue;
                if (item.Action == SyncAction.Skip) { result.Skipped++; continue; }
                try
                {
                    string parent = Path.GetDirectoryName(item.LocalFullPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    bool done = await sftp.DownloadResumeAsync(item.RemoteFullPath, item.LocalFullPath, perFile, ct);
                    if (done) result.Transferred++; else result.Skipped++;
                }
                catch (Exception ex) { result.Failed++; result.Errors.Add(item.RelativePath + ": " + ex.Message); }
            }
            return result;
        }
    }
}
