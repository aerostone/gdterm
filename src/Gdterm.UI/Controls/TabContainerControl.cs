using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.Logging;
using Gdterm.Rdp;
using Gdterm.Rdp.Models;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 标签页容器——SSH/RDP/串口/本地/SFTP，懒连接、暂停渲染、自动重连、健康监控
    /// </summary>
    public class TabContainerControl : UserControl
    {
        private readonly TunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IAiAssistantService _aiService;
        private readonly IAuditLogger _auditLogger;
        private readonly IKeePassService _keepassService;
        private readonly IFolderCredentialStore _folderCredStore;
        private readonly DangerousCommandDetector _dangerousDetector;
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly IConnectionStore _connectionStore;
        private readonly Dictionary<TabPage, TabSession> _sessions = new Dictionary<TabPage, TabSession>();
        private TabControl _tabControl;

        /// <summary>活动标签变化</summary>
        public event EventHandler ActiveSessionChanged;

        public TabContainerControl(
            TunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IAiAssistantService aiService,
            IAuditLogger auditLogger,
            IKeePassService keepassService,
            IFolderCredentialStore folderCredStore,
            DangerousCommandDetector dangerousDetector = null,
            AutoReconnectWatchdog reconnectWatchdog = null,
            IConnectionStore connectionStore = null)
        {
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _sftpFactory = sftpFactory;
            _aiService = aiService;
            _auditLogger = auditLogger;
            _keepassService = keepassService;
            _folderCredStore = folderCredStore;
            _dangerousDetector = dangerousDetector;
            _reconnectWatchdog = reconnectWatchdog;
            _connectionStore = connectionStore;

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
            _auditLogger?.LogConnection(config.Id, config.Name, config.Host, true);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>打开本地终端</summary>
        public void OpenLocalTerminal(string shellPath = null)
        {
            var local = TerminalSessionFactory.CreateLocal(shellPath);
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
            try
            {
                if (_keepassService == null || !_keepassService.IsUnlocked)
                    return null;

                KeePassEntry entry = null;

                if (!string.IsNullOrEmpty(config.CredentialRefId))
                {
                    try { entry = GetKeePassEntry(config.CredentialRefId); }
                    catch { }
                }

                if (entry == null && _folderCredStore != null && !string.IsNullOrEmpty(config.GroupPath))
                {
                    try
                    {
                        var inheritedRefId = _folderCredStore.ResolveByInheritance(config.GroupPath);
                        if (!string.IsNullOrEmpty(inheritedRefId))
                            entry = GetKeePassEntry(inheritedRefId);
                    }
                    catch { }
                }

                if (entry == null)
                    entry = _keepassService.FindEntryByConnection(config);

                if (entry == null) return null;

                var credential = new CredentialPayload
                {
                    Username = !string.IsNullOrEmpty(entry.Username) ? entry.Username : config.Username,
                    Password = entry.Password ?? ""
                };

                if (config.Protocol == ProtocolType.SSH && entry.SshPrivateKeyData != null)
                {
                    credential.SshPrivateKey = entry.SshPrivateKeyData;
                    credential.SshPrivateKeyPassphrase = entry.SshPrivateKeyPassphrase;
                }

                return credential;
            }
            catch
            {
                return null;
            }
        }

        private KeePassEntry GetKeePassEntry(string entryId)
        {
            var entries = _keepassService.ListEntries();
            foreach (var summary in entries)
            {
                if (summary.Id == entryId)
                {
                    var cred = _keepassService.GetCredential(entryId);
                    return new KeePassEntry
                    {
                        Id = summary.Id,
                        Title = summary.Title,
                        Username = cred.Username,
                        Password = cred.Password,
                        SshPrivateKeyData = _keepassService.GetSshPrivateKey(entryId),
                        SshPrivateKeyPassphrase = _keepassService.GetSshPrivateKeyPassphrase(entryId)
                    };
                }
            }
            return null;
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

            var rdp = new RdpClient();
            rdp.Control.Dock = DockStyle.Fill;
            tab.Controls.Add(rdp.Control);

            var options = BuildRdpOptions(config);

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
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("RDP 连接失败: " + ex.Message, "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            };

            _sessions[tab] = session;
            return tab;
        }

        private static RdpOptions BuildRdpOptions(ConnectionConfig config)
        {
            var opts = new RdpOptions();
            if (config?.Metadata == null) return opts;

            if (config.Metadata.ContainsKey("rdp_drives"))
                opts.RedirectDrives = config.Metadata["rdp_drives"] == "true";
            if (config.Metadata.ContainsKey("rdp_clipboard"))
                opts.RedirectClipboard = config.Metadata["rdp_clipboard"] != "false";
            if (config.Metadata.ContainsKey("rdp_colordepth") &&
                int.TryParse(config.Metadata["rdp_colordepth"], out var depth))
                opts.ColorDepth = depth;
            if (config.Metadata.ContainsKey("rdp_fullscreen"))
                opts.FullScreen = config.Metadata["rdp_fullscreen"] == "true";
            if (config.Metadata.ContainsKey("rdp_nla"))
                opts.EnableNLA = config.Metadata["rdp_nla"] != "false";

            return opts;
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

        private void WireHealthAndReconnect(TabSession ts, ITerminalSession session)
        {
            if (ts == null || session == null) return;

            try { ts.HealthMonitor?.Dispose(); } catch { }
            ts.HealthMonitor = new ConnectionHealthMonitor(session)
            {
                MaxHistoryEntries = 120,
                IsPaused = false
            };
            ts.HealthMonitor.ConnectionLost += host =>
            {
                _reconnectWatchdog?.NotifyConnectionLost(ts.SessionId);
            };
            ts.HealthMonitor.Start(5000);

            _reconnectWatchdog?.Watch(ts.SessionId, session);
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

            if (!string.IsNullOrEmpty(session.SessionId))
                _reconnectWatchdog?.Unwatch(session.SessionId);

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

            _sessions.Remove(tab);
            try { _tabControl.TabPages.Remove(tab); } catch { }
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
            if (_tabControl.SelectedTab != null &&
                _sessions.TryGetValue(_tabControl.SelectedTab, out var newSession))
            {
                if (cred != null)
                {
                    newSession.Credential = cred;
                    if (newSession.Control is TerminalControl tc)
                        tc.Credentials = cred;
                }
                return true;
            }
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
            public RdpClient RdpClient { get; set; }
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
