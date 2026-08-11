using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Rdp;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;

namespace Gdterm.UI.Forms
{
    public enum ViewMode
    {
        Standard,
        Focus,
        Compact
    }

    /// <summary>
    /// 组合根 + 布局壳。业务逻辑在 Services/（finding-10）。
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly IConnectionStore _connectionStore;
        private readonly ITunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly IRdpClientFactory _rdpFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IKeePassService _keepassService;
        private readonly IAuditLogger _auditLogger;
        private readonly IAiAssistantService _aiService;
        private readonly ISecurityManager _securityManager;
        private readonly DangerousCommandDetector _dangerousCmdDetector;
        private readonly IFolderCredentialStore _folderCredStore;
        private readonly SessionStateStore _sessionStore;
        private readonly IBookmarkStore _bookmarkStore;
        private readonly CommandHistoryStore _commandHistoryStore;
        private readonly QuickCommandStore _quickCommandStore;
        private readonly TerminalKeyBindingStore _keyBindingStore;
        private readonly HighlightStore _highlightStore;
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly MultiChannelManager _multiChannelManager;
        private readonly ToolRegistry _toolRegistry;
        private readonly SecretScanner _secretScanner;

        private ConnectionTreeControl _connectionTree;
        private TabContainerControl _tabContainer;
        private ActiveSessionBridge _sessionBridge;
        private SidePanelFactory _sidePanels;
        private SidePanelHost _sidePanelHost;
        private SessionStateCoordinator _sessionState;
        private ViewModeController _viewMode;
        private ToolsDialogsLauncher _toolsDialogs;
        private GlobalHotkeyController _hotkeys;
        private MainFormCommandRouter _cmdRouter;
        private ConnectionOpenCoordinator _openCoord;
        private LockStateCoordinator _lockCoord;
        private AppShutdownCoordinator _shutdown;
        private StatusBarControl _statusBar;
        private LockOverlayControl _lockOverlay;
        private MenuStrip _menuStrip;
        private QuickBarPanel _quickBar;
        private WelcomePanel _welcomePanel;
        private NotifyIcon _trayIcon;
        private bool _closeToTray = true;

        public MainForm(
            IConnectionStore connectionStore,
            ITunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IKeePassService keepassService,
            IAuditLogger auditLogger,
            IAiAssistantService aiService,
            ISecurityManager securityManager,
            DangerousCommandDetector dangerousCmdDetector,
            IFolderCredentialStore folderCredStore,
            SessionStateStore sessionStore,
            IRdpClientFactory rdpFactory = null,
            IBookmarkStore bookmarkStore = null,
            CommandHistoryStore commandHistoryStore = null,
            QuickCommandStore quickCommandStore = null,
            TerminalKeyBindingStore keyBindingStore = null,
            HighlightStore highlightStore = null,
            AutoReconnectWatchdog reconnectWatchdog = null,
            MultiChannelManager multiChannelManager = null,
            ToolRegistry toolRegistry = null,
            SecretScanner secretScanner = null)
        {
            _connectionStore = connectionStore;
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _rdpFactory = rdpFactory ?? new RdpClientFactory();
            _sftpFactory = sftpFactory;
            _keepassService = keepassService;
            _auditLogger = auditLogger;
            _aiService = aiService;
            _securityManager = securityManager;
            _dangerousCmdDetector = dangerousCmdDetector;
            _folderCredStore = folderCredStore;
            _sessionStore = sessionStore;
            _bookmarkStore = bookmarkStore;
            _commandHistoryStore = commandHistoryStore;
            _quickCommandStore = quickCommandStore;
            _keyBindingStore = keyBindingStore;
            _highlightStore = highlightStore;
            _reconnectWatchdog = reconnectWatchdog;
            _multiChannelManager = multiChannelManager ?? new MultiChannelManager();
            _toolRegistry = toolRegistry;
            _secretScanner = secretScanner;

            // 消除重绘闪烁：DoubleBuffered + 指定一致暗色背景。
            // 这是「发虚」的第二根因——重绘时 GDI 一帧帧走，看起来不稳重。
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
            ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point, 134);

