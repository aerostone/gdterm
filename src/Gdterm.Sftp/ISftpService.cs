using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Sftp.Models;

namespace Gdterm.Sftp
{
    /// <summary>
    /// SFTP 文件服务接口——提供远程文件浏览和传输能力
    /// </summary>
    /// <remarks>
    /// 直连模式：直接连接 config.Host:config.Port
    /// 跳板模式：通过 ITunnelManager 建立端口转发后连接 localhost:forwarded_port
    /// </remarks>
    public interface ISftpService : IDisposable
    {
        /// <summary>
        /// 连接到 SFTP 服务器
        /// </summary>
        Task ConnectAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct);

        /// <summary>
        /// 通过隧道连接到 SFTP 服务器
        /// </summary>
        Task ConnectViaTunnelAsync(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, CancellationToken ct);

        /// <summary>
        /// 列出目录内容（目录优先 + 名称排序）
        /// </summary>
        Task<IList<SftpFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct);

        /// <summary>
        /// 上传文件
        /// </summary>
        Task UploadAsync(string localPath, string remotePath, IProgress<FileTransferProgress> progress, CancellationToken ct);

        /// <summary>
        /// 下载文件
        /// </summary>
        Task DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress> progress, CancellationToken ct);

        /// <summary>
        /// 删除文件或目录
        /// </summary>
        Task DeleteAsync(string remotePath, bool recursive, CancellationToken ct);

        /// <summary>
        /// 创建目录
        /// </summary>
        Task CreateDirectoryAsync(string remotePath, CancellationToken ct);

        /// <summary>
        /// 重命名/移动文件或目录
        /// </summary>
        Task RenameAsync(string oldPath, string newPath, CancellationToken ct);

        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }
    }
}
