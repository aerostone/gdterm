using System;
using System.Windows.Forms;
using AxMsTscLib;
using Gdterm.Core.Models;
using Gdterm.Rdp.Models;
using Gdterm.Tunnel.Models;

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 客户端实现——封装 AxMsTscLib ActiveX 控件，支持设备重定向和性能优化
    /// </summary>
    public class RdpClient : IRdpClient
    {
        private AxMsRdpClient8 _rdpControl;
        private UserControl _container;
        private bool _disposed;
        private bool _isViaTunnel;

        public bool IsConnected => _rdpControl?.Connected == 1;

        public UserControl Control => _container;

        public RdpOptions CurrentOptions { get; private set; }

        public event EventHandler<RdpStateChangedEventArgs> StateChanged;
        public event EventHandler<FileTransferEventArgs> FileTransferred;

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

        public void Connect(ConnectionConfig config, CredentialPayload credential, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_rdpControl == null) throw new InvalidOperationException("RDP 控件不可用");

            _isViaTunnel = false;
            CurrentOptions = options ?? new RdpOptions();

            // 应用连接选项
            ApplyOptions(config, credential);

            // 连接
            _rdpControl.Connect();
        }

        public void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, RdpOptions options = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (tunnelEndpoint == null) throw new ArgumentNullException(nameof(tunnelEndpoint));
            if (_rdpControl == null) throw new InvalidOperationException("RDP 控件不可用");

            _isViaTunnel = true;
            CurrentOptions = options ?? new RdpOptions();

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

            // 应用连接选项
            ApplyOptions(config, credential);

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

        /// <summary>
        /// 应用连接选项到 RDP 控件
        /// </summary>
        private void ApplyOptions(ConnectionConfig config, CredentialPayload credential)
        {
            var opts = CurrentOptions;

            // 基本连接参数（非隧道模式）
            if (!_isViaTunnel)
            {
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
            }

            // ===== 设备重定向 =====

            // 磁盘重定向（挂载本地驱动器）
            _rdpControl.AdvancedSettings7.RedirectDrives = opts.RedirectDrives;

            // 剪贴板共享
            _rdpControl.AdvancedSettings7.RedirectClipboard = opts.RedirectClipboard;

            // 打印机重定向
            _rdpControl.AdvancedSettings7.RedirectPrinters = opts.RedirectPrinters;

            // 串口重定向
            _rdpControl.AdvancedSettings7.RedirectPorts = opts.RedirectPorts;

            // 智能卡重定向
            _rdpControl.AdvancedSettings7.RedirectSmartCards = opts.RedirectSmartCards;

            // USB 设备重定向
            _rdpControl.AdvancedSettings7.RedirectDevices = opts.RedirectDevices;

            // ===== 音频 =====
            _rdpControl.AdvancedSettings7.AudioRedirectionMode = (uint)opts.AudioMode;

            // ===== 显示 =====

            // 颜色深度
            _rdpControl.ColorDepth = opts.ColorDepth;

            // 多显示器
            _rdpControl.AdvancedSettings7.UseMultimon = opts.UseMultimon;

            // 全屏
            if (opts.FullScreen)
            {
                _rdpControl.FullScreen = true;
            }

            // ===== 性能 =====

            // 带宽类型（影响自动禁用桌面特性）
            _rdpControl.AdvancedSettings7.BandwidthDetection = true;

            // 桌面壁纸
            _rdpControl.AdvancedSettings7.EnableAutoReconnect = opts.AutoReconnectCount > 0;
            _rdpControl.AdvancedSettings7.MaxReconnectAttempts = opts.AutoReconnectCount;

            // 字体平滑
            _rdpControl.AdvancedSettings7.EnableFontSmoothing = opts.EnableFontSmoothing;

            // 桌面合成
            _rdpControl.AdvancedSettings7.EnableDesktopComposition = opts.EnableDesktopComposition;

            // ===== 连接 =====

            // 超时
            _rdpControl.AdvancedSettings7.singleConnectionTimeout = opts.ConnectionTimeout;

            // 网络级别认证（NLA）
            _rdpControl.AdvancedSettings7.EnableNLA = opts.EnableNLA;

            // CredSSP
            if (opts.EnableCredSSP)
            {
                _rdpControl.AdvancedSettings7.AuthenticationLevel = 2;
            }
        }

        private void OnRdpConnected(object sender, EventArgs e)
        {
            OnStateChanged(new RdpStateChangedEventArgs(true, "connected"));
        }

        private void OnRdpDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e)
        {
            var reason = e.discReason;
            var errorMessage = GetDisconnectReasonMessage(reason);
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

        /// <summary>
        /// 获取断开连接原因的可读消息
        /// </summary>
        private static string GetDisconnectReasonMessage(int reasonCode)
        {
            switch (reasonCode)
            {
                case 0: return "本地初始化断开";
                case 1: return "远程桌面已关闭";
                case 2: return "用户断开连接";
                case 3: return "空闲超时";
                case 4: return "会话超时";
                case 5: return "另一用户连接";
                case 6: return "服务器拒绝连接";
                case 7: return "服务器许可错误";
                case 8: return "服务器内存不足";
                case 9: return "DNS 解析失败";
                case 10: return "网络连接丢失";
                case 11: return "主机连接被拒绝";
                case 12: return "许可证密钥错误";
                case 13: return "加密错误";
                case 14: return "DNS 名称解析失败";
                case 15: return "主机未找到";
                case 16: return "内部错误";
                case 17: return "许可协商超时";
                case 18: return "无法连接网关";
                case 256: return "内部错误 (256)";
                case 257: return "内部错误 (257)";
                case 258: return "内部错误 (258)";
                case 259: return "内部错误 (259)";
                case 260: return "内部错误 (260)";
                case 261: return "内部错误 (261)";
                case 262: return "内部错误 (262)";
                case 263: return "内部错误 (263)";
                case 264: return "内部错误 (264)";
                case 265: return "内部错误 (265)";
                default: return $"断开连接 (原因代码: {reasonCode})";
            }
        }
    }
}
