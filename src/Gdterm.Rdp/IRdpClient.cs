using System;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;
using Gdterm.Tunnel.Models;

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
        /// <param name="config">连接配置</param>
        /// <param name="credential">凭据</param>
        void Connect(ConnectionConfig config, CredentialPayload credential);

        /// <summary>
        /// 通过隧道连接 RDP（跳板模式）
        /// </summary>
        /// <param name="config">连接配置</param>
        /// <param name="credential">凭据</param>
        /// <param name="tunnelEndpoint">隧道接入点（localhost:LocalPort）</param>
        void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint);

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
        /// 连接状态变化事件
        /// </summary>
        event EventHandler<RdpStateChangedEventArgs> StateChanged;
    }
}
