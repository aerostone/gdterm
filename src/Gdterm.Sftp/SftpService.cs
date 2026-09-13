using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Sftp.Models;
using Renci.SshNet;

namespace Gdterm.Sftp
{
    /// <summary>
    /// SFTP 文件服务实现——基于 SSH.NET SftpClient
    /// </summary>
    public class SftpService : ISftpService
    {
        private SftpClient _client;
        private bool _disposed;

        public bool IsConnected => _client?.IsConnected == true;

        /// <summary>
        /// 直连模式连接
        /// </summary>
        public Task ConnectAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credential == null) throw new ArgumentNullException(nameof(credential));

            var connInfo = SshConnectionInfoFactory.Create(
                config.Host,
                config.Port,
                credential.Username ?? config.Username,
                credential);

            _client = new SftpClient(connInfo);
            Gdterm.Tunnel.SshKeepAlive.Apply(_client, config);
            _client.Connect();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 跳板模式连接（通过隧道接入点）
        /// </summary>
        public Task ConnectViaTunnelAsync(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));

            var connInfo = SshConnectionInfoFactory.Create(
                tunnelEndpoint.LocalHost,
                tunnelEndpoint.LocalPort,
                credential.Username ?? config.Username,
                credential);

            _client = new SftpClient(connInfo);
            Gdterm.Tunnel.SshKeepAlive.Apply(_client, config);
            _client.Connect();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 列出目录内容（目录优先 + 名称排序）
        /// </summary>
        public Task<IList<SftpFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct)
        {
            EnsureConnected();

            var entries = _client.ListDirectory(remotePath);
            var result = entries
                .Where(e => e.Name != "." && e.Name != "..")
                .Select(e => new SftpFileInfo
                {
                    Name = e.Name,
                    FullPath = e.FullName,
                    IsDirectory = e.IsDirectory,
                    SizeBytes = (long)e.Length,
                    LastModified = e.LastWriteTime,
                    Permissions = ToRwx(e),
                    Owner = e.UserId.ToString(),
                    Group = e.GroupId.ToString()
                })
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IList<SftpFileInfo>>(result);
        }

        /// <summary>rwx 九字符（SSH.NET SftpFile 三元组实证：Owner/Group/Others × CanRead/Write/Execute）。</summary>
        internal static string ToRwx(Renci.SshNet.Sftp.ISftpFile f)
        {
            var sb = new System.Text.StringBuilder(9);
            sb.Append(f.OwnerCanRead ? 'r' : '-');
            sb.Append(f.OwnerCanWrite ? 'w' : '-');
            sb.Append(f.OwnerCanExecute ? 'x' : '-');
            sb.Append(f.GroupCanRead ? 'r' : '-');
            sb.Append(f.GroupCanWrite ? 'w' : '-');
            sb.Append(f.GroupCanExecute ? 'x' : '-');
            sb.Append(f.OthersCanRead ? 'r' : '-');
            sb.Append(f.OthersCanWrite ? 'w' : '-');
            sb.Append(f.OthersCanExecute ? 'x' : '-');
            return sb.ToString();
        }

        /// <summary>
        /// 上传文件
        /// </summary>
        public Task UploadAsync(string localPath, string remotePath, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            EnsureConnected();

            if (!File.Exists(localPath))
                throw new FileNotFoundException("本地文件不存在", localPath);

            var totalBytes = new FileInfo(localPath).Length;
            var stopwatch = Stopwatch.StartNew();

            using (var fileStream = File.OpenRead(localPath))
            {
                _client.UploadFile(fileStream, remotePath, true, uploadedBytes =>
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new FileTransferProgress
                    {
                        BytesTransferred = (long)uploadedBytes,
                        TotalBytes = totalBytes,
                        Elapsed = stopwatch.Elapsed
                    });
                });
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 下载文件
        /// </summary>
        public Task DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            EnsureConnected();

            // 获取远程文件大小（SSH.NET 2024 Length 为 ulong）
            var remoteInfo = _client.Get(remotePath);
            var totalBytes = (long)remoteInfo.Length;
            var stopwatch = Stopwatch.StartNew();

            using (var fileStream = File.Create(localPath))
            {
                _client.DownloadFile(remotePath, fileStream, downloadedBytes =>
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new FileTransferProgress
                    {
                        BytesTransferred = (long)downloadedBytes,
                        TotalBytes = totalBytes,
                        Elapsed = stopwatch.Elapsed
                    });
                });
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 上传断点续传（SSH.NET 无 reput 原生支持：Open Append + 本地 Seek 手工实现）。
        /// </summary>
        public Task<bool> UploadResumeAsync(string localPath, string remotePath, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            EnsureConnected();

            if (!File.Exists(localPath))
                throw new FileNotFoundException("本地文件不存在", localPath);

            var localSize = new FileInfo(localPath).Length;

            // 远端大小：Get 不存在时抛异常→视为全新上传（项目内已用模式，见 DownloadAsync）
            long remoteSize = 0;
            bool remoteExists = true;
            try { remoteSize = (long)_client.Get(remotePath).Length; }
            catch { remoteExists = false; remoteSize = 0; }

            if (remoteExists && localSize > 0 && remoteSize == localSize)
                return Task.FromResult(false); // 已是最新，跳过
            if (remoteExists && remoteSize > 0 && remoteSize < localSize)
            {
                // 断点追加
                var stopwatch = Stopwatch.StartNew();
                using (var local = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var remote = _client.Open(remotePath, FileMode.Append, FileAccess.Write))
                {
                    local.Seek(remoteSize, SeekOrigin.Begin);
                    var buf = new byte[81920];
                    long done = remoteSize;
                    int n;
                    while ((n = local.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        remote.Write(buf, 0, n);
                        done += n;
                        if (progress != null) progress.Report(new FileTransferProgress
                        {
                            BytesTransferred = done,
                            TotalBytes = localSize,
                            Elapsed = stopwatch.Elapsed
                        });
                    }
                }
                return Task.FromResult(true);
            }

            // 全新/脏文件整传覆盖（复用 UploadFile 路径）
            var totalBytes = localSize;
            var sw = Stopwatch.StartNew();
            using (var fileStream = File.OpenRead(localPath))
            {
                _client.UploadFile(fileStream, remotePath, true, uploadedBytes =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (progress != null) progress.Report(new FileTransferProgress
                    {
                        BytesTransferred = (long)uploadedBytes,
                        TotalBytes = totalBytes,
                        Elapsed = sw.Elapsed
                    });
                });
            }
            return Task.FromResult(true);
        }

        /// <summary>
        /// 下载断点续传（SSH.NET 无 reget 原生支持：OpenRead + 双端 Seek 手工实现）。
        /// </summary>
        public Task<bool> DownloadResumeAsync(string remotePath, string localPath, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            EnsureConnected();

            // Get 不存在时抛异常→与原 DownloadAsync 行为一致（直接抛给调用方）
            var remoteSize = (long)_client.Get(remotePath).Length;
            long localSize = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;

            if (remoteSize > 0 && localSize == remoteSize)
                return Task.FromResult(false); // 已是最新，跳过
            if (localSize > 0 && localSize < remoteSize)
            {
                // 断点追加
                var stopwatch = Stopwatch.StartNew();
                using (var remote = _client.OpenRead(remotePath))
                using (var local = new FileStream(localPath, FileMode.Append, FileAccess.Write, FileShare.None))
                {
                    remote.Seek(localSize, SeekOrigin.Begin);
                    var buf = new byte[81920];
                    long done = localSize;
                    int n;
                    while ((n = remote.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        local.Write(buf, 0, n);
                        done += n;
                        if (progress != null) progress.Report(new FileTransferProgress
                        {
                            BytesTransferred = done,
                            TotalBytes = remoteSize,
                            Elapsed = stopwatch.Elapsed
                        });
                    }
                }
                return Task.FromResult(true);
            }

            // 全新/脏文件整传覆盖（复用 DownloadFile 路径）
            var totalBytes = remoteSize;
            var sw = Stopwatch.StartNew();
            using (var fileStream = File.Create(localPath))
            {
                _client.DownloadFile(remotePath, fileStream, downloadedBytes =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (progress != null) progress.Report(new FileTransferProgress
                    {
                        BytesTransferred = (long)downloadedBytes,
                        TotalBytes = totalBytes,
                        Elapsed = sw.Elapsed
                    });
                });
            }
            return Task.FromResult(true);
        }

        /// <summary>
        /// 删除文件或目录
        /// </summary>
        public Task DeleteAsync(string remotePath, bool recursive, CancellationToken ct)
        {
            EnsureConnected();

            var entry = _client.Get(remotePath);
            if (entry.IsDirectory)
            {
                if (recursive)
                {
                    DeleteDirectoryRecursive(remotePath, ct);
                }
                else
                {
                    _client.DeleteDirectory(remotePath);
                }
            }
            else
            {
                _client.DeleteFile(remotePath);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 创建目录
        /// </summary>
        public Task CreateDirectoryAsync(string remotePath, CancellationToken ct)
        {
            EnsureConnected();
            _client.CreateDirectory(remotePath);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 重命名/移动文件或目录
        /// </summary>
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct)
        {
            EnsureConnected();
            _client.RenameFile(oldPath, newPath);
            return Task.CompletedTask;
        }

        /// <summary>修改远端权限（SFTP 协议 ChangePermissions，不走 shell；octalMode 十进制位如 755）。</summary>
        public Task ChmodAsync(string remotePath, int octalMode, CancellationToken ct)
        {
            EnsureConnected();
            if (string.IsNullOrEmpty(remotePath)) throw new ArgumentNullException("remotePath");
            int mode = 0;
            int d0 = (octalMode / 100) % 10, d1 = (octalMode / 10) % 10, d2 = octalMode % 10;
            if (d0 < 0 || d0 > 7 || d1 < 0 || d1 > 7 || d2 < 0 || d2 > 7)
                throw new ArgumentOutOfRangeException("octalMode", "权限位须为 000-777（如 755）。");
            mode = (d0 << 6) | (d1 << 3) | d2;
            _client.ChangePermissions(remotePath, (short)mode);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_client?.IsConnected == true)
                    _client.Disconnect();
                _client?.Dispose();
            }
            catch { /* best-effort */ }

            _client = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("SFTP 未连接，请先调用 ConnectAsync");
        }

        private void DeleteDirectoryRecursive(string remotePath, CancellationToken ct)
        {
            var entries = _client.ListDirectory(remotePath);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.Name == "." || entry.Name == "..")
                    continue;

                if (entry.IsDirectory)
                {
                    DeleteDirectoryRecursive(entry.FullName, ct);
                    _client.DeleteDirectory(entry.FullName);
                }
                else
                {
                    _client.DeleteFile(entry.FullName);
                }
            }
        }
    }
}
