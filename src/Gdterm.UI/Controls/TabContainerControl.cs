using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Rdp;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 标签页容器壳——持有会话字典与 TabControl，业务委托 Services 层（finding-10）。
    /// </summary>
    public class TabContainerControl : UserControl
    {
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly IConnectionStore _connectionStore;
        private readonly TabSessionLifecycle _lifecycle;
        private readonly ProtocolTabOpener _opener;
        private readonly TabReconnectService _reconnectService;
        private readonly TabCloseService _closeService;
        private readonly TabActiveSessionQuery _activeQuery;
        private readonly TabSplitService _splitService;
        private readonly TabChromePainter _chrome;
        private readonly TabSelectionCoordinator _selection;
        private readonly Dictionary<TabPage, TabSessionState> _sessions = new Dictionary<TabPage, TabSessionState>();
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
            // aiService 保留构造参数以兼容 Program/MainForm 签名；协议侧不直接消费
            _ = aiService;
            _reconnectWatchdog = reconnectWatchdog;
            _connectionStore = connectionStore;
            _lifecycle = new TabSessionLifecycle(auditLogger, reconnectWatchdog);
            _opener = new ProtocolTabOpener(
                tunnelManager,
                terminalFactory,
                rdpFactory ?? new RdpClientFactory(),
                sftpFactory,
                auditLogger,
                keepassService,
                dangerousDetector,
                new CredentialResolver(keepassService, folderCredStore));
            _opener.OnTerminalConnected = HandleTerminalConnected;
            _opener.OnRdpConnected = HandleRdpConnected;
            _reconnectService = new TabReconnectService();
            _closeService = new TabCloseService(
                _lifecycle, _reconnectWatchdog, keepassService, tunnelManager);
            _splitService = new TabSplitService(_opener);
            _chrome = new TabChromePainter();
            _selection = new TabSelectionCoordinator();
            _activeQuery = new TabActiveSessionQuery(
                () => _tabControl != null ? _tabControl.SelectedTab : null,
                _sessions);

            if (_reconnectWatchdog != null)
            {
                // go-live P0-01：UI 线程只调度，await 异步重连，禁止 GetResult 死锁
                _reconnectWatchdog.DefaultReconnectFunc = async (id, session) =>
                {
                    if (IsDisposed) return false;
                    if (InvokeRequired)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        BeginInvoke(new Action(() =>
                        {
                            // 在 UI 线程启动 async 重连，完成后回填 tcs（无 async void lambda）
                            ReconnectByIdAsync(id).ContinueWith(t =>
                            {
                                if (t.IsFaulted && t.Exception != null)
                                    tcs.TrySetException(t.Exception.GetBaseException());
                                else if (t.IsCanceled)
                                    tcs.TrySetCanceled();
                                else
                                    tcs.TrySetResult(t.Result);
                            }, TaskScheduler.Default);
                        }));
                        return await tcs.Task.ConfigureAwait(false);
                    }
                    return await ReconnectByIdAsync(id).ConfigureAwait(true);
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

            var opened = _opener.CreateForConnection(config);
            if (opened == null || opened.Page == null || opened.Session == null) return;

            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            _tabControl.SelectedTab = opened.Page;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void OpenLocalTerminal(string shellPath = null)
        {
            var opened = _opener.CreateLocal(shellPath);
            if (opened == null) return;
            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            _tabControl.SelectedTab = opened.Page;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void OpenSftpBrowser(ConnectionConfig config)
        {
            var opened = _opener.CreateSftp(config);
            if (opened == null) return;
            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            _tabControl.SelectedTab = opened.Page;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void HandleTerminalConnected(TabPage tab, TerminalControl terminalControl, ConnectionConfig config)
        {
            if (tab != null && _sessions.TryGetValue(tab, out var ts))
            {
                ts.IsConnected = true;
                WireHealthAndReconnect(ts, terminalControl != null ? terminalControl.Session : null);
            }
            _lifecycle.TryRunLogonScript(terminalControl, config);
        }

        private void HandleRdpConnected(TabPage tab)
        {
            if (tab != null && _sessions.TryGetValue(tab, out var ts))
                ts.IsConnected = true;
        }

        private void WireHealthAndReconnect(TabSessionState ts, ITerminalSession session)
        {
            if (ts == null || session == null) return;
            ts.HealthMonitor = _lifecycle.WireHealthAndReconnect(
                ts.SessionId, session, ts.HealthMonitor);
        }

        public void SplitHorizontal() => _splitService.TrySplit("horizontal", _tabControl.SelectedTab, _sessions);
        public void SplitVertical() => _splitService.TrySplit("vertical", _tabControl.SelectedTab, _sessions);

        public void CloseAllTabs()
        {
            _closeService.CloseAllTabs(_tabControl, _sessions);
            try { ActiveSessionChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void CloseTab(TabPage tab)
        {
            var sessionId = _closeService.CloseTab(tab, _sessions, _tabControl);
            if (!string.IsNullOrEmpty(sessionId))
            {
                try { SessionClosed?.Invoke(this, sessionId); } catch { }
            }
            try { ActiveSessionChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void OnDrawTab(object sender, DrawItemEventArgs e)
        {
            _chrome.DrawTab(e, _tabControl);
        }

        private void OnTabMouseDown(object sender, MouseEventArgs e)
        {
            var tab = _chrome.HitTestClose(_tabControl, e.Location);
            if (tab != null)
                CloseTab(tab);
        }

        private void OnTabSelectedIndexChanged(object sender, EventArgs e)
        {
            _selection.OnSelectedChanged(_tabControl, _sessions);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void CloseActiveTab()
        {
            if (_tabControl.SelectedTab != null)
                CloseTab(_tabControl.SelectedTab);
        }

        /// <summary>
        /// 锁屏时擦除所有标签缓存的明文凭据（finding-04）。
        /// 已建立的 SSH 会话不受影响，但自动重连无法再用缓存密码。
        /// </summary>
        public void ClearCachedCredentials()
        {
            foreach (var kvp in _sessions)
            {
                var state = kvp.Value;
                if (state == null) continue;
                try { state.Credential?.ClearSecrets(); } catch { }
                state.Credential = null;
                var terminals = new System.Collections.Generic.List<TerminalControl>();
                TabActiveSessionQuery.CollectSessionTerminals(state, terminals);
                foreach (var tc in terminals)
                {
                    if (tc == null) continue;
                    try { tc.ClearCachedCredentials(); } catch { }
                }
            }
        }

        /// <summary>解锁后重新武装所有健康监控（go-live P1-03）。</summary>
        public void RearmAllHealthMonitors()
        {
            foreach (var kvp in _sessions)
            {
                try { kvp.Value?.HealthMonitor?.Rearm(); } catch { }
            }
        }

        public void ReconnectActiveTab()
        {
            var _ = ReconnectActiveTabAsync();
        }

        public async Task ReconnectActiveTabAsync()
        {
            await _reconnectService.ReconnectActiveAsync(
                _tabControl.SelectedTab,
                _sessions,
                CloseTab,
                OpenConnection,
                () => _tabControl.SelectedTab,
                OnTerminalReconnected).ConfigureAwait(true);
        }

        public void ReconnectById(string connectionId)
        {
            var _ = ReconnectByIdAsync(connectionId);
        }

        private Task<bool> ReconnectByIdAsync(string connectionId)
        {
            return _reconnectService.ReconnectByIdAsync(
                connectionId,
                _sessions,
                EnumTabs(),
                CloseTab,
                OpenConnection,
                () => _tabControl.SelectedTab,
                OnTerminalReconnected,
                _connectionStore);
        }

        private void OnTerminalReconnected(TabSessionState session, ITerminalSession terminalSession)
        {
            WireHealthAndReconnect(session, terminalSession);
            // P0-02：重连成功后重新武装健康监控
            try { session?.HealthMonitor?.RecordReconnect(); } catch { }
        }

        private IEnumerable<TabPage> EnumTabs()
        {
            foreach (TabPage page in _tabControl.TabPages)
                yield return page;
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

        public TerminalControl GetActiveTerminalControl() => _activeQuery.GetActiveTerminalControl();
        public ITerminalSession GetActiveSession() => _activeQuery.GetActiveSession();
        public ISshPortForwardHost GetActivePortForwardHost() => _activeQuery.GetActivePortForwardHost();
        public ISshRemoteSession GetActiveRemoteSession() => _activeQuery.GetActiveRemoteSession();
        public ISshPortForwardHost GetActiveSshClient() => GetActivePortForwardHost();
        public Dictionary<string, ITerminalSession> GetConnectedSessions() => _activeQuery.GetConnectedSessions();
        public ConnectionHealthMonitor GetActiveHealthMonitor() => _activeQuery.GetActiveHealthMonitor();

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseAllTabs();
            base.Dispose(disposing);
        }
    }
}
