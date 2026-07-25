using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Rdp;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 标签页容器——SSH/RDP/串口/本地/SFTP，懒连接、暂停渲染、自动重连、健康监控
    /// </summary>
    public class TabContainerControl : UserControl
    {
        private readonly ITunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly IRdpClientFactory _rdpFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IAiAssistantService _aiService;
        private readonly IAuditLogger _auditLogger;
        private readonly IKeePassService _keepassService;
        private readonly IFolderCredentialStore _folderCredStore;
        private readonly CredentialResolver _credentialResolver;
        private readonly DangerousCommandDetector _dangerousDetector;
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly IConnectionStore _connectionStore;
        private readonly TabSessionLifecycle _lifecycle;
        private readonly Dictionary<TabPage, TabSession> _sessions = new Dictionary<TabPage, TabSession>();
        private TabControl _tabControl;

        /// <summary>活动标签变化</summary>
        public event EventHandler ActiveSessionChanged;

        /// <summary>标签关闭时触发（参数为 SessionId，供多通道注销等）</summary>
        public event EventHandler<string> SessionClosed;

        public TabContainerControl(
            ITunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IAiAssistantService aiService,
            IAuditLogger auditLogger,
            IKeePassService keepassService,
            IFolderCredentialStore folderCredStore,
            DangerousCommandDetector dangerousDetector = null,
            AutoReconnectWatchdog reconnectWatchdog = null,
            IConnectionStore connectionStore = null,
            IRdpClientFactory rdpFactory = null)
        {
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _rdpFactory = rdpFactory ?? new RdpClientFactory();
            _sftpFactory = sftpFactory;
            _aiService = aiService;
            _auditLogger = auditLogger;
            _keepassService = keepassService;
            _folderCredStore = folderCredStore;
            _credentialResolver = new CredentialResolver(keepassService, folderCredStore);
            _dangerousDetector = dangerousDetector;
            _reconnectWatchdog = reconnectWatchdog;
            _connectionStore = connectionStore;
            _lifecycle = new TabSessionLifecycle(auditLogger, reconnectWatchdog);

            if (_reconnectWatchdog != null)
            {
                _reconnectWatchdog.DefaultReconnectFunc = async (id, session) =>
                {
                    if (InvokeRequired)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        BeginInvoke(new Action(() =>
                        {
                            try { tcs.SetResult(ReconnectByIdSync(id)); }
                            catch (Exception ex) { tcs.SetException(ex); }
                        }));
                        return await tcs.Task;
                    }
                    return ReconnectByIdSync(id);
                };
            }

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(120, 24),
                Padding = new Point(10, 3)
            };
            _tabControl.DrawItem += OnDrawTab;
            _tabControl.MouseDown += OnTabMouseDown;
            _tabControl.SelectedIndexChanged += OnTabSelectedIndexChanged;
            Controls.Add(_tabControl);
        }

        public void OpenConnection(ConnectionConfig config)
        {
            if (config == null) return;

            foreach (TabPage existingTab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(existingTab, out var session) &&
                    session.Config?.Id == config.Id)
                {
                    _tabControl.SelectedTab = existingTab;
                    return;
                }
            }

            CredentialPayload credential = null;
            if (config.Protocol == ProtocolType.SSH || config.Protocol == ProtocolType.RDP)
                credential = ResolveCredential(config);

            TabPage tab;
            switch (config.Protocol)
            {
                case ProtocolType.SSH:
                    tab = CreateSshTerminalTab(config, credential);
                    break;
                case ProtocolType.RDP:
                    tab = CreateRdpTab(config, credential);
                    break;
                case ProtocolType.Serial:
                    tab = CreateSerialTab(config);
                    break;
                default:
                    MessageBox.Show("不支持的协议: " + config.Protocol, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
            }

            _tabControl.TabPages.Add(tab);
            _tabControl.SelectedTab = tab;
            // 懒连接：真实 Open/Error 由 TerminalControl 或 RDP PendingConnect 记录
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>打开本地终端</summary>
        public void OpenLocalTerminal(string shellPath = null)
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

            _sessions[tab] = new TabSession
            {
                Config = new ConnectionConfig
                {
                    Id = "local-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = "本地终端",
                    Host = "localhost",
                    Protocol = ProtocolType.SSH
                },
                Control = terminal,
                Protocol = ProtocolType.SSH,
                IsConnected = true,
                SessionId = Guid.NewGuid().ToString("N")
            };

            _tabControl.TabPages.Add(tab);
            _tabControl.SelectedTab = tab;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>打开 SFTP 浏览器标签</summary>
        public void OpenSftpBrowser(ConnectionConfig config)
        {
            if (config == null) return;

            var credential = ResolveCredential(config) ?? new CredentialPayload { Username = config.Username };
            var tab = new TabPage("SFTP: " + config.Name)
            {
                ToolTipText = "sftp://" + config.Host
            };

            var panel = new SftpBrowserPanel(config, credential, _sftpFactory, _tunnelManager);
            panel.Dock = DockStyle.Fill;
            tab.Controls.Add(panel);

            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = panel,
                Protocol = ProtocolType.SSH,
                IsConnected = false,
                Credential = credential,
                SessionId = config.Id + "-sftp"
            };

            _tabControl.TabPages.Add(tab);
            _tabControl.SelectedTab = tab;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        private CredentialPayload ResolveCredential(ConnectionConfig config)
        {
            return _credentialResolver != null
                ? _credentialResolver.Resolve(config)
                : null;
        }

        private TabPage CreateSshTerminalTab(ConnectionConfig config, CredentialPayload credential)
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
                if (_sessions.TryGetValue(tab, out var ts))
                {
                    ts.IsConnected = true;
                    WireHealthAndReconnect(ts, terminalControl.Session);
                }
                TryRunLogonScript(terminalControl, config);
            };
            tab.Controls.Add(terminalControl);

            var sessionId = config.Id ?? Guid.NewGuid().ToString("N");
            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = terminalControl,
                Protocol = ProtocolType.SSH,
                IsConnected = false,
                Credential = credential,
                SessionId = sessionId
            };

            return tab;
        }

        private TabPage CreateRdpTab(ConnectionConfig config, CredentialPayload credential)
        {
            var tab = new TabPage(config.Name)
            {
                ToolTipText = "RDP: " + config.Host + ":" + config.Port
            };

            if (credential != null && !string.IsNullOrEmpty(credential.Password))
            {
                try
                {
                    _keepassService.InjectRdpCredential(
                        config.Host, credential.Username, credential.Password);
                }
                catch { }
            }

            var rdp = _rdpFactory.Create();
            rdp.Control.Dock = DockStyle.Fill;
            tab.Controls.Add(rdp.Control);

            var options = RdpOptionsBuilder.FromConnection(config);

            // 延迟连接：选中标签时再 Connect
            var session = new TabSession
            {
                Config = config,
                Control = rdp.Control,
                Protocol = ProtocolType.RDP,
                IsConnected = false,
                Credential = credential,
                RdpClient = rdp,
                SessionId = config.Id ?? Guid.NewGuid().ToString("N"),
                PendingConnect = () =>
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
                        if (_sessions.TryGetValue(tab, out var ts))
                            ts.IsConnected = true;
                        _auditLogger?.LogConnection(
                            config.Id,
                            config.Host ?? config.Name,
                            ProtocolType.RDP.ToString(),
                            ConnectionAction.Open);
                    }
                    catch (Exception ex)
                    {
                        _auditLogger?.LogConnection(
                            config.Id,
                            config.Host ?? config.Name,
                            ProtocolType.RDP.ToString(),
                            ConnectionAction.Error);
                        MessageBox.Show("RDP 连接失败: " + ex.Message, "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            };

            _sessions[tab] = session;
            return tab;
        }

        private TabPage CreateSerialTab(ConnectionConfig config)
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
                if (_sessions.TryGetValue(tab, out var ts))
                    ts.IsConnected = true;
                TryRunLogonScript(terminalControl, config);
            };
            tab.Controls.Add(terminalControl);

            _sessions[tab] = new TabSession
            {
                Config = config,
                Control = terminalControl,
                Protocol = ProtocolType.Serial,
                IsConnected = false,
                SessionId = config.Id ?? Guid.NewGuid().ToString("N")
            };

            return tab;
        }


        private void TryRunLogonScript(TerminalControl terminal, ConnectionConfig config)
        {
            _lifecycle.TryRunLogonScript(terminal, config);
        }

        private void WireHealthAndReconnect(TabSession ts, ITerminalSession session)
        {
            if (ts == null || session == null) return;
            ts.HealthMonitor = _lifecycle.WireHealthAndReconnect(
                ts.SessionId, session, ts.HealthMonitor);
        }

        public void SplitHorizontal() => SplitCurrentTab("horizontal");
        public void SplitVertical() => SplitCurrentTab("vertical");

        private void SplitCurrentTab(string direction)
        {
            var selectedTab = _tabControl.SelectedTab;
            if (selectedTab == null || !_sessions.ContainsKey(selectedTab))
            {
                MessageBox.Show("请先打开一个连接", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var session = _sessions[selectedTab];
            if (!(session.Control is TerminalControl))
            {
                MessageBox.Show("仅终端标签支持分屏", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var currentControl = session.Control;
            var newTerminal = new TerminalControl(
                session.Config, _terminalFactory, _tunnelManager, _auditLogger, _dangerousDetector);
            newTerminal.Credentials = session.Credential;
            newTerminal.Dock = DockStyle.Fill;
            newTerminal.ResumeRendering();

            var splitPane = direction == "horizontal"
                ? SplitPaneControl.CreateHorizontal(currentControl, newTerminal, 0.5)
                : SplitPaneControl.CreateVertical(currentControl, newTerminal, 0.5);
            splitPane.Dock = DockStyle.Fill;

            selectedTab.Controls.Clear();
            selectedTab.Controls.Add(splitPane);
            session.Control = splitPane;
        }

        public void CloseAllTabs()
        {
            foreach (TabPage tab in _tabControl.TabPages)
                CloseTab(tab);
            _tabControl.TabPages.Clear();
            _sessions.Clear();
        }

        private void CloseTab(TabPage tab)
        {
            if (!_sessions.TryGetValue(tab, out var session)) return;

            var sessionId = session.SessionId;
            var connectionId = session.Config?.Id;

            if (!string.IsNullOrEmpty(sessionId))
                _reconnectWatchdog?.Unwatch(sessionId);

            try { session.HealthMonitor?.Dispose(); } catch { }

            if (session.Protocol == ProtocolType.RDP)
            {
                try { session.RdpClient?.Dispose(); } catch { }
                try { _keepassService?.CleanupRdpCredential(session.Config?.Host); } catch { }
            }

            if (session.Control is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }

            // 先从字典移除，再判断同 connectionId 是否还有其他标签共享隧道
            _sessions.Remove(tab);

            if (!string.IsNullOrEmpty(connectionId))
            {
                var remaining = new List<string>();
                foreach (var other in _sessions.Values)
                {
                    if (other?.Config?.Id != null)
                        remaining.Add(other.Config.Id);
                }
                _lifecycle.CloseTunnelIfLastUser(_tunnelManager, connectionId, remaining);
            }

            _lifecycle.LogConnectionClose(
                connectionId,
                session.Config?.Host ?? session.Config?.Name,
                session.Protocol.ToString());

            try { _tabControl.TabPages.Remove(tab); } catch { }

            if (!string.IsNullOrEmpty(sessionId))
            {
                try { SessionClosed?.Invoke(this, sessionId); } catch { }
            }

            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnDrawTab(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _tabControl.TabPages.Count) return;
            var tab = _tabControl.TabPages[e.Index];
            var rect = e.Bounds;

            bool isSelected = (e.Index == _tabControl.SelectedIndex);
            using (var brush = new SolidBrush(isSelected ? SystemColors.ControlLight : SystemColors.Control))
                e.Graphics.FillRectangle(brush, rect);

            var textRect = new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - 24, rect.Height - 4);
            TextRenderer.DrawText(e.Graphics, tab.Text, e.Font, textRect, SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var closeRect = new Rectangle(rect.Right - 18, rect.Y + 4, 14, 16);
            using (var brush = new SolidBrush(Color.DarkGray))
                e.Graphics.DrawString("×", e.Font, brush, closeRect);
        }

        private void OnTabMouseDown(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabControl.TabPages.Count; i++)
            {
                var rect = _tabControl.GetTabRect(i);
                var closeRect = new Rectangle(rect.Right - 18, rect.Y + 4, 14, 16);
                if (closeRect.Contains(e.Location))
                {
                    var tab = _tabControl.TabPages[i];
                    CloseTab(tab);
                    break;
                }
            }
        }

        private void OnTabSelectedIndexChanged(object sender, EventArgs e)
        {
            foreach (var kvp in _sessions)
            {
                bool selected = kvp.Key == _tabControl.SelectedTab;

                if (kvp.Value.Control is TerminalControl tc)
                {
                    if (selected) tc.ResumeRendering();
                    else tc.PauseRendering();
                }

                if (kvp.Value.HealthMonitor != null)
                    kvp.Value.HealthMonitor.IsPaused = !selected;

                // RDP 懒连接
                if (selected && kvp.Value.PendingConnect != null && !kvp.Value.IsConnected)
                {
                    var connect = kvp.Value.PendingConnect;
                    kvp.Value.PendingConnect = null;
                    connect();
                }
            }

            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void CloseActiveTab()
        {
            if (_tabControl.SelectedTab != null)
                CloseTab(_tabControl.SelectedTab);
        }

        public void ReconnectActiveTab()
        {
            if (_tabControl.SelectedTab == null) return;
            if (!_sessions.TryGetValue(_tabControl.SelectedTab, out var session)) return;

            var config = session.Config;
            var cred = session.Credential;
            CloseTab(_tabControl.SelectedTab);

            if (config != null)
            {
                OpenConnection(config);
                // 重新注入凭据
                if (_tabControl.SelectedTab != null &&
                    _sessions.TryGetValue(_tabControl.SelectedTab, out var newSession))
                {
                    newSession.Credential = cred;
                    if (newSession.Control is TerminalControl tc)
                        tc.Credentials = cred;
                }
            }
        }

        public void ReconnectById(string connectionId)
        {
            ReconnectByIdSync(connectionId);
        }

        private bool ReconnectByIdSync(string connectionId)
        {
            ConnectionConfig config = null;
            CredentialPayload cred = null;

            foreach (TabPage tab in new List<TabPage>(EnumTabs()))
            {
                if (_sessions.TryGetValue(tab, out var session) &&
                    session.Config?.Id == connectionId)
                {
                    config = session.Config;
                    cred = session.Credential;
                    CloseTab(tab);
                    break;
                }
            }

            if (config == null && _connectionStore != null)
                config = _connectionStore.GetById(connectionId);

            if (config == null) return false;

            OpenConnection(config);
            if (_tabControl.SelectedTab == null ||
                !_sessions.TryGetValue(_tabControl.SelectedTab, out var newSession))
                return false;

            if (cred != null)
            {
                newSession.Credential = cred;
                if (newSession.Control is TerminalControl tcCred)
                    tcCred.Credentials = cred;
            }

            // 懒连接：主动触发并等待，避免 Watchdog 把“仅建标签”当成重连成功
            try
            {
                if (newSession.Control is TerminalControl tc)
                {
                    tc.ResumeRendering();
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (tc.IsConnected)
                        {
                            newSession.IsConnected = true;
                            WireHealthAndReconnect(newSession, tc.Session);
                            return true;
                        }
                        System.Threading.Thread.Sleep(200);
                        Application.DoEvents();
                    }
                    return tc.IsConnected;
                }

                if (newSession.PendingConnect != null)
                {
                    var connect = newSession.PendingConnect;
                    newSession.PendingConnect = null;
                    connect();
                    return newSession.IsConnected;
                }
            }
            catch
            {
                return false;
            }

            // 非终端/非 RDP 延迟连接：仅表示标签已重建，不算连接成功
            return false;
        }

        private IEnumerable<TabPage> EnumTabs()
        {
            foreach (TabPage t in _tabControl.TabPages)
                yield return t;
        }

        public int ActiveTabIndex
        {
            get { return _tabControl.SelectedIndex; }
        }

        public List<OpenTabState> GetOpenTabStates()
        {
            var result = new List<OpenTabState>();
            foreach (TabPage tab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(tab, out var session) && session.Config != null)
                {
                    result.Add(new OpenTabState
                    {
                        ConnectionId = session.Config.Id,
                        Title = session.Config.Name,
                        Protocol = session.Protocol.ToString(),
                        Host = session.Config.Host,
                        IsActive = (tab == _tabControl.SelectedTab)
                    });
                }
            }
            return result;
        }

        public void SetActiveTabIndex(int index)
        {
            if (index >= 0 && index < _tabControl.TabCount)
                _tabControl.SelectedIndex = index;
        }

        /// <summary>当前活动终端控件</summary>
        public TerminalControl GetActiveTerminalControl()
        {
            if (_tabControl.SelectedTab == null) return null;
            if (!_sessions.TryGetValue(_tabControl.SelectedTab, out var session)) return null;
            return session.Control as TerminalControl;
        }

        /// <summary>当前活动终端会话</summary>
        public ITerminalSession GetActiveSession()
        {
            return GetActiveTerminalControl()?.Session;
        }

        /// <summary>
        /// 当前活动 SSH 的端口转发宿主（UI 不直接持有 SshClient）。
        /// </summary>
        public ISshPortForwardHost GetActivePortForwardHost()
        {
            var session = GetActiveSession() as TerminalSession;
            if (session == null || session.UnderlyingClient == null) return null;
            return SshPortForwardHost.Wrap(session.UnderlyingClient);
        }

        /// <summary>
        /// 当前活动 SSH 的远程工具会话抽象。
        /// </summary>
        public ISshRemoteSession GetActiveRemoteSession()
        {
            var session = GetActiveSession() as TerminalSession;
            if (session == null || session.UnderlyingClient == null) return null;
            return SshNetRemoteSession.Wrap(session.UnderlyingClient);
        }

        /// <summary>兼容旧调用：返回端口转发宿主</summary>
        public ISshPortForwardHost GetActiveSshClient()
        {
            return GetActivePortForwardHost();
        }

        /// <summary>所有已连接终端会话（多通道/批量命令）</summary>
        public Dictionary<string, ITerminalSession> GetConnectedSessions()
        {
            var map = new Dictionary<string, ITerminalSession>();
            foreach (var kvp in _sessions)
            {
                if (kvp.Value.Control is TerminalControl tc && tc.Session != null && tc.IsConnected)
                {
                    var id = kvp.Value.SessionId ?? kvp.Value.Config?.Id ?? Guid.NewGuid().ToString("N");
                    map[id] = tc.Session;
                }
            }
            return map;
        }

        /// <summary>当前活动健康监控</summary>
        public ConnectionHealthMonitor GetActiveHealthMonitor()
        {
            if (_tabControl.SelectedTab == null) return null;
            if (!_sessions.TryGetValue(_tabControl.SelectedTab, out var session)) return null;
            return session.HealthMonitor;
        }

        private class TabSession
        {
            public ConnectionConfig Config { get; set; }
            public Control Control { get; set; }
            public ProtocolType Protocol { get; set; }
            public bool IsConnected { get; set; }
            public CredentialPayload Credential { get; set; }
            public string SessionId { get; set; }
            public IRdpClient RdpClient { get; set; }
            public ConnectionHealthMonitor HealthMonitor { get; set; }
            public Action PendingConnect { get; set; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseAllTabs();
            base.Dispose(disposing);
        }
    }
}
