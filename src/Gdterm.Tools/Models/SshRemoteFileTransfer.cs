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
                _sftp = new SftpClient(ci.Host, ci.Port, ci.Username, new PrivateKeyFile[] { });
                // 使用与 SSH 相同的认证
                _sftp = new SftpClient(ci);
                _sftp.Connect();
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
