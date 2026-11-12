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
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.Security;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 标签页容器壳——持有会话字典与 TabControl，业务委托 Services 层（finding-10）。
    /// </summary>
    public class TabContainerControl : UserControl, Gdterm.UI.Services.IHitRegionProvider
    {
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly IConnectionStore _connectionStore;
        private readonly Gdterm.Connections.HighlightStore _highlightStore;
        private readonly Gdterm.Logging.CommandHistoryStore _historyStore;
        private readonly Gdterm.Connections.QuickCommandStore _quickStore;
        private readonly TabSessionLifecycle _lifecycle;
        private readonly ProtocolTabOpener _opener;
        private readonly TabReconnectService _reconnectService;
        private readonly TabCloseService _closeService;
        private readonly TabActiveSessionQuery _activeQuery;
        private readonly TabSplitService _splitService;
        private readonly TabChromePainter _chrome;
        private readonly TabSelectionCoordinator _selection;
        private readonly Dictionary<TabPage, TabSessionState> _sessions = new Dictionary<TabPage, TabSessionState>();
        /// <summary>当前打开着的 SFTP 双栏面板（终端切目录时按主机名找它们跟随）。</summary>
        private readonly System.Collections.Generic.List<SftpDualPanePanel> _sftpPanels =
            new System.Collections.Generic.List<SftpDualPanePanel>();
        private TabControl _tabControl;
        private ContextMenuStrip _tabContextMenu;

        /// <summary>
        /// 标签装饰（P2：重命名 / 颜色）。按 TabPage 记账，不写进 ConnectionConfig——
        /// "这个标签叫生产库"是会话级的临时视图，改连接名才是持久事实，两者不能混。
        /// 需要跨重启保留的部分由 GetOpenTabStates 带进 session-state.json。
        /// </summary>
        private readonly Dictionary<TabPage, TabDecoration> _decorations = new Dictionary<TabPage, TabDecoration>();

        /// <summary>标签装饰数据：自定义标题（null=用连接名）+ 强调色（null=跟随主题）。</summary>
        private sealed class TabDecoration
        {
            public string Title;
            public int? AccentArgb;
            /// <summary>首次加装饰时标签的原始标题（恢复连接名时的兜底）。</summary>
            public string BaseTitle;
        }

        /// <summary>活动标签变化</summary>
        public event EventHandler ActiveSessionChanged;

        /// <summary>标签关闭时触发（参数为 SessionId，供多通道注销等）</summary>
        public event EventHandler<string> SessionClosed;

        // ===== 终端右键菜单转发事件（由 TerminalControl 的 4 个同名事件转出） =====
        public event EventHandler SearchRequested;
        public event EventHandler ReconnectRequested;
        public event EventHandler ExportRequested;
        public event EventHandler AppearanceSettingsRequested;
        /// <summary>终端右键「打开对应连接」（选中文本 host:port，A3-lite）；参数 HostPortEventArgs。</summary>
        public event EventHandler<TerminalControl.HostPortEventArgs> HostPortOpenRequested;
        /// <summary>终端尺寸/编码变化转发（由 TerminalControl.TerminalInfoChanged 转出）。</summary>
        public event EventHandler<Size> TerminalInfoChanged;

        /// <summary>终端上报的应用通知（OSC 9/777/99），供托盘气泡/通知中心使用。</summary>
        public event EventHandler<Gdterm.Terminal.Rendering.Vt.TerminalNotification> NotificationReceived;

        /// <summary>标签数量变化（落地页显示/隐藏）。</summary>
        public event EventHandler TabCountChanged;

        /// <summary>双击标签栏空白处（非任何标签头）时触发——主窗体路由到新建连接（浏览器惯例）。</summary>
        public event EventHandler BlankAreaDoubleClicked;

        /// <summary>
        /// 依赖用 <see cref="Gdterm.UI.Services.TabDependencies"/> 一次传入（2026-11-11 架构审计 F4）。
        /// 原 14 个位置参数里 AiService 是僵尸参数（进来就 <c>_ = aiService</c> 丢弃），已随之删除。
        /// </summary>
        public TabContainerControl(Gdterm.UI.Services.TabDependencies services)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            var tunnelManager = services.TunnelManager;
            var terminalFactory = services.TerminalFactory;
            var sftpFactory = services.SftpFactory;
            var auditLogger = services.AuditLogger;
            var keepassService = services.KeePassService;
            var folderCredStore = services.FolderCredentialStore;
            var dangerousDetector = services.DangerousCommandDetector;
            var commandGate = services.CommandGate
                ?? (dangerousDetector != null ? new CommandGate(dangerousDetector) : null);
            var reconnectWatchdog = services.ReconnectWatchdog;
            var connectionStore = services.ConnectionStore;
            var rdpFactory = services.RdpFactory;

            _reconnectWatchdog = reconnectWatchdog;
            _connectionStore = connectionStore;
            _highlightStore = services.HighlightStore;
            _historyStore = services.CommandHistoryStore;
            _quickStore = services.QuickCommandStore;
            _lifecycle = new TabSessionLifecycle(auditLogger, reconnectWatchdog);
            _opener = new ProtocolTabOpener(
                tunnelManager,
                terminalFactory,
                rdpFactory ?? new RdpClientFactory(),
                sftpFactory,
                auditLogger,
                keepassService,
                dangerousDetector,
                new CredentialResolver(keepassService, folderCredStore),
                commandGate);
            _opener.OnTerminalConnected = HandleTerminalConnected;
            _opener.OnRdpConnected = HandleRdpConnected;
            _reconnectService = new TabReconnectService();
            _closeService = new TabCloseService(
                _lifecycle, _reconnectWatchdog, keepassService, tunnelManager);
            _splitService = new TabSplitService(_opener);
            _chrome = new TabChromePainter();
            _chrome.AccentResolver = GetTabAccent;
            _chrome.BadgeResolver = IsTabRecording;
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
            // P1-4：显式设 token 色，跟随 Dark/Darker/OLED 切换（原先无 BackColor，靠默认原生色）
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = DpiScale.S(this, 120, 24),
                Padding = DpiScale.P(this, 10, 3),
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground
            };
            _tabControl.FontChanged += (s, e) => ApplyElasticTabWidth();
            _tabControl.DrawItem += OnDrawTab;
            _tabControl.MouseDown += OnTabMouseDown;
            _tabControl.MouseMove += OnTabMouseMove;
            _tabControl.MouseLeave += OnTabMouseLeave;
            _tabControl.SelectedIndexChanged += OnTabSelectedIndexChanged;
            // 标签关掉就把装饰一起丢掉：字典持有 TabPage 强引用，不清理会随开合次数累积
            _tabControl.ControlRemoved += (s, e) =>
            {
                try
                {
                    var page = e.Control as TabPage;
                    if (page != null) _decorations.Remove(page);
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            };
            _tabControl.Resize += (s, e) => ApplyElasticTabWidth();
            Controls.Add(_tabControl);

            // 右键菜单（参考 Xshell/MobaXterm Tab 右键）
            _tabContextMenu = new ContextMenuStrip();
            AddCtxItem(_tabContextMenu, L10n.T("关闭当前(&C)"), "close", (s, e) => TryCloseSelected());
            AddCtxItem(_tabContextMenu, L10n.T("关闭其他(&O)"), "close", (s, e) => CloseOthers());
            AddCtxItem(_tabContextMenu, L10n.T("关闭右侧全部(&R)"), "close", (s, e) => CloseRight());
            _tabContextMenu.Items.Add("-");
            AddCtxItem(_tabContextMenu, L10n.T("水平拆分(&H)"), "splith", (s, e) => SplitHorizontal());
            AddCtxItem(_tabContextMenu, L10n.T("垂直拆分(&V)"), "splitv", (s, e) => SplitVertical());
            _tabContextMenu.Items.Add("-");
            AddCtxItem(_tabContextMenu, L10n.T("重连当前(&E)"), "reconnect", (s, e) => { try { ReconnectActiveTab(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } } });
            _tabContextMenu.Items.Add("-");
            AddCtxItem(_tabContextMenu, L10n.T("重命名标签(&N)…"), "rename", (s, e) => PromptRenameActiveTab());
            var colorMenu = new ToolStripMenuItem(L10n.T("标签颜色(&L)"));
            AddColorItem(colorMenu, L10n.T("跟随主题"), null);
            colorMenu.DropDownItems.Add("-");
            AddColorItem(colorMenu, L10n.T("蓝"), TabAccentPalette[0]);
            AddColorItem(colorMenu, L10n.T("绿"), TabAccentPalette[1]);
            AddColorItem(colorMenu, L10n.T("橙"), TabAccentPalette[2]);
            AddColorItem(colorMenu, L10n.T("红"), TabAccentPalette[3]);
            AddColorItem(colorMenu, L10n.T("紫"), TabAccentPalette[4]);
            _tabContextMenu.Items.Add(colorMenu);
        }

        public void OpenConnection(ConnectionConfig config)
        {
            if (config == null) return;

            foreach (TabPage existingTab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(existingTab, out var session) &&
                    session.Config?.Id == config.Id)
                {
                    // 重复打开同一连接：切到已有标签。仍需强制激活——若该标签上次创建后
                    // 从未连上（懒连接未触发/中途失败），仅 SelectTab 会让它永远空白。
                    try { DiagLog.Info("TabContainer.OpenConnection",
                        "duplicate id=" + (config.Id ?? "") + " → activate existing tab"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                    _tabControl.SelectedTab = existingTab;

                    // 已断开的 RDP 标签（服务器踢下线/掉线后重开同一连接）：
                    // 走完整重连路径（关旧标签→重新 OpenConnection→CompleteAfterOpen），
                    // 而不是激活一个死标签让用户手动关掉再开。
                    if (session.Protocol == ProtocolType.RDP && !session.IsConnected && session.PendingConnect == null)
                    {
                        try { DiagLog.Info("TabContainer.OpenConnection", "duplicate disconnected RDP → reconnect id=" + (config.Id ?? "")); }
                        catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                        var _ = ReconnectByIdAsync(config.Id);
                        return;
                    }

                    ForceActivateSession(existingTab);
                    ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            try { DiagLog.Info("TabContainer.OpenConnection",
                "creating id=" + (config.Id ?? "") + " host=" + (config.Host ?? "") +
                " proto=" + config.Protocol); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }

            var opened = _opener.CreateForConnection(config);
            if (opened == null || opened.Page == null || opened.Session == null)
            {
                // 创建返回半成品：必须留痕，否则日志链在此静默断裂无法排查 SSH 失联类问题
                try { DiagLog.Info("TabContainer.OpenConnection",
                    "ABORT create returned incomplete id=" + (config.Id ?? "") +
                    " opened=" + (opened != null) +
                    " page=" + (opened != null && opened.Page != null) +
                    " session=" + (opened != null && opened.Session != null)); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                return;
            }

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
                    "opened id=" + (config.Id ?? "") + " host=" + (config.Host ?? "") +
                    " proto=" + config.Protocol);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        public void OpenLocalTerminal(string shellPath = null)
        {
            try { DiagLog.Info("TabContainer.OpenLocalTerminal",
                "shellPath=" + (shellPath ?? "<default>")); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            var opened = _opener.CreateLocal(shellPath);
            if (opened == null)
            {
                try { DiagLog.Info("TabContainer.OpenLocalTerminal", "ABORT create returned null"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                return;
            }
            _sessions[opened.Page] = opened.Session;
            _tabControl.TabPages.Add(opened.Page);
            RaiseTabCountChanged();
            _tabControl.SelectedTab = opened.Page;
            ForceActivateSession(opened.Page);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            try { DiagLog.Info("TabContainer.OpenLocalTerminal", "opened"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        public void OpenSftpBrowser(ConnectionConfig config)
        {
            try { DiagLog.Info("TabContainer.OpenSftpBrowser",
                "id=" + (config != null ? config.Id ?? "" : "<null>")); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            var opened = _opener.CreateSftp(config);
            if (opened == null)
            {
                try { DiagLog.Info("TabContainer.OpenSftpBrowser", "ABORT create returned null"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                return;
            }
            _sessions[opened.Page] = opened.Session;
            var panel = opened.Session != null ? opened.Session.Control as SftpDualPanePanel : null;
            if (panel != null) _sftpPanels.Add(panel);
            _tabControl.TabPages.Add(opened.Page);
            RaiseTabCountChanged();
            _tabControl.SelectedTab = opened.Page;
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            try { DiagLog.Info("TabContainer.OpenSftpBrowser", "opened"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        private void HandleTerminalConnected(TabPage tab, TerminalControl terminalControl, ConnectionConfig config)
        {
            if (tab != null && _sessions.TryGetValue(tab, out var ts))
            {
                ts.IsConnected = true;
                WireHealthAndReconnect(ts, terminalControl != null ? terminalControl.Session : null);
            }
            _lifecycle.TryRunLogonScript(terminalControl, config);
            // 补全数据源下发：历史 + 快捷命令（缺省则只用内置）
            try
            {
                if (terminalControl != null)
                {
                    terminalControl.HistoryStore = _historyStore;
                    terminalControl.QuickStore = _quickStore;
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            // 高亮规则下发：连上即按当前规则染 Lightweight 新行
            try
            {
                if (terminalControl != null && _highlightStore != null)
                {
                    var cfg = _highlightStore.Load();
                    if (cfg != null) terminalControl.SetHighlightRules(cfg.Rules);
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }

            // 订阅终端右键菜单事件，转发给顶层订阅者（MainForm 调起面板）。
            if (terminalControl != null)
            {
                terminalControl.SearchRequested += (s, e) => SearchRequested?.Invoke(terminalControl, e);
                // 右键「断开」：宿主先摘看门狗与健康监控，控件随后拆会话（顺序不能反，
                // 否则 session.Disconnected 会先喂给看门狗，1s 后自动重连把用户的选择顶掉）。
                terminalControl.DisconnectRequested += (s, e) => HandleUserDisconnect(tab, terminalControl);
                terminalControl.ReconnectRequested += (s, e) =>
                {
                    // 直接走当前活动标签重连；重连路径已 awaited。
                    try { ReconnectActiveTab(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                    ReconnectRequested?.Invoke(terminalControl, e);
                };
                terminalControl.ExportRequested += (s, e) => ExportRequested?.Invoke(terminalControl, e);
                terminalControl.AppearanceSettingsRequested += (s, e) => AppearanceSettingsRequested?.Invoke(terminalControl, e);
                terminalControl.HostPortOpenRequested += (s, e) => HostPortOpenRequested?.Invoke(terminalControl, e);
                // P0-1：OSC 7 上报的 cwd → 同主机 SFTP 双栏跟随
                terminalControl.DirectoryChanged += (s, path) => FollowSftpTo(terminalControl, path, false);
                // P1-6：Ctrl+Click 远端路径 → 有 SFTP 就跳过去，没有就开一个
                terminalControl.RemotePathOpenRequested += (s, path) => HandleRemotePathOpen(terminalControl, path);
                // P1-7：触发器要求切标签（标题包含关键字就切过去）
                terminalControl.TriggerTabSwitchRequested += (s, keyword) => ActivateTabByTitle(keyword);
                // P0-3：OSC 9/777/99 → 通知中心 + 托盘气泡
                terminalControl.NotificationReceived += (s, n) =>
                {
                    try
                    {
                        string title = string.IsNullOrEmpty(n.Title) ? "NOTIFY" : n.Title;
                        NotificationCenterPanel.Push(title, n.Body);
                        // 气泡：error/warn 更显眼（pushToCenter=false，避免通知中心重复记两条）
                        bool urgent = string.Equals(n.Urgency, "error", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(n.Urgency, "warn", StringComparison.OrdinalIgnoreCase);
                        var lvl = string.Equals(n.Urgency, "error", StringComparison.OrdinalIgnoreCase)
                            ? ToastNotifier.Level.Error
                            : (urgent ? ToastNotifier.Level.Warning : ToastNotifier.Level.Info);
                        ToastNotifier.Show(n.ToString(), lvl, urgent ? 5000 : 3500, pushToCenter: false);
                    }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                    try { NotificationReceived?.Invoke(terminalControl, n); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                };
                // 终端尺寸/编码变化转发到状态栏
                terminalControl.TerminalInfoChanged += (s, size) =>
                {
                    try { TerminalInfoChanged?.Invoke(terminalControl, size); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                };
                // 连上后立即推送一次尺寸 + 编码给状态栏
                try
                {
                    var info = terminalControl.GetCurrentTerminalInfo();
                    TerminalInfoChanged?.Invoke(terminalControl, info);
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            }
        }

        /// <summary>终端切目录 → 同主机 SFTP 双栏跟过去（MobaXterm「follow terminal folder」）。</summary>
        private void FollowSftpTo(TerminalControl source, string path, bool force)
        {
            if (source == null || string.IsNullOrEmpty(path)) return;
            string host = null;
            try { host = source.Config != null ? source.Config.Host : null; } catch { }
            if (string.IsNullOrEmpty(host)) return;
            PruneSftpPanels();
            for (int i = 0; i < _sftpPanels.Count; i++)
            {
                var panel = _sftpPanels[i];
                try
                {
                    if (panel == null || !panel.MatchesHost(host)) continue;
                    panel.FollowTo(path, force);
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.FollowSftp", exSwallowed); } catch { } }
            }
        }

        private void PruneSftpPanels()
        {
            for (int i = _sftpPanels.Count - 1; i >= 0; i--)
            {
                var panel = _sftpPanels[i];
                if (panel == null || panel.IsDisposed) _sftpPanels.RemoveAt(i);
            }
        }

        /// <summary>Ctrl+Click 到远端路径：已有同主机 SFTP 就跳，否则新开一个 SFTP 标签并落位。</summary>
        private void HandleRemotePathOpen(TerminalControl source, string path)
        {
            if (source == null || string.IsNullOrEmpty(path)) return;
            PruneSftpPanels();
            string host = null;
            try { host = source.Config != null ? source.Config.Host : null; } catch { }
            bool any = false;
            for (int i = 0; i < _sftpPanels.Count; i++)
            {
                var panel = _sftpPanels[i];
                if (panel == null || !panel.MatchesHost(host)) continue;
                any = true;
                try { panel.FollowTo(path, true); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.RemotePathOpen", exSwallowed); } catch { } }
            }
            if (!any)
            {
                var cfg = source.Config;
                if (cfg == null) return;
                OpenSftpBrowser(cfg);
                PruneSftpPanels();
                // 刚建的 panel 会在连上后落到 _pendingFollow（FollowTo 先记下路径）
                for (int i = 0; i < _sftpPanels.Count; i++)
                {
                    var panel = _sftpPanels[i];
                    if (panel == null || !panel.MatchesHost(host)) continue;
                    try { panel.FollowTo(path, true); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.RemotePathOpen", exSwallowed); } catch { } }
                }
            }
        }

        private void HandleRdpConnected(TabPage tab)
        {
            if (tab != null && _sessions.TryGetValue(tab, out var ts))
                ts.IsConnected = true;
        }

        /// <summary>
        /// 用户主动断开：摘掉自动重连监视 + 健康监控，标记标签已断开，留可见提示。
        /// 会话本身由 TerminalControl.DisconnectSession 拆（保住渲染缓冲，可翻阅/重连）。
        /// </summary>
        private void HandleUserDisconnect(TabPage tab, TerminalControl terminalControl)
        {
            if (tab == null || terminalControl == null) return;
            TabSessionState ts;
            if (_sessions.TryGetValue(tab, out ts) && ts != null)
            {
                if (!string.IsNullOrEmpty(ts.SessionId))
                {
                    try { _reconnectWatchdog?.Unwatch(ts.SessionId); }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.UserDisconnect.Unwatch", exSwallowed); } catch { } }
                }
                try { ts.HealthMonitor?.Dispose(); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.UserDisconnect.Monitor", exSwallowed); } catch { } }
                ts.HealthMonitor = null;
                ts.IsConnected = false;
            }
            try { DiagLog.Info("TabContainer.UserDisconnect", "host=" + (terminalControl.Config != null ? terminalControl.Config.Host : "-")); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.UserDisconnect", exSwallowed); } catch { } }
            try { ToastNotifier.Show(L10n.T("已断开连接"), ToastNotifier.Level.Info, 2500, pushToCenter: false); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.UserDisconnect.Toast", exSwallowed); } catch { } }
            try { ActiveSessionChanged?.Invoke(this, EventArgs.Empty); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainer.UserDisconnect", exSwallowed); } catch { } }
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
            try { ActiveSessionChanged?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        private void CloseTab(TabPage tab)
        {
            var sessionId = _closeService.CloseTab(tab, _sessions, _tabControl);
            if (!string.IsNullOrEmpty(sessionId))
            {
                try { SessionClosed?.Invoke(this, sessionId); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            }
            try { ActiveSessionChanged?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        private void OnDrawTab(object sender, DrawItemEventArgs e)
        {
            _chrome.DrawTab(e, _tabControl);
        }

        /// <summary>L1：悬停跟踪——关闭钮仅悬停/选中时绘制，需要知道鼠标在哪个标签上。</summary>
        private void OnTabMouseMove(object sender, MouseEventArgs e)
        {
            int idx = -1;
            for (int i = 0; i < _tabControl.TabCount; i++)
                if (_tabControl.GetTabRect(i).Contains(e.Location)) { idx = i; break; }

            // 审计 C5：关闭钮可点时要给出手型光标（原先整条标签栏都是默认箭头，
            // 用户不知道“×”是可点的）。命中区与 HitTestClose 的第二级一致。
            try
            {
                var overClose = idx >= 0 && _chrome.HitTestClose(_tabControl, e.Location) != null;
                var want = overClose ? Cursors.Hand : Cursors.Default;
                if (_tabControl.Cursor != want) _tabControl.Cursor = want;
            }
            catch { }

            if (idx != _chrome.HoverIndex)
            {
                _chrome.HoverIndex = idx;
                try { _tabControl.Invalidate(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            }
        }

        private void OnTabMouseLeave(object sender, EventArgs e)
        {
            if (_chrome.HoverIndex != -1)
            {
                _chrome.HoverIndex = -1;
                try { _tabControl.Invalidate(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            }
        }

        /// <summary>L1 弹性宽：标签宽随文本长度与可视宽自适应（90~180 设计 px），每次增删/缩放重算。</summary>
        private void ApplyElasticTabWidth()
        {
            try
            {
                if (_tabControl == null || _tabControl.IsDisposed) return;
                var dpi = DpiScale.Factor(this);
                int designW = _chrome.ComputeTabWidth(_tabControl, _tabControl.Font, dpi);
                int px = (int)Math.Round(designW * dpi);
                int h = (int)Math.Round(24 * dpi);
                var cur = _tabControl.ItemSize;
                if (cur.Width != px || cur.Height != h)
                    _tabControl.ItemSize = new Size(px, h);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        /// <summary>右键菜单条目 + 手绘图标（失败回退纯文本）。</summary>
        private static void AddCtxItem(ContextMenuStrip menu, string text, string icon, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick);
            try { var img = Gdterm.UI.Services.MenuIconFactory.Get(icon); if (img != null) item.Image = img; }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            menu.Items.Add(item);
        }

        private void OnTabMouseDown(object sender, MouseEventArgs e)
        {
            // 双击标签栏空白处 = 新建连接（浏览器惯例）；限定在标签头行内，
            // 避免点下方页面区域误触发。无标签时整个控件都是空白区。
            if (e.Button == MouseButtons.Left && e.Clicks >= 2)
            {
                bool onHeaderRow = _tabControl.TabCount == 0 || e.Y <= _tabControl.GetTabRect(0).Bottom + 2;
                bool onTab = false;
                for (int i = 0; i < _tabControl.TabCount; i++)
                    if (_tabControl.GetTabRect(i).Contains(e.Location)) { onTab = true; break; }
                if (onHeaderRow && !onTab)
                {
                    try { BlankAreaDoubleClicked?.Invoke(this, EventArgs.Empty); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                    return;
                }
            }

            // 右键：选中所点标签 + 弹右键菜单（参考 Xshell/MobaXterm）
            if (e.Button == MouseButtons.Right)
            {
                for (int i = 0; i < _tabControl.TabCount; i++)
                {
                    if (_tabControl.GetTabRect(i).Contains(e.Location))
                    {
                        try { _tabControl.SelectedIndex = i; } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                        try { _tabContextMenu.Show(_tabControl, e.Location); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                        return;
                    }
                }
                return;
            }

            // 中键：关闭所点标签（浏览器/VS Code 肌肉记忆）
            if (e.Button == MouseButtons.Middle)
            {
                for (int i = 0; i < _tabControl.TabCount; i++)
                {
                    if (_tabControl.GetTabRect(i).Contains(e.Location))
                    {
                        CloseTab(_tabControl.TabPages[i]);
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

        /// <summary>循环切换标签：delta=+1 下一个，-1 上一个（首尾环绕）。</summary>
        public void CycleTab(int delta)
        {
            int count = _tabControl.TabCount;
            if (count < 2) return;
            int idx = ((_tabControl.SelectedIndex + delta) % count + count) % count;
            _tabControl.SelectedIndex = idx;
        }

        /// <summary>
        /// 自绘命中区（审计 C5/C7-③）：每个标签的关闭钮命中区（屏幕坐标）。
        /// 几何口径与 HitTestClose 一致（GetCloseHitRect，≥24×24 逻辑 px）；
        /// 命中测试只在选中/悬停标签生效，但几何对所有标签恒定，故全量上报供 R1 判尺寸。
        /// </summary>
        public System.Collections.Generic.List<Gdterm.UI.Services.HitRegion> GetHitRegions()
        {
            var list = new System.Collections.Generic.List<Gdterm.UI.Services.HitRegion>();
            if (_tabControl == null || !Visible) return list;
            try
            {
                var dpi = DpiScale.Factor(_tabControl);
                for (int i = 0; i < _tabControl.TabCount; i++)
                {
                    var local = Gdterm.UI.Services.TabChromePainter.GetCloseHitRect(
                        _tabControl.GetTabRect(i), dpi);
                    list.Add(new Gdterm.UI.Services.HitRegion(
                        "tab-close:" + (string.IsNullOrEmpty(_tabControl.TabPages[i].Text)
                            ? ("tab" + i) : _tabControl.TabPages[i].Text),
                        _tabControl.RectangleToScreen(local)));
                }
            }
            catch { return list; }
            return list;
        }

        /// <summary>直达第 index 个标签（0 基，越界忽略）。</summary>
        /// <summary>按标题关键字切标签（触发器用）。找不到就忽略——不弹窗打扰。</summary>
        public void ActivateTabByTitle(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword) || _tabControl == null) return;
            try
            {
                foreach (TabPage tab in _tabControl.TabPages)
                {
                    if (tab == null) continue;
                    if (tab.Text != null && tab.Text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _tabControl.SelectedTab = tab;
                        return;
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl.ActivateTabByTitle", exSwallowed); } catch { } }
        }

        public void ActivateTabIndex(int index)
        {
            if (index < 0 || index >= _tabControl.TabCount) return;
            _tabControl.SelectedIndex = index;
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
                try { state.Credential?.ClearSecrets(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                state.Credential = null;
                var terminals = new System.Collections.Generic.List<TerminalControl>();
                TabActiveSessionQuery.CollectSessionTerminals(state, terminals);
                foreach (var tc in terminals)
                {
                    if (tc == null) continue;
                    try { tc.ClearCachedCredentials(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
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
                    try { tc.ApplyCurrentAppearance(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
                }
            }
        }

        /// <summary>解锁后重新武装所有健康监控（go-live P1-03）。</summary>
        public void RearmAllHealthMonitors()
        {
            foreach (var kvp in _sessions)
            {
                try { kvp.Value?.HealthMonitor?.Rearm(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
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
            try { session?.HealthMonitor?.RecordReconnect(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
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
            ApplyElasticTabWidth(); // L1：标签数变化 → 重算弹性宽
            try
            {
                var h = TabCountChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
        }

        public int ActiveTabIndex
        {
            get { return _tabControl.SelectedIndex; }
        }

        // ========== 标签重命名 / 颜色（P2） ==========

        /// <summary>标签强调色预设（顺序与右键菜单一致；色值收在 GdtermColorTable）。</summary>
        public static int[] TabAccentPalette
        {
            get { return Gdterm.UI.Diagnostics.GdtermColorTable.TabAccentPalette; }
        }

        private void AddColorItem(ToolStripMenuItem parent, string text, int? argb)
        {
            var item = new ToolStripMenuItem(text);
            if (argb.HasValue)
            {
                try
                {
                    var swatch = new Bitmap(DpiScale.V(this, 12), DpiScale.V(this, 12));
                    using (var g = Graphics.FromImage(swatch))
                    using (var b = new SolidBrush(Gdterm.UI.Diagnostics.GdtermColorTable.FromToken(argb.Value)))
                        g.FillRectangle(b, 0, 0, swatch.Width, swatch.Height);
                    item.Image = swatch;
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            }
            item.Click += (s, e) => SetActiveTabAccent(argb);
            parent.DropDownItems.Add(item);
        }

        /// <summary>右键入口：弹输入框改名；取消（null）则什么都不改，空串=恢复连接名。</summary>
        /// <summary>右键/命令面板入口：给当前活动标签改名。</summary>
        public void PromptRenameActiveTab()
        {
            try
            {
                var page = _tabControl != null ? _tabControl.SelectedTab : null;
                if (page == null) return;
                TabDecoration dec;
                _decorations.TryGetValue(page, out dec);
                var current = dec != null && !string.IsNullOrEmpty(dec.Title) ? dec.Title : page.Text;
                var name = Gdterm.UI.Controls.TextPromptDialog.Ask(FindForm(), L10n.T("重命名标签"),
                    L10n.T("标签名称（留空恢复连接名）"), current, L10n.T("只改这个标签的显示名，不影响连接配置"));
                if (name == null) return;
                RenameTab(page, name);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl.Rename", exSwallowed); } catch { } }
        }

        /// <summary>给某个标签改名。title 为空 → 恢复连接名（不做"空标签"）。</summary>
        public void RenameTab(TabPage page, string title)
        {
            if (page == null) return;
            TabDecoration dec;
            if (!_decorations.TryGetValue(page, out dec)) { dec = new TabDecoration { BaseTitle = page.Text }; _decorations[page] = dec; }
            var trimmed = (title ?? "").Trim();
            if (trimmed.Length == 0)
            {
                dec.Title = null;
                page.Text = DefaultTabTitle(page);
            }
            else
            {
                dec.Title = trimmed;
                page.Text = trimmed;
            }
            ApplyElasticTabWidth();   // 标题长度变了，弹性宽必须重算，否则长名字被裁
            try { _tabControl.Invalidate(); } catch { }
        }

        /// <summary>给某个标签设强调色（null=跟随主题）。</summary>
        public void SetTabAccent(TabPage page, int? argb)
        {
            if (page == null) return;
            TabDecoration dec;
            if (!_decorations.TryGetValue(page, out dec)) { dec = new TabDecoration { BaseTitle = page.Text }; _decorations[page] = dec; }
            dec.AccentArgb = argb;
            try { _tabControl.Invalidate(); } catch { }
        }

        /// <summary>当前活动标签设强调色（菜单/命令面板用）。</summary>
        public void SetActiveTabAccent(int? argb)
        {
            var page = _tabControl != null ? _tabControl.SelectedTab : null;
            if (page != null) SetTabAccent(page, argb);
        }

        /// <summary>
        /// 重画标签徽标（录制红点等）。录制状态**不另存一份**、每次绘制现读会话里的
        /// TerminalControl.IsSessionRecording——存副本就一定会飘（停止录制忘了清、
        /// 标签关了没删、分屏里第二个终端在录看不到），而录制状态是"现在到底有没有在写盘"这么硬的事实。
        /// </summary>
        public void RefreshTabBadges()
        {
            try { if (_tabControl != null) _tabControl.Invalidate(); } catch { }
        }

        /// <summary>某标签是否在录制（供绘制器读取；分屏下任一终端在录即为真）。</summary>
        internal bool IsTabRecording(TabPage page)
        {
            TabSessionState session;
            if (page == null || !_sessions.TryGetValue(page, out session) || session == null) return false;
            return IsRecordingIn(session.Control) ||
                   (session.PrimaryTerminal != null && session.PrimaryTerminal.IsSessionRecording);
        }

        private static bool IsRecordingIn(Control root)
        {
            if (root == null) return false;
            var terminal = root as TerminalControl;
            if (terminal != null) return terminal.IsSessionRecording;
            try
            {
                foreach (Control child in root.Controls)
                    if (IsRecordingIn(child)) return true;
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl.IsRecordingIn", exSwallowed); } catch { } }
            return false;
        }

        /// <summary>某标签的强调色（供绘制器读取；未设为 null）。</summary>
        internal int? GetTabAccent(TabPage page)
        {
            TabDecoration dec;
            if (page != null && _decorations.TryGetValue(page, out dec)) return dec.AccentArgb;
            return null;
        }

        /// <summary>标签"本该叫什么"：优先连接名，没有会话（SFTP/RDP 等）则保留创建时的标题。</summary>
        private string DefaultTabTitle(TabPage page)
        {
            TabSessionState session;
            if (page != null && _sessions.TryGetValue(page, out session) &&
                session.Config != null && !string.IsNullOrEmpty(session.Config.Name))
                return session.Config.Name;
            TabDecoration dec;
            if (page != null && _decorations.TryGetValue(page, out dec) && !string.IsNullOrEmpty(dec.BaseTitle))
                return dec.BaseTitle;
            return page != null ? page.Text : "";
        }

        /// <summary>
        /// 重启后复原标签装饰：按连接 Id 找标签，套回标题与颜色。
        /// 找不到（连接已删/没开）静默跳过——恢复是尽力而为，不该弹错。
        /// </summary>
        public void RestoreTabDecoration(string connectionId, string title, int? accentArgb)
        {
            if (string.IsNullOrEmpty(connectionId)) return;
            foreach (TabPage tab in _tabControl.TabPages)
            {
                TabSessionState session;
                if (!_sessions.TryGetValue(tab, out session) || session.Config == null) continue;
                if (session.Config.Id != connectionId) continue;
                if (!string.IsNullOrEmpty(title)) RenameTab(tab, title);
                if (accentArgb.HasValue) SetTabAccent(tab, accentArgb);
                return;
            }
        }

        public List<OpenTabState> GetOpenTabStates()
        {
            var result = new List<OpenTabState>();
            foreach (TabPage tab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(tab, out var session) && session.Config != null)
                {
                    TabDecoration dec;
                    _decorations.TryGetValue(tab, out dec);
                    result.Add(new OpenTabState
                    {
                        ConnectionId = session.Config.Id,
                        // 存"屏幕上真正的标题"而不是连接名：用户改过名字，重启后应该还是那个名字
                        Title = tab.Text,
                        AccentColor = dec != null && dec.AccentArgb.HasValue ? dec.AccentArgb.Value : 0,
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

        /// <summary>命令面板用：按连接 Id 激活标签（GetOpenTabStates 的顺序与 TabPage 下标可能不一致）。</summary>
        public bool ActivateTabByConnectionId(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId)) return false;
            foreach (TabPage tab in _tabControl.TabPages)
            {
                if (_sessions.TryGetValue(tab, out var session) &&
                    session.Config != null &&
                    session.Config.Id == connectionId)
                {
                    _tabControl.SelectedTab = tab;
                    return true;
                }
            }
            return false;
        }

        public TerminalControl GetActiveTerminalControl() => _activeQuery.GetActiveTerminalControl();
        public ITerminalSession GetActiveSession() => _activeQuery.GetActiveSession();

        /// <summary>SessionId → 可显示名（连接名或 Host）。重连状态提示用（A1）。</summary>
        public string TryGetDisplayName(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return sessionId;
            try
            {
                foreach (var kv in _sessions)
                {
                    var ts = kv.Value;
                    if (ts != null && ts.SessionId != null &&
                        string.Equals(ts.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        var cfg = ts.Config;
                        if (cfg != null)
                            return string.IsNullOrEmpty(cfg.Name) ? cfg.Host : cfg.Name;
                        break;
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } }
            return sessionId;
        }

        /// <summary>该 SessionId 是否为当前选中标签的会话（状态栏只跟活动标签，A1）。</summary>
        public bool IsActiveSessionId(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            try
            {
                var tab = _tabControl != null ? _tabControl.SelectedTab : null;
                TabSessionState ts;
                if (tab == null || !_sessions.TryGetValue(tab, out ts) || ts == null) return false;
                return ts.SessionId != null &&
                    string.Equals(ts.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TabContainerControl", exSwallowed); } catch { } return false; }
        }
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
