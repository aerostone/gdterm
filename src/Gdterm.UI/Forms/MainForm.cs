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
            Size = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 600);

            _connectionTree = new ConnectionTreeControl(_connectionStore);
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
                this, _securityManager, _keepassService, _dangerousCmdDetector);

            _openCoord = new ConnectionOpenCoordinator(
                _tabContainer, _connectionStore, _bookmarkStore, _connectionTree, this);

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
                AiSettings = (s, e) => _toolsDialogs.OpenAiSettings(),
                DangerousCmdSettings = (s, e) => _toolsDialogs.OpenDangerousCmdSettings(),
                ShowHotkeys = (s, e) => _toolsDialogs.ShowHotkeysHelp(),
                About = (s, e) => _toolsDialogs.ShowAbout()
            });
            _menuStrip = menuBuilt.Menu;
            MainMenuStrip = _menuStrip;

            _viewMode = new ViewModeController(
                _connectionTree,
                _statusBar,
                _menuStrip,
                _quickBar,
                () => _sidePanelHost?.Hide(),
                menuBuilt.ViewStandardItem,
                menuBuilt.ViewFocusItem,
                menuBuilt.ViewCompactItem);

            _cmdRouter = new MainFormCommandRouter(
                _tabContainer, _sidePanels, _sidePanelHost, _viewMode);

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

        private void SetupEventHandlers()
        {
            _connectionTree.ConnectionDoubleClicked += (s, cfg) => _openCoord.OpenConnection(cfg);
            _securityManager.LockStateChanged += (s, e) => _lockCoord.Handle(s, e);
            MouseMove += (s, e) => _securityManager.ResetIdleTimer();
            KeyDown += (s, e) => _securityManager.ResetIdleTimer();
            Click += (s, e) => _securityManager.ResetIdleTimer();
            _hotkeys = new GlobalHotkeyController(this);
            _hotkeys.Initialize();
            _shutdown = new AppShutdownCoordinator(
                _sessionState, _hotkeys, _tabContainer,
                _tunnelManager, _keepassService, _securityManager);
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

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_cmdRouter != null && _cmdRouter.TryHandle(keyData))
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
