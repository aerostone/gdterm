using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
using Gdterm.UI.Hotkeys;

using Gdterm.UI.Services;
namespace Gdterm.UI.Forms
{
    public enum ViewMode
    {
        Standard,
        Focus,
        Compact
    }

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
        private SessionStateCoordinator _sessionState;
        private StatusBarControl _statusBar;
        private LockOverlayControl _lockOverlay;
        private MenuStrip _menuStrip;
        private GlobalHotkeyManager _hotkeyManager;
        private int _toggleHotkeyId;
        private QuickBarPanel _quickBar;
        private Panel _sideToolHost;
        private Control _activeSidePanel;

        private ViewMode _currentViewMode = ViewMode.Standard;
        private ToolStripMenuItem _viewStandardItem;
        private ToolStripMenuItem _viewFocusItem;
        private ToolStripMenuItem _viewCompactItem;

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

            SetupEventHandlers();
            if (!_securityManager.IsLocked)
                _lockOverlay.Visible = false;

            Shown += (s, e) => RestoreSessionState();
        }

        private void InitializeComponent()
        {
            Text = "gdterm - 绿色运维客户端";
            Size = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 600);

            var menuBuilt = new MainFormMenuBuilder().Build(new MainFormMenuBuilder.Callbacks
            {
                NewConnection = OnNewConnection,
                ImportConnections = OnImportConnections,
                ExportConnections = OnExportConnections,
                Exit = (s, e) => Close(),
                OpenLocalTerminal = (s, e) => _tabContainer.OpenLocalTerminal(),
                OpenSftp = OnOpenSftp,
                ReconnectActive = (s, e) => _tabContainer.ReconnectActiveTab(),
                CloseActive = (s, e) => _tabContainer.CloseActiveTab(),
                ViewStandard = (s, e) => SetViewMode(ViewMode.Standard),
                ViewFocus = (s, e) => SetViewMode(ViewMode.Focus),
                ViewCompact = (s, e) => SetViewMode(ViewMode.Compact),
                ToggleTree = (s, e) => ToggleConnectionTree(),
                SplitHorizontal = (s, e) => _tabContainer.SplitHorizontal(),
                SplitVertical = (s, e) => _tabContainer.SplitVertical(),
                ToggleQuickBar = (s, e) =>
                {
                    if (_quickBar != null) _quickBar.Visible = !_quickBar.Visible;
                },
                ShowSearch = (s, e) => ShowSearchBar(),
                ShowSnippet = (s, e) => ShowSnippetSearch(),
                ShowHighlight = (s, e) => ShowSidePanel(_sidePanels.CreateHighlightPanel()),
                ShowKeyBinding = (s, e) => ShowSidePanel(_sidePanels.CreateKeyBindingPanel()),
                ShowLogonScript = (s, e) => ShowSidePanel(_sidePanels.CreateLogonScriptPanel()),
                ShowMultiChannel = (s, e) => ShowSidePanel(_sidePanels.CreateMultiChannelPanel()),
                ShowBatch = (s, e) => ShowSidePanel(_sidePanels.CreateBatchPanel()),
                ShowHistory = (s, e) => ShowSidePanel(_sidePanels.CreateHistoryPanel()),
                ShowHealth = (s, e) => ShowSidePanel(_sidePanels.CreateHealthPanel()),
                ShowPortForward = (s, e) => ShowSidePanel(_sidePanels.CreatePortForwardPanel()),
                ShowToolbox = (s, e) => ShowSidePanel(_sidePanels.CreateToolboxPanel()),
                ShowSecretScan = (s, e) => ShowSidePanel(_sidePanels.CreateSecretScanPanel()),
                KeePassManager = OnKeePassManager,
                PasswordHealth = OnPasswordHealth,
                PasswordGenerator = OnPasswordGenerator,
                AiSettings = OnAiSettings,
                DangerousCmdSettings = OnDangerousCmdSettings,
                ShowHotkeys = OnShowHotkeys,
                About = OnAbout
            });
            _menuStrip = menuBuilt.Menu;
            _viewStandardItem = menuBuilt.ViewStandardItem;
            _viewFocusItem = menuBuilt.ViewFocusItem;
            _viewCompactItem = menuBuilt.ViewCompactItem;

            MainMenuStrip = _menuStrip;
            Controls.Add(_menuStrip);

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
                this);
            _tabContainer.Dock = DockStyle.Fill;
            _tabContainer.ActiveSessionChanged += OnActiveSessionChanged;
            _tabContainer.SessionClosed += OnSessionClosed;

            // AI “Run this” 经活动终端危险命令闸；无终端时用 detector 对话框
            if (_aiService is Gdterm.AI.AiAssistantService aiSvc)
            {
                aiSvc.CommandGate = cmd =>
                {
                    var tc = _tabContainer.GetActiveTerminalControl();
                    if (tc != null)
                        return tc.ConfirmDangerousCommand(cmd);
                    if (_dangerousCmdDetector == null) return true;
                    try
                    {
                        var check = _dangerousCmdDetector.Check(cmd);
                        if (check == null || !check.IsDangerous) return true;
                        using (var dlg = new DangerousCommandDialog(cmd, check))
                        {
                            dlg.ShowDialog(this);
                            if (!dlg.IsConfirmed) return false;
                            if (dlg.RememberChoice)
                            {
                                try { _dangerousCmdDetector.AddToWhitelist(cmd); } catch { }
                            }
                            return true;
                        }
                    }
                    catch { return true; }
                };
            }

            // 右侧工具宿主（默认隐藏）
            _sideToolHost = new Panel
            {
                Dock = DockStyle.Right,
                Width = 360,
                Visible = false,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            var sideClose = new Button
            {
                Text = "✕ 关闭面板",
                Dock = DockStyle.Top,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White
            };
            sideClose.Click += (s, e) => HideSidePanel();
            _sideToolHost.Controls.Add(sideClose);

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

            // QuickBar 底部
            List<QuickCommand> cmds = null;
            try { cmds = _quickCommandStore?.LoadAll(); } catch { }
            _quickBar = new QuickBarPanel(cmds ?? new List<QuickCommand>());
            _quickBar.Dock = DockStyle.Bottom;
            _quickBar.Height = 36;
            // 统一经 TerminalControl 危险命令闸门，禁止直发 ITerminalSession
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

            Controls.Add(_tabContainer);
            Controls.Add(sideSplitter);
            Controls.Add(_sideToolHost);
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
                () => _currentViewMode,
                SetViewMode,
                () => _connectionTree != null ? _connectionTree.Width : 250,
                w => { if (_connectionTree != null) _connectionTree.Width = w; });
        }

        private void SetupEventHandlers()
        {
            _connectionTree.ConnectionDoubleClicked += OnConnectionDoubleClicked;
            _securityManager.LockStateChanged += OnLockStateChanged;
            MouseMove += (s, e) => _securityManager.ResetIdleTimer();
            KeyDown += (s, e) => _securityManager.ResetIdleTimer();
            Click += (s, e) => _securityManager.ResetIdleTimer();
            InitializeHotkeys();
            FormClosing += OnFormClosing;
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
            var activeTc = _tabContainer.GetActiveTerminalControl();
            if (activeTc != null)
                _quickBar?.SetActiveTerminal(activeTc, host, user);
            else
                _quickBar?.SetActiveSession(session, host, user);

            // 同步多通道：注册在线会话，注销已不存在的会话
            try { _sidePanels?.SyncMultiChannelRegistrations(); } catch { }
        }

        private void SetViewMode(ViewMode mode)
        {
            _currentViewMode = mode;
            _viewStandardItem.Checked = mode == ViewMode.Standard;
            _viewFocusItem.Checked = mode == ViewMode.Focus;
            _viewCompactItem.Checked = mode == ViewMode.Compact;

            switch (mode)
            {
                case ViewMode.Standard:
                    _connectionTree.Visible = true;
                    _connectionTree.Width = 250;
                    _statusBar.Visible = true;
                    _menuStrip.Visible = true;
                    if (_quickBar != null) _quickBar.Visible = true;
                    break;
                case ViewMode.Focus:
                    _connectionTree.Visible = false;
                    _statusBar.Visible = false;
                    _menuStrip.Visible = false;
                    if (_quickBar != null) _quickBar.Visible = false;
                    HideSidePanel();
                    break;
                case ViewMode.Compact:
                    _connectionTree.Visible = true;
                    _connectionTree.Width = 200;
                    _statusBar.Visible = false;
                    _menuStrip.Visible = false;
                    break;
            }
        }

        private void ToggleConnectionTree()
        {
            _connectionTree.Visible = !_connectionTree.Visible;
        }

        private void InitializeHotkeys()
        {
            try
            {
                _hotkeyManager = new GlobalHotkeyManager(this);
                _toggleHotkeyId = _hotkeyManager.Register(HotkeyModifiers.Control, Keys.Oemtilde);
                _hotkeyManager.HotkeyPressed += OnGlobalHotkeyPressed;
            }
            catch { }
        }

        private void OnGlobalHotkeyPressed(object sender, HotkeyPressedEventArgs e)
        {
            if (e.HotkeyId == _toggleHotkeyId)
                ToggleWindowVisibility();
        }

        private void ToggleWindowVisibility()
        {
            if (Visible && Form.ActiveForm == this) Hide();
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
        }

        private void OnConnectionDoubleClicked(object sender, ConnectionConfig config)
        {
            _tabContainer.OpenConnection(config);
            try
            {
                _bookmarkStore?.AddRecentConnection(new RecentConnection
                {
                    ConnectionId = config.Id,
                    Host = config.Host,
                    Protocol = config.Protocol.ToString(),
                    ConnectedAt = DateTime.UtcNow,
                    Success = true
                });
            }
            catch { }
        }

        private void OnOpenSftp(object sender, EventArgs e)
        {
            var tc = _tabContainer.GetActiveTerminalControl();
            if (tc?.Config != null)
            {
                _tabContainer.OpenSftpBrowser(tc.Config);
                return;
            }
            MessageBox.Show("请先打开一个 SSH 连接，或从连接树双击后再打开 SFTP。", "SFTP",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnLockStateChanged(object sender, LockStateChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnLockStateChanged(sender, e)));
                return;
            }

            if (e.IsLocked)
            {
                try { _keepassService.Lock(); } catch { }
                _lockOverlay.Visible = true;
                _lockOverlay.BringToFront();
            }
            else
            {
                var masterPassword = _securityManager.GetMasterPassword();
                if (!string.IsNullOrEmpty(masterPassword))
                {
                    try { _keepassService.UnlockAsync(masterPassword); } catch { }
                }
                _lockOverlay.Visible = false;
            }
        }

        private void OnNewConnection(object sender, EventArgs e)
        {
            using (var dlg = new ConnectionDialog())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    _connectionStore.Add(dlg.Result);
                    _connectionTree.LoadConnections();
                }
            }
        }

        private void OnKeePassManager(object sender, EventArgs e)
        {
            if (!ReAuthenticate("访问密码库管理")) return;
            if (!_keepassService.IsUnlocked)
            {
                MessageBox.Show("密码库未解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new KeePassManagerForm(_keepassService))
                form.ShowDialog(this);
        }

        private void OnPasswordHealth(object sender, EventArgs e)
        {
            if (!ReAuthenticate("查看密码健康报告")) return;
            if (!_keepassService.IsUnlocked)
            {
                MessageBox.Show("密码库未解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new PasswordHealthForm(_keepassService))
                form.ShowDialog(this);
        }

        private bool ReAuthenticate(string action)
        {
            return MasterPasswordPrompt.Confirm(this, _securityManager, action);
        }

        private void OnAiSettings(object sender, EventArgs e)
        {
            var aiModelStore = new AiModelStore(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "ai-models.json"));
            // ApiKey 用主密码派生 AES（gdk2）；锁定时退回 gdk1
            aiModelStore.SetMasterPasswordProvider(() =>
                _securityManager != null && !_securityManager.IsLocked
                    ? _securityManager.GetMasterPassword()
                    : null);
            try { aiModelStore.UpgradeSecretsToMasterKey(); } catch { }
            using (var form = new AiSettingsForm(aiModelStore))
                form.ShowDialog(this);
        }

        private void OnPasswordGenerator(object sender, EventArgs e)
        {
            using (var form = new PasswordGeneratorForm())
                form.ShowDialog(this);
        }

        private void OnDangerousCmdSettings(object sender, EventArgs e)
        {
            using (var form = new DangerousCommandConfigForm(_dangerousCmdDetector))
                form.ShowDialog(this);
        }

        private void OnShowHotkeys(object sender, EventArgs e)
        {
            MessageBox.Show(
                "快捷键：\n\n" +
                "Ctrl + `          呼出/隐藏窗口\n" +
                "Ctrl + L          切换连接面板\n" +
                "Ctrl + R          重连当前标签\n" +
                "Ctrl + W          关闭当前标签\n" +
                "Ctrl + F          终端查找\n" +
                "Ctrl + P          片段搜索\n" +
                "Esc               专注模式恢复菜单",
                "快捷键", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnAbout(object sender, EventArgs e)
        {
            MessageBox.Show(
                "gdterm - 绿色运维客户端\n版本 1.0.0\n\nSSH / RDP / SFTP / 串口 / 本地终端 / 运维工具箱",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnImportConnections(object sender, EventArgs e)
        {
            ConnectionImportExportUi.Import(this, _connectionStore, () => _connectionTree.LoadConnections());
        }

        private void OnExportConnections(object sender, EventArgs e)
        {
            ConnectionImportExportUi.Export(this, _connectionStore);
        }

        // ====== 侧边面板（工厂在 SidePanelFactory） ======

        private void ShowSidePanel(Control panel)
        {
            if (panel == null) return;
            if (_activeSidePanel != null)
            {
                _sideToolHost.Controls.Remove(_activeSidePanel);
                try { _activeSidePanel.Dispose(); } catch { }
            }
            _activeSidePanel = panel;
            panel.Dock = DockStyle.Fill;
            _sideToolHost.Controls.Add(panel);
            panel.BringToFront();
            _sideToolHost.Visible = true;
            _sideToolHost.Width = Math.Max(320, _sideToolHost.Width);
        }

        private void HideSidePanel()
        {
            if (_activeSidePanel != null)
            {
                _sideToolHost.Controls.Remove(_activeSidePanel);
                try { _activeSidePanel.Dispose(); } catch { }
                _activeSidePanel = null;
            }
            _sideToolHost.Visible = false;
        }

        private void ShowSearchBar()
        {
            _sidePanels?.AttachSearchBar(_tabContainer);
        }

        private void ShowSnippetSearch()
        {
            var panel = _sidePanels.CreateSnippetSearchPanel(cmd =>
            {
                var tc = _tabContainer.GetActiveTerminalControl();
                if (tc == null) return;
                var line = cmd.EndsWith("\r") || cmd.EndsWith("\n") ? cmd : cmd + "\r";
                tc.SendInput(line);
            });
            ShowSidePanel(panel);
            var snip = panel as SnippetSearchPanel;
            snip?.ShowAndFocus();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try { _sessionState?.Save(); } catch { }
            _hotkeyManager?.Dispose();
            _tabContainer.CloseAllTabs();
            _tunnelManager.Dispose();
            _keepassService.Dispose();
            _securityManager.Dispose();
        }

        private void SaveSessionState()
        {
            try { _sessionState?.Save(); } catch { }
        }

        private void RestoreSessionState()
        {
            try { _sessionState?.Restore(); } catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _currentViewMode == ViewMode.Focus)
            {
                _menuStrip.Visible = true;
                return true;
            }
            if (keyData == (Keys.Control | Keys.R))
            {
                _tabContainer.ReconnectActiveTab();
                return true;
            }
            if (keyData == (Keys.Control | Keys.W))
            {
                _tabContainer.CloseActiveTab();
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                ShowSearchBar();
                return true;
            }
            if (keyData == (Keys.Control | Keys.P))
            {
                ShowSnippetSearch();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