            if (_reconnectWatchdog != null)
            {
                _reconnectWatchdog.ReconnectFailed += (s, e) =>
                {
                    try
                    {
                        _auditLogger?.LogSecurityEvent(
                            SecurityEvent.ApplicationError,
                            "reconnect failed session=" + (e.SessionId ?? "") +
                            " retries=" + e.RetryCount +
                            " err=" + (e.ErrorMessage ?? ""));
                    }
                    catch { }
                };
            }

            InitializeComponent();
            LoadAppIcon();
            SetupEventHandlers();
            if (!_securityManager.IsLocked && _lockOverlay != null)
                _lockOverlay.Visible = false;

            Shown += (s, e) =>
            {
                try { _sessionState?.Restore(); } catch { }
            };
        }

        private void LoadAppIcon()
        {
            try
            {
                var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("Gdterm.UI.Resources.gdterm.ico");
                if (iconStream != null)
                {
                    this.Icon = new Icon(iconStream);
                    iconStream.Dispose();
                }
            }
            catch { }
        }

        private void InitializeComponent()
        {
            Text = "gdterm - 绿色运维客户端";
            KeyPreview = true; // Esc/F11 在终端焦点时也能回到主窗体
            // app.manifest 声明 PerMonitorV2 让 WinForms 按屏自动缩放；不要在这里再叠手工 Scale，
            // 否则控件会被缩放两次导致字号超大、布局错乱（DpiHelper 已废弃）。
            Size = new Size(1200, 800);
            MinimumSize = new Size(800, 600);
            StartPosition = FormStartPosition.CenterScreen;

            _connectionTree = new ConnectionTreeControl(_connectionStore, _keepassService);
            _connectionTree.Dock = DockStyle.Left;
            _connectionTree.Width = 250;

            var mainSplitter = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 4,
                BackColor = Color.FromArgb(60, 60, 60)
            };

            _tabContainer = new TabContainerControl(
                _tunnelManager,
                _terminalFactory,
                _sftpFactory,
                _aiService,
                _auditLogger,
                _keepassService,
                _folderCredStore,
                _dangerousCmdDetector,
                _reconnectWatchdog,
                _connectionStore,
                _rdpFactory);
            _sessionBridge = new ActiveSessionBridge(_tabContainer);
            _sidePanels = new SidePanelFactory(
                _tabContainer,
                _sessionBridge,
                _toolRegistry,
                _secretScanner,
                _multiChannelManager,
                _dangerousCmdDetector,
                _auditLogger,
                _commandHistoryStore,
                _highlightStore,
                _keyBindingStore,
                _quickCommandStore,
                _bookmarkStore,
                _connectionStore,
                this,
                _securityManager);
            _tabContainer.Dock = DockStyle.Fill;
            _tabContainer.ActiveSessionChanged += OnActiveSessionChanged;
            _tabContainer.SessionClosed += OnSessionClosed;
            _tabContainer.SearchRequested += (s, e) => _sidePanels?.AttachSearchBar(_tabContainer);
            _tabContainer.ExportRequested += (s, e) => ExportActiveTerminalBuffer();
            _tabContainer.AppearanceSettingsRequested += (s, e) => _toolsDialogs.OpenAppearanceSettings();
            // 终端尺寸/编码变化→状态栏显示
            _tabContainer.TerminalInfoChanged += (s, size) =>
            {
                try
                {
                    var tc = s as Gdterm.UI.Controls.TerminalControl;
                    var enc = tc != null ? tc.CurrentEncoding : "UTF-8";
                    _statusBar?.UpdateTerminalInfo(size.Width, size.Height, enc);
                }
                catch { }
            };
            // ReconnectRequested 已由 TabContainerControl 内部直走 ReconnectActiveTab，无需重复。

            AiCommandGateBinder.Bind(
                _aiService,
                () => _tabContainer.GetActiveTerminalControl(),
                _dangerousCmdDetector,
                this);

            var sideHostPanel = SidePanelHost.CreateHost((s, e) => _sidePanelHost?.Hide());
            _sidePanelHost = new SidePanelHost(sideHostPanel);

            var sideSplitter = new Splitter
            {
                Dock = DockStyle.Right,
                Width = 4,
                BackColor = Color.FromArgb(60, 60, 60)
            };

            _statusBar = new StatusBarControl(
                _tunnelManager,
                _keepassService,
                _aiService,
                _securityManager);
            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = 25;

            List<QuickCommand> cmds = null;
            try { cmds = _quickCommandStore?.LoadAll(); } catch { }
            _quickBar = new QuickBarPanel(cmds ?? new List<QuickCommand>());
            _quickBar.Dock = DockStyle.Bottom;
            _quickBar.Height = 36;
            _quickBar.CommandSent += (cmd, group) =>
            {
                var tc = _tabContainer.GetActiveTerminalControl();
                if (tc == null) return;
                var line = cmd.EndsWith("\r") || cmd.EndsWith("\n") ? cmd : cmd + "\r";
                tc.SendInput(line);
            };

            _lockOverlay = new LockOverlayControl(_securityManager);
            _lockOverlay.Dock = DockStyle.Fill;
            _lockOverlay.Visible = _securityManager.IsLocked;

            _toolsDialogs = new ToolsDialogsLauncher(
                this, _securityManager, _keepassService, _dangerousCmdDetector,
                () => _tabContainer.ApplyAppearanceToAllTerminals());

            _openCoord = new ConnectionOpenCoordinator(
                _tabContainer, _connectionStore, _bookmarkStore, _connectionTree, this, _keepassService);

            var menuBuilt = new MainFormMenuBuilder().Build(new MainFormMenuBuilder.Callbacks
            {
                NewConnection = (s, e) => _openCoord.NewConnection(),
                ImportConnections = (s, e) => ConnectionImportExportUi.Import(this, _connectionStore, () => _connectionTree.LoadConnections()),
                ExportConnections = (s, e) => ConnectionImportExportUi.Export(this, _connectionStore),
                Exit = (s, e) => Close(),
                OpenLocalTerminal = (s, e) => _tabContainer.OpenLocalTerminal(),
                OpenSftp = (s, e) => _openCoord.OpenSftpFromActive(),
                ReconnectActive = (s, e) => _tabContainer.ReconnectActiveTab(),
                CloseActive = (s, e) => _tabContainer.CloseActiveTab(),
                ViewStandard = (s, e) => _viewMode?.SetViewMode(ViewMode.Standard),
                ViewFocus = (s, e) => _viewMode?.SetViewMode(ViewMode.Focus),
                ViewCompact = (s, e) => _viewMode?.SetViewMode(ViewMode.Compact),
                ToggleTree = (s, e) => _viewMode?.ToggleConnectionTree(),
                ToggleTreePin = (s, e) =>
                {
                    try { _connectionTree?.TogglePin(); }
                    catch { }
                },
                SplitHorizontal = (s, e) => _tabContainer.SplitHorizontal(),
                SplitVertical = (s, e) => _tabContainer.SplitVertical(),
                ToggleQuickBar = (s, e) =>
                {
                    if (_quickBar != null) _quickBar.Visible = !_quickBar.Visible;
                },
                ShowSearch = (s, e) => _sidePanels?.AttachSearchBar(_tabContainer),
                ShowSnippet = (s, e) => _sidePanelHost?.ShowSnippetSearch(_sidePanels, _tabContainer),
                ShowHighlight = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateHighlightPanel()),
                ShowKeyBinding = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateKeyBindingPanel()),
                ShowLogonScript = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateLogonScriptPanel()),
                ShowMultiChannel = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateMultiChannelPanel()),
                ShowBatch = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateBatchPanel()),
                ShowHistory = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateHistoryPanel()),
                ShowHealth = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateHealthPanel()),
                ShowPortForward = (s, e) => _sidePanelHost?.Show(_sidePanels.CreatePortForwardPanel()),
                ShowToolbox = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateToolboxPanel()),
                ShowSecretScan = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateSecretScanPanel()),
                ShowBookmarks = (s, e) => _sidePanelHost?.Show(_sidePanels.CreateBookmarksPanel(cfg =>
                {
                    if (cfg != null) _openCoord.OpenConnection(cfg);
                })),
                KeePassManager = (s, e) => _toolsDialogs.OpenKeePassManager(),
                PasswordHealth = (s, e) => _toolsDialogs.OpenPasswordHealth(),
                PasswordGenerator = (s, e) => _toolsDialogs.OpenPasswordGenerator(),
                ChangeMasterPassword = (s, e) => _toolsDialogs.OpenChangeMasterPassword(),
                AppearanceSettings = (s, e) => _toolsDialogs.OpenAppearanceSettings(),
                AiSettings = (s, e) => _toolsDialogs.OpenAiSettings(),
                DangerousCmdSettings = (s, e) => _toolsDialogs.OpenDangerousCmdSettings(),
                ShowHotkeys = (s, e) => _toolsDialogs.ShowHotkeysHelp(),
                About = (s, e) => _toolsDialogs.ShowAbout(),
                SshKeyManager = (s, e) => { try { _toolsDialogs?.OpenSshKeyManager(); } catch (Exception ex) { DiagLog.Swallowed("MainForm.SshKey", ex); } },
                ShowTransferCenter = (s, e) => { try { _sidePanelHost?.Show(_sidePanels?.CreateTransferCenterPanel()); } catch { } },
                ShowNotificationCenter = (s, e) => { try { _sidePanelHost?.Show(_sidePanels?.CreateNotificationCenterPanel()); } catch { } },
                QuickJump = (s, e) => OpenQuickJump()
            });
            _menuStrip = menuBuilt.Menu;
            MainMenuStrip = _menuStrip;
            // ManagerRenderMode 让 ToolStripManager.Renderer 生效（全局 GdtermToolStripRenderer）。
            try
            {
                _menuStrip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                _menuStrip.BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
                _menuStrip.ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            }
            catch { }

            _viewMode = new ViewModeController(
                _connectionTree,
                _statusBar,
                _menuStrip,
                _quickBar,
                () => _sidePanelHost?.Hide(),
                menuBuilt.ViewStandardItem,
                menuBuilt.ViewFocusItem,
                menuBuilt.ViewCompactItem,
                host: this);

            _cmdRouter = new MainFormCommandRouter(
                _tabContainer, _sidePanels, _sidePanelHost, _viewMode);

            // Toast / 落地页 / 托盘
            try { ToastNotifier.Bind(this); } catch { }
            try { SetupWelcomePanel(); } catch (Exception ex) { DiagLog.Swallowed("MainForm.Welcome", ex); }
            try { SetupTrayIcon(); } catch (Exception ex) { DiagLog.Swallowed("MainForm.Tray", ex); }
            try
            {
                if (_tabContainer != null)
                {
                    _tabContainer.TabCountChanged += (s, e) => UpdateWelcomeVisibility();
                }
            }
            catch { }

            Controls.Add(_tabContainer);
            Controls.Add(sideSplitter);
            Controls.Add(sideHostPanel);
            Controls.Add(mainSplitter);
            Controls.Add(_connectionTree);
            Controls.Add(_quickBar);
            Controls.Add(_statusBar);
            Controls.Add(_lockOverlay);
            Controls.Add(_menuStrip);
            _lockOverlay.BringToFront();

            // 主界面统一字体（微软雅黑妖会被镜像发给终端，这里只给 UI 侧）。
            try { ApplyGlobalUIFont(); } catch { }

            _sessionState = new SessionStateCoordinator(
                _sessionStore,
                _connectionStore,
                _tabContainer,
                this,
                () => _viewMode != null ? _viewMode.Current : ViewMode.Standard,
                mode => _viewMode?.SetViewMode(mode),
                () => _connectionTree != null ? _connectionTree.Width : 250,
                w => { if (_connectionTree != null) _connectionTree.Width = w; });

            _lockCoord = new LockStateCoordinator(
                this, _securityManager, _keepassService, _lockOverlay,
                _tabContainer, _reconnectWatchdog, _auditLogger);
        }

        public void ApplyGlobalUIFont()
        {
            var ga = Gdterm.UI.Program.GlobalAppearance;
            if (ga == null) return;
            var name = !string.IsNullOrWhiteSpace(ga.UIFontName) ? ga.UIFontName : "Microsoft YaHei UI";
            var size = ga.UIFontSize > 0 ? ga.UIFontSize : 9;
            // manifest PerMonitorV2 已经按 DPI 自动放大字号；这里直接用用户设置的 pt 值，不要再手工 scale。
            Font font;
            try { font = new Font(name, size, FontStyle.Regular); }
            catch { font = new Font("Microsoft YaHei UI", size); }
            try { this.Font = font; } catch { }
            if (_menuStrip != null) try { _menuStrip.Font = font; } catch { }
            if (_statusBar != null) try { _statusBar.Font = font; } catch { }
            if (_connectionTree != null) try { _connectionTree.ApplyUIFont(name, size); } catch { }
            if (_quickBar != null) try { _quickBar.Font = font; } catch { }
        }

        private void SetupEventHandlers()
        {
            _connectionTree.ConnectionDoubleClicked += (s, cfg) => _openCoord.OpenConnection(cfg);
            _connectionTree.OpenLocalTerminalRequested += () =>
            {
                try { _tabContainer?.OpenLocalTerminal(); UpdateWelcomeVisibility(); } catch { }
            };
            _securityManager.LockStateChanged += (s, e) => _lockCoord.Handle(s, e);
            MouseMove += (s, e) => _securityManager.ResetIdleTimer();
            KeyDown += (s, e) =>
            {
                _securityManager.ResetIdleTimer();
                // KeyPreview 备份：终端吃键时仍可 Esc/F11 退出专注
                if (e.KeyCode == Keys.Escape && _viewMode != null && _viewMode.TryHandleEscape())
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.F11 && _viewMode != null)
                {
                    _viewMode.ToggleFocus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            Click += (s, e) => _securityManager.ResetIdleTimer();
            _hotkeys = new GlobalHotkeyController(this);
            _hotkeys.Initialize();
            _shutdown = new AppShutdownCoordinator(
                _sessionState, _hotkeys, _tabContainer,
                _tunnelManager, _keepassService, _securityManager);
            FormClosing += MaybeCloseToTray;
            FormClosing += _shutdown.OnFormClosing;
        }

        private void OnSessionClosed(object sender, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || _multiChannelManager == null) return;
            try { _multiChannelManager.Unregister(sessionId); } catch { }
        }

        private void OnActiveSessionChanged(object sender, EventArgs e)
        {
            var session = _tabContainer.GetActiveSession();
            var tc = _tabContainer.GetActiveTerminalControl();
            var host = tc?.Config?.Host;
            var user = tc?.Config?.Username;
            if (tc != null)
                _quickBar?.SetActiveTerminal(tc, host, user);
            else
                _quickBar?.SetActiveSession(session, host, user);

            try { _sidePanels?.SyncMultiChannelRegistrations(); } catch { }
        }

        /// <summary>
        /// 导出当前活动终端的滚动缓冲到文本或 HTML 文件。
        /// 从右键菜单「导出缓冲」触发；本会话未连接时弹提示。
        /// </summary>
        private void ExportActiveTerminalBuffer()
        {
            try
            {
                var tc = _tabContainer?.GetActiveTerminalControl();
                if (tc == null || !tc.IsConnected)
                {
                    ToastNotifier.Info("当前没有活动的终端会话"); return;
                    return;
                }

                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "导出终端缓冲";
                    dlg.Filter = "文本文件 (*.txt)|*.txt|HTML 文件 (*.html)|*.html|所有文件 (*.*)|*.*";
                    dlg.FileName = "terminal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    var lines = tc.Session?.GetRecentOutput(2000);
                    if (lines == null) lines = new System.Collections.Generic.List<string>();
                    var host = tc.Config?.Host ?? "localhost";
                    string content;
                    if (System.IO.Path.GetExtension(dlg.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase))
                        content = Gdterm.Terminal.TerminalBufferExport.ExportAsHtml(new System.Collections.Generic.List<string>(lines), host);
                    else
                        content = Gdterm.Terminal.TerminalBufferExport.ExportAsText(new System.Collections.Generic.List<string>(lines), host);
                    Gdterm.Terminal.TerminalBufferExport.SaveToFile(content, dlg.FileName);
                    ToastNotifier.Success("已导出缓冲");
                }
            }
            catch (Exception ex)
            {
                Gdterm.UI.Diagnostics.DiagLog.Swallowed("MainForm.ExportBuffer", ex);
                MessageBox.Show(this, "导出失败：" + ex.Message, "导出缓冲",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }


        private void SetupWelcomePanel()
        {
            _welcomePanel = new WelcomePanel(_connectionStore, _bookmarkStore)
            {
                Dock = DockStyle.Fill,
                Visible = false
            };
            _welcomePanel.NewConnectionRequested += () =>
            {
                try { _openCoord?.NewConnection(); } catch { }
            };
            _welcomePanel.OpenLocalTerminalRequested += () =>
            {
                try { _tabContainer?.OpenLocalTerminal(); UpdateWelcomeVisibility(); } catch { }
            };
            _welcomePanel.OpenBookmarksRequested += () =>
            {
                try
                {
                    _sidePanelHost?.Show(_sidePanels.CreateBookmarksPanel(cfg =>
                    {
                        if (cfg != null) _openCoord?.OpenConnection(cfg);
                    }));
                }
                catch { }
            };
            _welcomePanel.OpenKeePassRequested += () =>
            {
                try { _toolsDialogs?.OpenKeePassManager(); } catch { }
            };
            _welcomePanel.OpenConnectionRequested += cfg =>
            {
                try
                {
                    if (cfg != null) _openCoord?.OpenConnection(cfg);
                    UpdateWelcomeVisibility();
                }
                catch { }
            };
            Controls.Add(_welcomePanel);
            _welcomePanel.BringToFront();
            UpdateWelcomeVisibility();
        }

        private void UpdateWelcomeVisibility()
        {
            try
            {
                int tabs = 0;
                try { tabs = _tabContainer != null ? _tabContainer.OpenTabCount : 0; } catch { }
                bool show = tabs <= 0;
                if (_welcomePanel != null && !_welcomePanel.IsDisposed)
                {
                    if (show) _welcomePanel.Reload();
                    _welcomePanel.Visible = show;
                    if (show) _welcomePanel.BringToFront();
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("MainForm.UpdateWelcome", ex); }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Text = "gdterm",
                Visible = true
            };
            try
            {
                if (this.Icon != null) _trayIcon.Icon = this.Icon;
                else _trayIcon.Icon = SystemIcons.Application;
            }
            catch
            {
                try { _trayIcon.Icon = SystemIcons.Application; } catch { }
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (s, e) => RestoreFromTray());
            menu.Items.Add("本地终端", null, (s, e) =>
            {
                RestoreFromTray();
                try { _tabContainer?.OpenLocalTerminal(); } catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) =>
            {
                _closeToTray = false;
                try { _trayIcon.Visible = false; } catch { }
                Close();
            });
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void RestoreFromTray()
        {
            try
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                ShowInTaskbar = true;
            }
            catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try
            {
                if (_trayIcon != null && WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    ShowInTaskbar = false;
                    try
                    {
                        _trayIcon.ShowBalloonTip(1200, "gdterm",
                            "已最小化到托盘，双击图标恢复。", ToolTipIcon.Info);
                    }
                    catch { }
                }
            }
            catch { }
        }



        private void MaybeCloseToTray(object sender, FormClosingEventArgs e)
        {
            if (!_closeToTray || e.CloseReason != CloseReason.UserClosing || _trayIcon == null)
                return;
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
        }

        /// <summary>Ctrl+K 快速跳转连接。</summary>
        private void OpenQuickJump()
        {
            try
            {
                using (var dlg = new ConnectionQuickJumpForm(_connectionStore))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Selected != null)
                        _openCoord?.OpenConnection(dlg.Selected);
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("MainForm.QuickJump", ex); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.K))
            {
                OpenQuickJump();
                return true;
            }
            if (_cmdRouter != null && _cmdRouter.TryHandle(keyData))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
