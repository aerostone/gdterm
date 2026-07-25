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
                    SizeBytes = e.Length,
                    LastModified = e.LastWriteTime,
                    Permissions = e.OwnerCanRead.ToString(), // 简化权限表示
                    Owner = e.UserId.ToString(),
                    Group = e.GroupId.ToString()
                })
                .OrderByDescending(f => f.IsDirectory)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IList<SftpFileInfo>>(result);
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
                        BytesTransferred = uploadedBytes,
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

            // 获取远程文件大小
            var remoteInfo = _client.Get(remotePath);
            var totalBytes = remoteInfo.Length;
            var stopwatch = Stopwatch.StartNew();

            using (var fileStream = File.Create(localPath))
            {
                _client.DownloadFile(remotePath, fileStream, downloadedBytes =>
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new FileTransferProgress
                    {
                        BytesTransferred = downloadedBytes,
                        TotalBytes = totalBytes,
                        Elapsed = stopwatch.Elapsed
                    });
                });
            }

            return Task.CompletedTask;
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
