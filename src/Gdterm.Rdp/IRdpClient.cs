using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;
// TunnelEndpoint 在 Core.Models

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 客户端接口——提供远程桌面连接能力
    /// </summary>
    public interface IRdpClient : IDisposable
    {
        /// <summary>
        /// 直连 RDP 会话
        /// </summary>
        void Connect(ConnectionConfig config, CredentialPayload credential, RdpOptions options = null);

        /// <summary>
        /// 通过隧道连接 RDP（跳板模式）
        /// </summary>
        void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, RdpOptions options = null);

        /// <summary>
        /// 断开连接
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 获取承载 ActiveX 控件的 UserControl（UI 嵌入用）
        /// </summary>
        UserControl Control { get; }

        /// <summary>
        /// 当前连接选项（连接后可读取）
        /// </summary>
        RdpOptions CurrentOptions { get; }

        /// <summary>
        /// 连接状态变化事件
        /// </summary>
        event EventHandler<RdpStateChangedEventArgs> StateChanged;

        /// <summary>
        /// 文件拖放事件（当文件通过剪贴板传输时触发）
        /// </summary>
        event EventHandler<FileTransferEventArgs> FileTransferred;
    }
}
