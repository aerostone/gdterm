using System;
using System.IO;
using Renci.SshNet;

namespace Gdterm.Tools.Models
{
    /// <summary>
    /// SSH 文件传输实现
    /// </summary>
    public class SshRemoteFileTransfer : IRemoteFileTransfer
    {
        private readonly SshClient _sshClient;
        private SftpClient _sftp;

        public SshRemoteFileTransfer(SshClient sshClient)
        {
            _sshClient = sshClient ?? throw new ArgumentNullException("sshClient");
        }

        private SftpClient GetSftp()
        {
            if (_sftp == null || !_sftp.IsConnected)
            {
                _sftp?.Dispose();
                var ci = _sshClient.ConnectionInfo;
                // 用与终端相同的认证，并钉同一份主机密钥：SFTP 是另一条独立握手，
                // 不挂校验就等于给了一条绕过 P0 的后门（原实现还有一行立即被覆盖的裸建连，一并删掉）。
                _sftp = Gdterm.Tunnel.SshHostKeyGuard.CreateSftpClient(ci, ci.Host, ci.Port);
                try { _sftp.Connect(); }
                catch (Exception ex) { throw Gdterm.Tunnel.SshHostKeyGuard.Describe(_sftp, ex); }
            }
            return _sftp;
        }

        public string UploadToTemp(string localPath)
        {
            var fileName = Path.GetFileName(localPath);
            var remotePath = "/tmp/gdterm_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + fileName;
            var sftp = GetSftp();
            using (var fs = File.OpenRead(localPath))
            {
                sftp.UploadFile(fs, remotePath);
            }
            return remotePath;
        }

        public string UploadToTemp(byte[] content, string remoteFileName)
        {
            var remotePath = "/tmp/gdterm_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + remoteFileName;
            var sftp = GetSftp();
            using (var ms = new MemoryStream(content))
            {
                sftp.UploadFile(ms, remotePath);
            }
            return remotePath;
        }

        public void CleanupTemp(string remotePath)
        {
            try
            {
                var sftp = GetSftp();
                if (sftp.Exists(remotePath))
                    sftp.DeleteFile(remotePath);
            }
            catch { /* 清理失败忽略 */ }
        }

        public void DownloadToFile(string remotePath, string localPath)
        {
            var sftp = GetSftp();
            using (var fs = File.Create(localPath))
            {
                sftp.DownloadFile(remotePath, fs);
            }
        }

        public void Dispose()
        {
            _sftp?.Dispose();
        }
    }
}
