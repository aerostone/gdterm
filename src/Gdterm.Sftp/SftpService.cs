using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.Sftp.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Gdterm.Sftp
{
    /// <summary>SFTP 会话串行执行操作；取消、断开与释放均撤销当前会话。</summary>
    public class SftpService : ISftpService
    {
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _operations = new SemaphoreSlim(1, 1);
        private SftpClient _client;
        private CancellationTokenSource _session = new CancellationTokenSource();
        private bool _disposed;

        public bool IsConnected
        {
            get { lock (_sync) return !_disposed && _client != null && _client.IsConnected; }
        }

        public Task ConnectAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            return ConnectCoreAsync(config, credential, null, ct);
        }

        public Task ConnectViaTunnelAsync(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint endpoint, CancellationToken ct)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            return ConnectCoreAsync(config, credential, endpoint, ct);
        }

        private async Task ConnectCoreAsync(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint endpoint, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            CancellationToken sessionToken;
            lock (_sync) { ThrowIfDisposed(); sessionToken = _session.Token; }
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken))
            {
                await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
                SftpClient candidate = null;
                try
                {
                    // Key parsing and handshake must not run on the WinForms thread.
                    await Task.Run(async () =>
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        var info = Gdterm.Tunnel.SshConnectionInfoFactory.Create(
                            endpoint == null ? config.Host : endpoint.LocalHost,
                            endpoint == null ? config.Port : endpoint.LocalPort,
                            credential.Username ?? config.Username, credential, 30,
                            endpoint == null ? config.Proxy : null);
                        candidate = Gdterm.Tunnel.SshHostKeyGuard.CreateSftpClient(info, config.Host, config.Port);
                        candidate.OperationTimeout = TimeSpan.FromSeconds(30);
                        Gdterm.Tunnel.SshKeepAlive.Apply(candidate, config);
                        using (linked.Token.Register(() => SafeDispose(candidate)))
                            await candidate.ConnectAsync(linked.Token).ConfigureAwait(false);
                        lock (_sync)
                        {
                            ThrowIfDisposed();
                            linked.Token.ThrowIfCancellationRequested();
                            if (_client != null) throw new InvalidOperationException(L10n.T("SFTP 已连接"));
                            _client = candidate;
                            candidate = null;
                        }
                    }, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (candidate != null) throw Gdterm.Tunnel.SshHostKeyGuard.Describe(candidate, ex);
                    throw;
                }
                finally { SafeDispose(candidate); _operations.Release(); }
            }
        }

        private async Task<T> RunAsync<T>(Func<SftpClient, CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            SftpClient client;
            CancellationToken sessionToken;
            lock (_sync)
            {
                ThrowIfDisposed();
                client = _client;
                sessionToken = _session.Token;
            }
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken))
            {
                await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (client == null || !client.IsConnected)
                        throw new InvalidOperationException(L10n.T("SFTP 未连接，请先调用 ConnectAsync"));
                    // Metadata APIs are synchronous. Closing this session interrupts blocked requests too.
                    using (linked.Token.Register(() => SafeDispose(client)))
                        return await Task.Run(() => operation(client, linked.Token), linked.Token).ConfigureAwait(false);
                }
                catch { linked.Token.ThrowIfCancellationRequested(); throw; }
                finally { _operations.Release(); }
            }
        }

        public Task<IList<SftpFileInfo>> ListDirectoryAsync(string path, CancellationToken ct)
        {
            return RunAsync<IList<SftpFileInfo>>((client, token) =>
            {
                var result = new List<SftpFileInfo>();
                foreach (var entry in client.ListDirectory(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Name != "." && entry.Name != "..") result.Add(Map(entry));
                }
                return Task.FromResult<IList<SftpFileInfo>>(result.OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());
            }, ct);
        }

        public Task<SftpFileInfo> GetEntryAsync(string path, CancellationToken ct)
        {
            return RunAsync((client, token) =>
            {
                if (path == "/") return Task.FromResult(new SftpFileInfo { Name = "/", FullPath = "/", IsDirectory = true });
                var entry = FindEntry(client, path, token);
                return Task.FromResult(entry == null ? null : Map(entry));
            }, ct);
        }

        // Get() canonicalizes and follows links before lstat. Locate the directory entry instead.
        private static ISftpFile FindEntry(SftpClient client, string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException(nameof(path));
            var clean = path.TrimEnd('/');
            var index = clean.LastIndexOf('/');
            var parent = index < 0 ? "." : index == 0 ? "/" : clean.Substring(0, index);
            var name = clean.Substring(index + 1);
            if (name == "." || name == ".." || name.Length == 0) throw new ArgumentException(nameof(path));
            foreach (var entry in client.ListDirectory(parent))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name == name) return entry;
            }
            return null;
        }

        private static ISftpFile RequireEntry(SftpClient client, string path, CancellationToken ct)
        {
            return FindEntry(client, path, ct) ?? throw new SftpPathNotFoundException(path);
        }

        private static SftpFileInfo Map(ISftpFile e)
        {
            return new SftpFileInfo { Name = e.Name, FullPath = e.FullName, IsDirectory = e.IsDirectory,
                IsSymbolicLink = e.IsSymbolicLink, SizeBytes = (long)e.Length, LastModified = e.LastWriteTime,
                Permissions = ToRwx(e), Owner = e.UserId.ToString(), Group = e.GroupId.ToString() };
        }

        internal static string ToRwx(ISftpFile f)
        {
            return new string(new[] { f.OwnerCanRead ? 'r' : '-', f.OwnerCanWrite ? 'w' : '-', f.OwnerCanExecute ? 'x' : '-',
                f.GroupCanRead ? 'r' : '-', f.GroupCanWrite ? 'w' : '-', f.GroupCanExecute ? 'x' : '-',
                f.OthersCanRead ? 'r' : '-', f.OthersCanWrite ? 'w' : '-', f.OthersCanExecute ? 'x' : '-' });
        }

        private static void RequireRegularFile(ISftpFile entry)
        {
            if (entry != null && !entry.IsRegularFile)
                throw new IOException(L10n.T("传输仅支持普通文件，不能跟随符号链接。"));
        }

        public Task UploadAsync(string local, string remote, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            return TransferAsync(local, remote, true, false, progress, ct);
        }
        public Task DownloadAsync(string remote, string local, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            return TransferAsync(local, remote, false, false, progress, ct);
        }
        public Task<bool> UploadResumeAsync(string local, string remote, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            return TransferAsync(local, remote, true, true, progress, ct);
        }
        public Task<bool> DownloadResumeAsync(string remote, string local, IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            return TransferAsync(local, remote, false, true, progress, ct);
        }

        private Task<bool> TransferAsync(string localPath, string remotePath, bool upload, bool resume,
            IProgress<FileTransferProgress> progress, CancellationToken ct)
        {
            return RunAsync(async (client, token) =>
            {
                SftpPathSafety.EnsureNoReparsePoints(localPath);
                var entry = FindEntry(client, remotePath, token);
                RequireRegularFile(entry);
                if (!upload && entry == null) throw new SftpPathNotFoundException(remotePath);
                using (var local = new FileStream(localPath, upload ? FileMode.Open : FileMode.OpenOrCreate,
                    upload ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None, 65536, true))
                using (var remote = await client.OpenAsync(remotePath,
                    upload ? FileMode.OpenOrCreate : FileMode.Open,
                    upload ? (resume ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read, token).ConfigureAwait(false))
                {
                    Stream source = upload ? (Stream)local : remote;
                    Stream target = upload ? (Stream)remote : local;
                    var total = source.Length;
                    var offset = resume ? target.Length : 0;
                    if (resume)
                    {
                        if (offset > total) throw new IOException(L10n.T("续传失败：目标内容不是源文件的前缀，请选择覆盖。"));
                        await VerifyPrefixAsync(source, target, offset, token).ConfigureAwait(false);
                        if (offset == total)
                        {
                            progress?.Report(new FileTransferProgress { BytesTransferred = total, TotalBytes = total });
                            return false;
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!resume) target.SetLength(0);
                    source.Position = target.Position = offset;
                    var watch = Stopwatch.StartNew();
                    var buffer = new byte[65536];
                    long done = offset;
                    var lastReport = TimeSpan.Zero;
                    progress?.Report(new FileTransferProgress { BytesTransferred = done, TotalBytes = total });
                    int count;
                    while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        done += count;
                        if (watch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(100))
                        {
                            progress?.Report(new FileTransferProgress { BytesTransferred = done, TotalBytes = total, Elapsed = watch.Elapsed });
                            lastReport = watch.Elapsed;
                        }
                    }
                    await target.FlushAsync(token).ConfigureAwait(false);
                    progress?.Report(new FileTransferProgress { BytesTransferred = done, TotalBytes = total, Elapsed = watch.Elapsed });
                    return true;
                }
            }, ct);
        }

        private static async Task VerifyPrefixAsync(Stream source, Stream target, long length, CancellationToken ct)
        {
            var a = new byte[65536];
            var b = new byte[65536];
            while (length > 0)
            {
                int count = (int)Math.Min(a.Length, length);
                await ReadExactlyAsync(source, a, count, ct).ConfigureAwait(false);
                await ReadExactlyAsync(target, b, count, ct).ConfigureAwait(false);
                for (int i = 0; i < count; i++)
                    if (a[i] != b[i]) throw new IOException(L10n.T("续传失败：目标内容不是源文件的前缀，请选择覆盖。"));
                length -= count;
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        /// <summary>读取最多 maxBytes 字节；预览不下载完整文件或使用临时文件。</summary>
        public Task<byte[]> ReadFilePrefixAsync(string path, int maxBytes, CancellationToken ct)
        {
            if (maxBytes < 1 || maxBytes > 8 * 1024 * 1024 + 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            return RunAsync(async (client, token) =>
            {
                RequireRegularFile(RequireEntry(client, path, token));
                using (var stream = await client.OpenAsync(path, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[Math.Min(8192, maxBytes)];
                    while (output.Length < maxBytes)
                    {
                        int count = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - output.Length), token).ConfigureAwait(false);
                        if (count == 0) break;
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
            }, ct);
        }

        public Task DeleteAsync(string path, bool recursive, CancellationToken ct)
        {
            return RunAsync(async (client, token) =>
            {
                await DeleteEntryAsync(client, RequireEntry(client, path, token), recursive, token).ConfigureAwait(false);
                return true;
            }, ct);
        }
        private static async Task DeleteEntryAsync(SftpClient client, ISftpFile entry, bool recursive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory && !entry.IsSymbolicLink && recursive)
                foreach (var child in client.ListDirectory(entry.FullName).Where(e => e.Name != "." && e.Name != "..").ToList())
                    await DeleteEntryAsync(client, child, true, ct).ConfigureAwait(false);
            await entry.DeleteAsync(ct).ConfigureAwait(false);
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct)
        {
            return RunAsync((client, token) => { token.ThrowIfCancellationRequested(); client.CreateDirectory(path); return Task.FromResult(true); }, ct);
        }
        public Task RenameAsync(string oldPath, string newPath, CancellationToken ct)
        {
            return RunAsync((client, token) =>
            {
                var entry = RequireEntry(client, oldPath, token);
                if (FindEntry(client, newPath, token) != null) throw new IOException(L10n.T("目标已存在"));
                token.ThrowIfCancellationRequested();
                entry.MoveTo(newPath);
                return Task.FromResult(true);
            }, ct);
        }
        public Task ChmodAsync(string path, int octalMode, CancellationToken ct)
        {
            if (octalMode < 0 || octalMode > 777 || octalMode / 100 > 7 || octalMode / 10 % 10 > 7 || octalMode % 10 > 7)
                throw new ArgumentOutOfRangeException(nameof(octalMode), L10n.T("权限位须为 000-777（如 755）。"));
            return RunAsync((client, token) =>
            {
                var entry = RequireEntry(client, path, token);
                if (entry.IsSymbolicLink) throw new IOException(L10n.T("不能修改符号链接的权限。"));
                token.ThrowIfCancellationRequested();
                client.ChangePermissions(path, (short)octalMode);
                return Task.FromResult(true);
            }, ct);
        }

        public void Disconnect() { Close(false); }
        public void Dispose() { Close(true); }
        private void Close(bool disposing)
        {
            SftpClient client;
            CancellationTokenSource session;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = disposing;
                client = _client;
                _client = null;
                session = _session;
                if (!disposing) _session = new CancellationTokenSource();
            }
            session.Cancel();
            session.Dispose();
            SafeDispose(client);
        }
        private static void SafeDispose(SftpClient client)
        {
            try { client?.Dispose(); } catch (Exception) { /* Cancellation/close is best effort. */ }
        }
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SftpService));
        }
    }
}
