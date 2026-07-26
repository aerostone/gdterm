using System;
using System.Windows.Forms;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Rdp;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 协议标签建造器——SSH/RDP/Serial/本地/SFTP 的控件与会话状态组装（finding-10）。
    /// TabContainer 只负责把 OpenedTab 挂进 TabControl 与字典。
    /// </summary>
    public sealed class ProtocolTabOpener
    {
        private readonly ITunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly IRdpClientFactory _rdpFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IAuditLogger _auditLogger;
        private readonly IKeePassService _keepassService;
        private readonly DangerousCommandDetector _dangerousDetector;
        private readonly CredentialResolver _credentialResolver;

        /// <summary>
        /// 终端连接成功后回调：参数为 (page, terminal, config)。
        /// 由 TabContainer 注入，用于更新 IsConnected / 健康监控 / 登录脚本。
        /// </summary>
        public Action<TabPage, TerminalControl, ConnectionConfig> OnTerminalConnected { get; set; }

        /// <summary>
        /// RDP PendingConnect 成功后把对应 TabSessionState.IsConnected 置 true。
        /// 由 TabContainer 注入，因字典归属 TabContainer。
        /// </summary>
        public Action<TabPage> OnRdpConnected { get; set; }

        public ProtocolTabOpener(
            ITunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            IRdpClientFactory rdpFactory,
            ISftpServiceFactory sftpFactory,
            IAuditLogger auditLogger,
            IKeePassService keepassService,
            DangerousCommandDetector dangerousDetector,
            CredentialResolver credentialResolver)
        {
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _rdpFactory = rdpFactory ?? new RdpClientFactory();
            _sftpFactory = sftpFactory;
            _auditLogger = auditLogger;
            _keepassService = keepassService;
            _dangerousDetector = dangerousDetector;
            _credentialResolver = credentialResolver;
        }

        public CredentialPayload ResolveCredential(ConnectionConfig config)
        {
            return _credentialResolver != null
                ? _credentialResolver.Resolve(config)
                : null;
        }

        /// <summary>按协议创建标签；不支持的协议返回 null 并已弹 MessageBox。</summary>
        public OpenedTab CreateForConnection(ConnectionConfig config)
        {
            if (config == null) return null;

            CredentialPayload credential = null;
            if (config.Protocol == ProtocolType.SSH || config.Protocol == ProtocolType.RDP)
                credential = ResolveCredential(config);

            switch (config.Protocol)
            {
                case ProtocolType.SSH:
                    return CreateSsh(config, credential);
                case ProtocolType.RDP:
                    return CreateRdp(config, credential);
                case ProtocolType.Serial:
                    return CreateSerial(config);
                default:
                    MessageBox.Show("不支持的协议: " + config.Protocol, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return null;
            }
        }

        public OpenedTab CreateSsh(ConnectionConfig config, CredentialPayload credential)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = (config.Username ?? "") + "@" + config.Host + ":" + config.Port
            };

            var terminalControl = new TerminalControl(
                config, _terminalFactory, _tunnelManager, _auditLogger, _dangerousDetector);
            terminalControl.Dock = DockStyle.Fill;
            terminalControl.Credentials = credential;
            terminalControl.SessionConnected += (s, e) =>
            {
                OnTerminalConnected?.Invoke(tab, terminalControl, config);
            };
            tab.Controls.Add(terminalControl);

            var session = new TabSessionState
            {
                Config = config,
                Control = terminalControl,
                PrimaryTerminal = terminalControl,
                Protocol = ProtocolType.SSH,
                IsConnected = false,
                Credential = credential,
                SessionId = config.Id ?? Guid.NewGuid().ToString("N")
            };

            return new OpenedTab { Page = tab, Session = session };
        }

        public OpenedTab CreateRdp(ConnectionConfig config, CredentialPayload credential)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = "RDP: " + config.Host + ":" + config.Port
            };

            if (credential != null && !string.IsNullOrEmpty(credential.Password))
            {
                try
                {
                    _keepassService?.InjectRdpCredential(
                        config.Host, credential.Username, credential.Password);
                }
                catch { }
            }

            var rdp = _rdpFactory.Create();
            rdp.Control.Dock = DockStyle.Fill;
            tab.Controls.Add(rdp.Control);

            var options = RdpOptionsBuilder.FromConnection(config);

            var session = new TabSessionState
            {
                Config = config,
                Control = rdp.Control,
                Protocol = ProtocolType.RDP,
                IsConnected = false,
                Credential = credential,
                RdpClient = rdp,
                SessionId = config.Id ?? Guid.NewGuid().ToString("N")
            };

            session.PendingConnect = () =>
            {
                try
                {
                    if (config.Tunnel != null && _tunnelManager != null)
                    {
                        var tunnel = _tunnelManager.EstablishAsync(config, credential,
                            System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                        rdp.ConnectViaTunnel(config, credential, tunnel, options);
                    }
                    else
                    {
                        rdp.Connect(config, credential, options);
                    }
                    OnRdpConnected?.Invoke(tab);
                    try
                    {
                        _auditLogger?.LogConnection(
                            config.Id,
                            config.Host ?? config.Name,
                            ProtocolType.RDP.ToString(),
                            ConnectionAction.Open);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        _auditLogger?.LogConnection(
                            config.Id,
                            config.Host ?? config.Name,
                            ProtocolType.RDP.ToString(),
                            ConnectionAction.Error);
                    }
                    catch { }
                    MessageBox.Show("RDP 连接失败: " + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            return new OpenedTab { Page = tab, Session = session };
        }

        public OpenedTab CreateSerial(ConnectionConfig config)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = "Serial: " + (config.Serial?.PortName ?? "Unknown")
            };

            var terminalControl = new TerminalControl(
                config, _terminalFactory, _tunnelManager, _auditLogger, _dangerousDetector);
            terminalControl.Dock = DockStyle.Fill;
            terminalControl.SessionConnected += (s, e) =>
            {
                OnTerminalConnected?.Invoke(tab, terminalControl, config);
            };
            tab.Controls.Add(terminalControl);

            var session = new TabSessionState
            {
                Config = config,
                Control = terminalControl,
                PrimaryTerminal = terminalControl,
                Protocol = ProtocolType.Serial,
                IsConnected = false,
                SessionId = config.Id ?? Guid.NewGuid().ToString("N")
            };

            return new OpenedTab { Page = tab, Session = session };
        }

        public OpenedTab CreateLocal(string shellPath = null)
        {
            if (_terminalFactory == null)
                throw new InvalidOperationException("ITerminalSessionFactory 未注入，无法创建本地终端");

            var local = _terminalFactory.CreateLocal(shellPath);
            var tab = new TabPage("本地终端")
            {
                ToolTipText = "本地 Shell"
            };

            var terminal = new TerminalControl(local, _auditLogger);
            terminal.Dock = DockStyle.Fill;
            tab.Controls.Add(terminal);

            var session = new TabSessionState
            {
                Config = new ConnectionConfig
                {
                    Id = "local-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = "本地终端",
                    Host = "localhost",
                    Protocol = ProtocolType.SSH
                },
                Control = terminal,
                PrimaryTerminal = terminal,
                Protocol = ProtocolType.SSH,
                IsConnected = true,
                SessionId = Guid.NewGuid().ToString("N")
            };

            return new OpenedTab { Page = tab, Session = session };
        }

        public OpenedTab CreateSftp(ConnectionConfig config)
        {
            if (config == null) return null;

            var credential = ResolveCredential(config)
                ?? new CredentialPayload { Username = config.Username };
            var tab = new TabPage("SFTP: " + config.Name)
            {
                ToolTipText = "sftp://" + config.Host
            };

            var panel = new SftpBrowserPanel(config, credential, _sftpFactory, _tunnelManager);
            panel.Dock = DockStyle.Fill;
            tab.Controls.Add(panel);

            var session = new TabSessionState
            {
                Config = config,
                Control = panel,
                Protocol = ProtocolType.SSH,
                IsConnected = false,
                Credential = credential,
                SessionId = config.Id + "-sftp"
            };

            return new OpenedTab { Page = tab, Session = session };
        }

        /// <summary>分屏时创建第二个终端控件（不建独立 TabSession）。</summary>
        public TerminalControl CreateSplitTerminal(ConnectionConfig config, CredentialPayload credential)
        {
            var newTerminal = new TerminalControl(
                config, _terminalFactory, _tunnelManager, _auditLogger, _dangerousDetector);
            newTerminal.Credentials = credential;
            newTerminal.Dock = DockStyle.Fill;
            newTerminal.ResumeRendering();
            return newTerminal;
        }
    }
}
