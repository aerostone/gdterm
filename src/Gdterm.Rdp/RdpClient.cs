using System;
using System.Windows.Forms;
using AxMsTscLib;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;
using Gdterm.Tunnel.Models;

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 客户端实现——封装 AxMsTscLib ActiveX 控件
    /// </summary>
    public class RdpClient : IRdpClient
    {
        private AxMsRdpClient8 _rdpControl;
        private UserControl _container;
        private bool _disposed;
        private bool _isViaTunnel;

        public bool IsConnected => _rdpControl?.Connected == 1;

        public UserControl Control => _container;

        public event EventHandler<RdpStateChangedEventArgs> StateChanged;

        public RdpClient()
        {
            // 创建承载 ActiveX 控件的 UserControl
            _container = new UserControl();
            _container.Dock = DockStyle.Fill;

            try
            {
                // 创建 RDP ActiveX 控件
                _rdpControl = new AxMsRdpClient8();
                ((System.ComponentModel.ISupportInitialize)_rdpControl).BeginInit();
                _rdpControl.Dock = DockStyle.Fill;
                _container.Controls.Add(_rdpControl);
                ((System.ComponentModel.ISupportInitialize)_rdpControl).EndInit();

                // 绑定事件
                _rdpControl.OnConnected += OnRdpConnected;
                _rdpControl.OnDisconnected += OnRdpDisconnected;
                _rdpControl.OnLoginComplete += OnRdpLoginComplete;
            }
            catch
            {
                // AxMsTscLib 可能不可用（非 Windows 环境）
                // 创建一个占位 Label
                var label = new Label
                {
                    Text = "RDP 控件不可用（需要 Windows 环境）",
                    Dock = DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter
                };
                _container.Controls.Add(label);
            }
        }

        public void Connect(ConnectionConfig config, CredentialPayload credential)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_rdpControl == null) throw new InvalidOperationException("RDP 控件不可用");

            _isViaTunnel = false;

            // 配置连接参数
            _rdpControl.Server = config.Host;
            _rdpControl.UserName = credential?.Username ?? config.Username ?? "";

            if (!string.IsNullOrEmpty(credential?.Password))
            {
                _rdpControl.AdvancedSettings7.ClearTextPassword = credential.Password;
            }

            if (config.Port > 0 && config.Port != 3389)
            {
                _rdpControl.AdvancedSettings7.RDPPort = config.Port;
            }

            if (!string.IsNullOrEmpty(config.Domain))
            {
                _rdpControl.Domain = config.Domain;
            }

            // 连接
            _rdpControl.Connect();
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));
            if (_rdpControl == null) throw new InvalidOperationException("RDP 控件不可用");

            _isViaTunnel = true;

            // 通过隧道连接 localhost:LocalPort
            _rdpControl.Server = tunnelEndpoint.LocalHost ?? "127.0.0.1";
            _rdpControl.UserName = credential?.Username ?? config.Username ?? "";

            if (!string.IsNullOrEmpty(credential?.Password))
            {
                _rdpControl.AdvancedSettings7.ClearTextPassword = credential.Password;
            }

            _rdpControl.AdvancedSettings7.RDPPort = tunnelEndpoint.LocalPort;

            if (!string.IsNullOrEmpty(config.Domain))
            {
                _rdpControl.Domain = config.Domain;
            }

            // 连接
            _rdpControl.Connect();
        }

        public void Disconnect()
        {
            if (_rdpControl != null && _rdpControl.Connected == 1)
            {
                _rdpControl.Disconnect();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();

            _rdpControl?.Dispose();
            _container?.Dispose();
        }

        private void OnRdpConnected(object sender, EventArgs e)
        {
            OnStateChanged(new RdpStateChangedEventArgs(true, "connected"));
        }

        private void OnRdpDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e)
        {
            var reason = e.discReason;
            var errorMessage = $"断开连接 (原因代码: {reason})";
            OnStateChanged(new RdpStateChangedEventArgs(false, "disconnected", errorMessage));
        }

        private void OnRdpLoginComplete(object sender, EventArgs e)
        {
            OnStateChanged(new RdpStateChangedEventArgs(true, "connected"));
        }

        private void OnStateChanged(RdpStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }
    }
}
