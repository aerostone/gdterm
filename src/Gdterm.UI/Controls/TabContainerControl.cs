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
        private readonly IAuditLogger _auditLogger;
        private readonly IKeePassService _keepassService;
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly IConnectionStore _connectionStore;
        private readonly TabSessionLifecycle _lifecycle;
        private readonly ProtocolTabOpener _opener;
        private readonly TabReconnectService _reconnectService;
        private readonly TabActiveSessionQuery _activeQuery;
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
            _tunnelManager = tunnelManager;
            _auditLogger = auditLogger;
            _keepassService = keepassService;
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
            _activeQuery = new TabActiveSessionQuery(
                () => _tabControl != null ? _tabControl.SelectedTab : null,
                _sessions);

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

            var opened = _opener.CreateForConnection(config);
            if (opened == null || opened.Page == null || opened.Session == null) return;

            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            _tabControl.SelectedTab = opened.Page;
            // 懒连接：真实 Open/Error 由 TerminalControl 或 RDP PendingConnect 记录
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>打开本地终端</summary>
        public void OpenLocalTerminal(string shellPath = null)
        {
            var opened = _opener.CreateLocal(shellPath);
            if (opened == null) return;
            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            _tabControl.SelectedTab = opened.Page;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>打开 SFTP 浏览器标签</summary>
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
            TryRunLogonScript(terminalControl, config);
        }

        private void HandleRdpConnected(TabPage tab)
        {
            if (tab != null && _sessions.TryGetValue(tab, out var ts))
                ts.IsConnected = true;
        }

        private void TryRunLogonScript(TerminalControl terminal, ConnectionConfig config)
        {
            _lifecycle.TryRunLogonScript(terminal, config);
        }

        private void WireHealthAndReconnect(TabSessionState ts, ITerminalSession session)
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
            var newTerminal = _opener.CreateSplitTerminal(session.Config, session.Credential);

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

            return _reconnectService.CompleteAfterOpen(
                newSession,
                cred,
                WireHealthAndReconnect);
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
            return _activeQuery.GetActiveTerminalControl();
        }

        /// <summary>当前活动终端会话</summary>
        public ITerminalSession GetActiveSession()
        {
            return _activeQuery.GetActiveSession();
        }

        /// <summary>
        /// 当前活动 SSH 的端口转发宿主（UI 不直接持有 SshClient）。
        /// </summary>
        public ISshPortForwardHost GetActivePortForwardHost()
        {
            return _activeQuery.GetActivePortForwardHost();
        }

        /// <summary>
        /// 当前活动 SSH 的远程工具会话抽象。
        /// </summary>
        public ISshRemoteSession GetActiveRemoteSession()
        {
            return _activeQuery.GetActiveRemoteSession();
        }

        /// <summary>兼容旧调用：返回端口转发宿主</summary>
        public ISshPortForwardHost GetActiveSshClient()
        {
            return GetActivePortForwardHost();
        }

        /// <summary>所有已连接终端会话（多通道/批量命令）</summary>
        public Dictionary<string, ITerminalSession> GetConnectedSessions()
        {
            return _activeQuery.GetConnectedSessions();
        }

        /// <summary>当前活动健康监控</summary>
        public ConnectionHealthMonitor GetActiveHealthMonitor()
        {
            return _activeQuery.GetActiveHealthMonitor();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseAllTabs();
            base.Dispose(disposing);
        }
    }
}
