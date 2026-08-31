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
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Forms;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.Security;

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
            {
                // 未解锁时先提示解锁，否则永远 Permission denied (password)
                if (_keepassService != null && !_keepassService.IsUnlocked)
                {
                    try
                    {
                        using (var unlock = new KeePassUnlockForm(_keepassService))
                        {
                            unlock.ShowDialog();
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Swallowed("ProtocolTabOpener.KeePassUnlock", ex);
                    }
                }
                credential = ResolveCredential(config);
                try
                {
                    var hasPwd = credential != null && !string.IsNullOrEmpty(credential.Password);
                    var hasKey = credential != null && credential.SshPrivateKey != null && credential.SshPrivateKey.Length > 0;
                    DiagLog.Info("ProtocolTabOpener.CreateForConnection",
                        "id=" + (config.Id ?? "") +
                        " host=" + (config.Host ?? "") +
                        " keepass=" + (_keepassService != null && _keepassService.IsUnlocked) +
                        " cred=" + (credential != null) +
                        " hasPassword=" + hasPwd +
                        " hasKey=" + hasKey +
                        " user=" + (credential != null ? credential.Username : config.Username));
                }
                catch { }
            }

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

            // --- 抓包代理：连接级自动 dump ---
            // 用户配置的 Host/Port 一律不动（那是用户设置的）；开启抓包时仅在内部
            // 把本次 RDP 连接路由到本地代理：connectConfig 是内部副本（Host/Port 换成
            // 127.0.0.1:代理端口，Metadata 仍共享引用以便运行期元数据回写），
            // session.Config / 审计 / 重连全部继续使用原配置。
            // 注：隧道连接已有本地端点，不再叠加代理。
            ConnectionConfig connectConfig = config;
            bool tcpDump = config != null && config.Metadata != null
                && config.Metadata.TryGetValue("rdp_tcp_dump", out var dumpVal)
                && dumpVal == "true";
            if (tcpDump && config.Tunnel != null)
            {
                DiagLog.Info("RdpTab.Proxy", "连接使用隧道，跳过抓包代理");
                tcpDump = false;
            }
            if (tcpDump)
            {
                try
                {
                    if (RdpDumpProxy.IsRunning)
                    {
                        try { RdpDumpProxy.Stop(); } catch { }
                    }
                    var dumpDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "rdp-dump");
                    var proxyPort = RdpDumpProxy.StartFor(
                        config.Host, config.Port > 0 ? config.Port : 3389, dumpDir);
                    connectConfig = new ConnectionConfig
                    {
                        Id = config.Id,
                        Name = config.Name,
                        Protocol = config.Protocol,
                        Host = "127.0.0.1",
                        Port = proxyPort,
                        Username = config.Username,
                        Domain = config.Domain,
                        GroupPath = config.GroupPath,
                        JumpChain = config.JumpChain,
                        Tunnel = config.Tunnel,
                        CredentialRefId = config.CredentialRefId,
                        Serial = config.Serial,
                        Metadata = config.Metadata
                    };
                    DiagLog.Info("RdpTab.Proxy",
                        string.Format("抓包代理已启动 127.0.0.1:{0} → {1}:{2}, dump → {3}",
                            proxyPort, config.Host, config.Port, dumpDir));
                }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("RdpTab.Proxy.Start", ex);
                    // 代理启动失败时回退到直连（用户配置未动，直接用原配置连）
                    connectConfig = config;
                    tcpDump = false;
                }
            }

            // mstsc 引擎才需要 Windows 凭据（TERMSRV/<host>，供 mstscax 自动登录）。
            // 抓包时用 connectConfig.Host（127.0.0.1）——mstscax 实际连接的地址，
            // 否则凭据目标 TERMSRV/<原host> 与连接目标不匹配，自动登录不生效。
            // FreeRDP 引擎（含默认 auto）不注入：有凭据走 /u /p，无凭据走零凭据首连
            // （mstsc 仿真，堡垒机自渲染登录页）。
            // auto + wfreerdp.exe 缺失时工厂会静默回退 mstscax，仍需注入。
            string engineMeta = null;
            if (config != null && config.Metadata != null)
                config.Metadata.TryGetValue("rdp_engine", out engineMeta);
            string engineSel = (engineMeta ?? "").Trim().ToLowerInvariant();
            bool willUseMstscax = engineSel == "mstscax"
                || (engineSel != "freerdp" && FreeRdpClient.FindExecutable() == null);
            if (willUseMstscax && credential != null && !string.IsNullOrEmpty(credential.Password))
            {
                try
                {
                    _keepassService?.InjectRdpCredential(
                        connectConfig.Host, credential.Username, credential.Password);
                }
                catch { }
            }

            var rdp = _rdpFactory.CreateFor(connectConfig);
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

            // 零凭据不再回退 mstsc（v0.1.154 实证）：无 /u /p 时 wfreerdp 发 AUTOLOGON=0
            // 空凭据 client info（mstsc 仿真），堡垒机自渲染带内登录页，密码在页内输入。
            // 这是「零配置自协商」原则的一部分：凭据有无不应决定引擎选择。
            // 显式 rdp_engine=mstscax 时 RdpClientFactory 已直接返回 ActiveX，不至此分支。
            // 注：显式 freerdp 已在 RdpClientFactory 处理 exe 缺失（直接抛错），不会静默换引擎。

            // 订阅状态事件：断开/致命错误时更新 tab 状态并向用户报错（旧代码从不订阅，
            // 导致断开后 tab 假死空白）。事件来自 COM 连接点，稳妥起见弹回 UI 线程。
            rdp.StateChanged += (sender, ev) =>
            {
                var ctrl = rdp.Control;
                Action a = () =>
                {
                    try
                    {
                        if (ev.IsConnected)
                        {
                            session.IsConnected = true;
                            DiagLog.Info("RdpTab.StateChanged", "connected");
                            return;
                        }
                        session.IsConnected = false;
                        DiagLog.Info("RdpTab.StateChanged", "disconnected reason=" + ev.Reason + " msg=" + ev.ErrorMessage);
                        // 抓包代理：连接断开时自动停止
                        if (tcpDump) { try { RdpDumpProxy.Stop(); } catch { } }
                        if (ev.Reason == "closed") return;
                        if (tab.IsDisposed) return;
                        MessageBox.Show("RDP 已断开: " + (ev.ErrorMessage ?? ev.Reason), "远程桌面",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch (Exception ex2) { DiagLog.Swallowed("RdpTab.StateChanged", ex2); }
                };
                try
                {
                    if (ctrl != null && ctrl.IsHandleCreated && ctrl.InvokeRequired)
                        ctrl.BeginInvoke(a);
                    else
                        a();
                }
                catch (Exception ex3) { DiagLog.Swallowed("RdpTab.StateChanged", ex3); }
            };

            session.PendingConnect = () =>
            {
                try
                {
                    DiagLog.Info("RdpTab.Connect", "host=" + config.Host + " tunnel=" + (config.Tunnel != null) + " useFreeRdp=" + (rdp is FreeRdpClient));
                    if (config.Tunnel != null && _tunnelManager != null)
                    {
                        var tunnel = _tunnelManager.EstablishAsync(config, credential,
                            System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                        rdp.ConnectViaTunnel(connectConfig, credential, tunnel, options);
                    }
                    else
                    {
                        // 抓包时 connectConfig 指向本地代理；FreeRDP 的 /load-balance-info
                        // 等选项已在 options（RdpOptionsBuilder.FromConnection(config)）里，不受影响
                        rdp.Connect(connectConfig, credential, options);
                    }
                    DiagLog.Info("RdpTab.Connect", "Connect() returned, connected=" + SafeIsConnected(rdp));
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
                    DiagLog.Swallowed("RdpTab.Connect", ex);
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
            // 本地终端构造时已 Attach；再 Resume 确保 canvas 可输入并补启 shell
            try { terminal.ResumeRendering(); } catch { }
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
                IsConnected = local != null && local.IsConnected,
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

        private static string SafeIsConnected(Gdterm.Rdp.IRdpClient rdp)
        {
            try { return rdp != null && rdp.IsConnected ? "true" : "false"; }
            catch { return "unknown"; }
        }
    }
}
