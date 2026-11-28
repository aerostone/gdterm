using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;
using Gdterm.Sftp;
using Gdterm.Sftp.Models;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    internal sealed class SftpTransferPlan
    {
        internal readonly List<string> Directories = new List<string>();
        internal readonly List<SftpTransferFile> Files = new List<SftpTransferFile>();
        internal bool Upload;
        internal string Root;
        internal int Conflicts;
        internal long TotalBytes;
    }
    internal sealed class SftpTransferFile
    {
        internal string Source, Target;
        internal long Size;
    }
    internal sealed class SftpTransferProgress
    {
        internal string Path;
        internal long Bytes, Total;
        internal int Completed, Count;
    }

    /// <summary>先完整检查路径和冲突，再执行固定源/目标的传输计划。</summary>
    internal sealed class SftpTransferRunner
    {
        private readonly ISftpService _sftp;
        public SftpTransferRunner(ISftpService sftp) { _sftp = sftp; }

        public async Task<SftpTransferPlan> PlanAsync(FileEntry[] entries, bool upload, string target, CancellationToken ct)
        {
            var plan = new SftpTransferPlan { Upload = upload, Root = target };
            if (upload)
            {
                var root = await _sftp.GetEntryAsync(target, ct).ConfigureAwait(false);
                if (root == null || !root.IsDirectory || root.IsSymbolicLink) throw new IOException(L10n.T("目标目录不可用。"));
            }
            else
            {
                SftpPathSafety.EnsureNoReparsePoints(target);
                if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
            }
            var names = new HashSet<string>(upload ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                await AddAsync(plan, new FileEntry { Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory,
                    IsSymbolicLink = entry.IsSymbolicLink, SizeBytes = entry.SizeBytes }, target, true, names, 0, ct).ConfigureAwait(false);
            return plan;
        }

        private async Task AddAsync(SftpTransferPlan plan, FileEntry source, string targetBase, bool parentExists,
            HashSet<string> names, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 128) throw new IOException(L10n.T("目录层级过深。"));
            if (source.IsSymbolicLink) throw new IOException(L10n.T("传输不能跟随符号链接：") + source.FullPath);
            string target;
            bool exists, isDirectory;
            if (plan.Upload)
            {
                SftpPathSafety.ValidateName(source.Name);
                SftpPathSafety.EnsureNoReparsePoints(source.FullPath);
                source.IsDirectory = Directory.Exists(source.FullPath);
                target = CombineRemote(targetBase, source.Name);
                var existing = parentExists ? await _sftp.GetEntryAsync(target, ct).ConfigureAwait(false) : null;
                if (existing != null && existing.IsSymbolicLink) throw new IOException(L10n.T("传输不能跟随符号链接：") + target);
                exists = existing != null;
                isDirectory = existing != null && existing.IsDirectory;
            }
            else
            {
                var current = await _sftp.GetEntryAsync(source.FullPath, ct).ConfigureAwait(false);
                if (current == null || current.IsSymbolicLink) throw new IOException(L10n.T("源文件不可用：") + source.FullPath);
                source.IsDirectory = current.IsDirectory;
                source.SizeBytes = current.SizeBytes;
                target = SftpPathSafety.LocalChild(plan.Root, targetBase, source.Name);
                exists = File.Exists(target) || Directory.Exists(target);
                isDirectory = Directory.Exists(target);
            }
            if (!names.Add(target)) throw new IOException(L10n.T("目标文件名冲突：") + target);
            if (exists && source.IsDirectory != isDirectory) throw new IOException(L10n.T("文件与目录类型冲突：") + target);
            if (source.IsDirectory)
            {
                if (!exists) plan.Directories.Add(target);
                if (plan.Upload)
                {
                    foreach (var item in new DirectoryInfo(source.FullPath).EnumerateFileSystemInfos())
                        await AddAsync(plan, new FileEntry { Name = item.Name, FullPath = item.FullName,
                            IsDirectory = (item.Attributes & FileAttributes.Directory) != 0,
                            IsSymbolicLink = (item.Attributes & FileAttributes.ReparsePoint) != 0 },
                            target, exists, names, depth + 1, ct).ConfigureAwait(false);
                }
                else
                {
                    var children = await _sftp.ListDirectoryAsync(source.FullPath, ct).ConfigureAwait(false);
                    foreach (var item in children)
                        await AddAsync(plan, new FileEntry { Name = item.Name, FullPath = item.FullPath,
                            IsDirectory = item.IsDirectory, IsSymbolicLink = item.IsSymbolicLink, SizeBytes = item.SizeBytes },
                            target, exists, names, depth + 1, ct).ConfigureAwait(false);
                }
            }
            else
            {
                long size = plan.Upload ? new FileInfo(source.FullPath).Length : source.SizeBytes;
                plan.Files.Add(new SftpTransferFile { Source = source.FullPath, Target = target, Size = size });
                plan.TotalBytes = checked(plan.TotalBytes + size);
                if (exists) plan.Conflicts++;
            }
        }

        public async Task ExecuteAsync(SftpTransferPlan plan, bool resume, IProgress<SftpTransferProgress> progress, CancellationToken ct)
        {
            foreach (var dir in plan.Directories)
            {
                ct.ThrowIfCancellationRequested();
                if (plan.Upload) await _sftp.CreateDirectoryAsync(dir, ct).ConfigureAwait(false);
                else { SftpPathSafety.EnsureNoReparsePoints(dir); Directory.CreateDirectory(dir); }
            }
            long done = 0;
            int completed = 0;
            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                long prior = done;
                int priorCount = completed;
                var adapter = new InlineProgress<FileTransferProgress>(p => progress?.Report(new SftpTransferProgress
                {
                    Path = file.Source, Bytes = prior + p.BytesTransferred, Total = plan.TotalBytes,
                    Completed = priorCount, Count = plan.Files.Count
                }));
                if (plan.Upload)
                {
                    if (resume) await _sftp.UploadResumeAsync(file.Source, file.Target, adapter, ct).ConfigureAwait(false);
                    else await _sftp.UploadAsync(file.Source, file.Target, adapter, ct).ConfigureAwait(false);
                }
                else
                {
                    SftpPathSafety.EnsureNoReparsePoints(file.Target);
                    if (resume) await _sftp.DownloadResumeAsync(file.Source, file.Target, adapter, ct).ConfigureAwait(false);
                    else await _sftp.DownloadAsync(file.Source, file.Target, adapter, ct).ConfigureAwait(false);
                }
                done += file.Size;
                completed++;
                progress?.Report(new SftpTransferProgress { Path = file.Source, Bytes = done, Total = plan.TotalBytes,
                    Completed = completed, Count = plan.Files.Count });
            }
        }
        private static string CombineRemote(string path, string name) { return path.TrimEnd('/') + "/" + name; }
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            internal InlineProgress(Action<T> report) { _report = report; }
            public void Report(T value) { _report(value); }
        }
    }
}
