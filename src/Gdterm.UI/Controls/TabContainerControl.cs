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
using Gdterm.UI.Diagnostics;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.Security;

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
        private ContextMenuStrip _tabContextMenu;

        /// <summary>活动标签变化</summary>
        public event EventHandler ActiveSessionChanged;

        /// <summary>标签关闭时触发（参数为 SessionId，供多通道注销等）</summary>
        public event EventHandler<string> SessionClosed;

        // ===== 终端右键菜单转发事件（由 TerminalControl 的 4 个同名事件转出） =====
        public event EventHandler SearchRequested;
        public event EventHandler ReconnectRequested;
        public event EventHandler ExportRequested;
        public event EventHandler AppearanceSettingsRequested;
        /// <summary>终端尺寸/编码变化转发（由 TerminalControl.TerminalInfoChanged 转出）。</summary>
        public event EventHandler<Size> TerminalInfoChanged;

        /// <summary>标签数量变化（落地页显示/隐藏）。</summary>
        public event EventHandler TabCountChanged;

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

            // 右键菜单（参考 Xshell/MobaXterm Tab 右键）
            _tabContextMenu = new ContextMenuStrip();
            _tabContextMenu.Items.Add("关闭当前(&C)", null, (s, e) => TryCloseSelected());
            _tabContextMenu.Items.Add("关闭其他(&O)", null, (s, e) => CloseOthers());
            _tabContextMenu.Items.Add("关闭右侧全部(&R)", null, (s, e) => CloseRight());
            _tabContextMenu.Items.Add("-");
            _tabContextMenu.Items.Add("水平拆分(&H)", null, (s, e) => SplitHorizontal());
            _tabContextMenu.Items.Add("垂直拆分(&V)", null, (s, e) => SplitVertical());
            _tabContextMenu.Items.Add("-");
            _tabContextMenu.Items.Add("重连当前(&E)", null, (s, e) => { try { ReconnectActiveTab(); } catch { } });
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
            RaiseTabCountChanged();
            _tabControl.SelectedTab = opened.Page;
            // 首开标签时 SelectedIndexChanged 可能不触发（Index 已是 0），强制恢复渲染并懒连接
            ForceActivateSession(opened.Page);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                DiagLog.Info("TabContainer.OpenConnection",
                    "id=" + (config.Id ?? "") + " host=" + (config.Host ?? "") +
                    " proto=" + config.Protocol);
            }
            catch { }
        }

        public void OpenLocalTerminal(string shellPath = null)
        {
            var opened = _opener.CreateLocal(shellPath);
            if (opened == null) return;
            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            RaiseTabCountChanged();
            _tabControl.SelectedTab = opened.Page;
            ForceActivateSession(opened.Page);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            try { DiagLog.Info("TabContainer.OpenLocalTerminal", "ok"); } catch { }
        }

        public void OpenSftpBrowser(ConnectionConfig config)
        {
            var opened = _opener.CreateSftp(config);
            if (opened == null) return;
            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            RaiseTabCountChanged();
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

            // 订阅终端右键菜单事件，转发给顶层订阅者（MainForm 调起面板）。
            if (terminalControl != null)
            {
                terminalControl.SearchRequested += (s, e) => SearchRequested?.Invoke(terminalControl, e);
                terminalControl.ReconnectRequested += (s, e) =>
                {
                    // 直接走当前活动标签重连；重连路径已 awaited。
                    try { ReconnectActiveTab(); } catch { }
                    ReconnectRequested?.Invoke(terminalControl, e);
                };
                terminalControl.ExportRequested += (s, e) => ExportRequested?.Invoke(terminalControl, e);
                terminalControl.AppearanceSettingsRequested += (s, e) => AppearanceSettingsRequested?.Invoke(terminalControl, e);
                // 终端尺寸/编码变化转发到状态栏
                terminalControl.TerminalInfoChanged += (s, size) =>
                {
                    try { TerminalInfoChanged?.Invoke(terminalControl, size); } catch { }
                };
                // 连上后立即推送一次尺寸 + 编码给状态栏
                try
                {
                    var info = terminalControl.GetCurrentTerminalInfo();
                    TerminalInfoChanged?.Invoke(terminalControl, info);
                }
                catch { }
            }
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
            // 右键：选中所点标签 + 弹右键菜单（参考 Xshell/MobaXterm）
            if (e.Button == MouseButtons.Right)
            {
                for (int i = 0; i < _tabControl.TabCount; i++)
                {
                    if (_tabControl.GetTabRect(i).Contains(e.Location))
                    {
                        try { _tabControl.SelectedIndex = i; } catch { }
                        try { _tabContextMenu.Show(_tabControl, e.Location); } catch { }
                        return;
                    }
                }
                return;
            }

            var tab = _chrome.HitTestClose(_tabControl, e.Location);
            if (tab != null)
                CloseTab(tab);
        }

        private void TryCloseSelected()
        {
            if (_tabControl.SelectedTab != null)
                CloseTab(_tabControl.SelectedTab);
        }

        /// <summary>关闭除当前以外的所有标签。</summary>
        public void CloseOthers()
        {
            var current = _tabControl.SelectedTab;
            if (current == null) return;
            // 复制一份再迭代，避免 CloseTab 修改枚举
            var toClose = new List<TabPage>();
            foreach (TabPage t in _tabControl.TabPages)
                if (t != current) toClose.Add(t);
            foreach (var t in toClose) CloseTab(t);
        }

        /// <summary>关闭当前右侧所有标签。</summary>
        public void CloseRight()
        {
            int idx = _tabControl.SelectedIndex;
            if (idx < 0) return;
            var toClose = new List<TabPage>();
            for (int i = idx + 1; i < _tabControl.TabCount; i++)
                toClose.Add(_tabControl.TabPages[i]);
            foreach (var t in toClose) CloseTab(t);
        }

        private void OnTabSelectedIndexChanged(object sender, EventArgs e)
        {
            _selection.OnSelectedChanged(_tabControl, _sessions);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 显式激活标签：ResumeRendering + RDP PendingConnect。
        /// 解决「只有一个标签时 SelectedIndexChanged 不触发 → 点连接无反应」。
        /// </summary>
        private void ForceActivateSession(TabPage page)
        {
            if (page == null) return;
            try
            {
                _selection.OnSelectedChanged(_tabControl, _sessions);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TabContainer.ForceActivateSession", ex);
            }
        }

        public void CloseActiveTab() /*tabcount*/
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

        /// <summary>
        /// 将当前 GlobalAppearance 重新应用到所有已开标签页（包含 split-pane 子面板）。
        /// 供 ToolsDialogsLauncher 在用户保存外观设置后即时刷新已开终端使用。 Split-pane 通过 TabActiveSessionQuery.CollectSessionTerminals 递归收集。
        /// </summary>
        public void ApplyAppearanceToAllTerminals()
        {
            foreach (var kvp in _sessions)
            {
                var state = kvp.Value;
                if (state == null) continue;
                var terminals = new System.Collections.Generic.List<TerminalControl>();
                TabActiveSessionQuery.CollectSessionTerminals(state, terminals);
                foreach (var tc in terminals)
                {
                    if (tc == null) continue;
                    try { tc.ApplyCurrentAppearance(); } catch { }
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

        public int OpenTabCount
        {
            get
            {
                try { return _tabControl != null ? _tabControl.TabCount : 0; }
                catch { return 0; }
            }
        }

        private void RaiseTabCountChanged()
        {
            try
            {
                var h = TabCountChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch { }
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
