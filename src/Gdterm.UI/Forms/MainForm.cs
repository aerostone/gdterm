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
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using Gdterm.UI.Hotkeys;

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

            _menuStrip = new MenuStrip();

            // 文件
            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add("新建连接(&N)", null, OnNewConnection);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("导入连接(&I)...", null, OnImportConnections);
            fileMenu.DropDownItems.Add("导出连接(&E)...", null, OnExportConnections);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("退出(&X)", null, (s, e) => Close());
            _menuStrip.Items.Add(fileMenu);

            // 连接
            var connectionMenu = new ToolStripMenuItem("连接(&C)");
            connectionMenu.DropDownItems.Add("新建连接", null, OnNewConnection);
            connectionMenu.DropDownItems.Add("本地终端(&L)", null, (s, e) => _tabContainer.OpenLocalTerminal());
            connectionMenu.DropDownItems.Add("SFTP 浏览器", null, OnOpenSftp);
            connectionMenu.DropDownItems.Add(new ToolStripSeparator());
            connectionMenu.DropDownItems.Add("重连当前标签 Ctrl+R", null, (s, e) => _tabContainer.ReconnectActiveTab());
            connectionMenu.DropDownItems.Add("关闭当前标签 Ctrl+W", null, (s, e) => _tabContainer.CloseActiveTab());
            _menuStrip.Items.Add(connectionMenu);

            // 视图
            var viewMenu = new ToolStripMenuItem("视图(&V)");
            _viewStandardItem = new ToolStripMenuItem("标准视图(&S)") { Checked = true };
            _viewStandardItem.Click += (s, e) => SetViewMode(ViewMode.Standard);
            _viewFocusItem = new ToolStripMenuItem("专注模式(&F)");
            _viewFocusItem.Click += (s, e) => SetViewMode(ViewMode.Focus);
            _viewCompactItem = new ToolStripMenuItem("紧凑模式(&C)");
            _viewCompactItem.Click += (s, e) => SetViewMode(ViewMode.Compact);
            viewMenu.DropDownItems.Add(_viewStandardItem);
            viewMenu.DropDownItems.Add(_viewFocusItem);
            viewMenu.DropDownItems.Add(_viewCompactItem);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            var toggleTreeItem = new ToolStripMenuItem("切换连接面板(&T)") { ShortcutKeys = Keys.Control | Keys.L };
            toggleTreeItem.Click += (s, e) => ToggleConnectionTree();
            viewMenu.DropDownItems.Add(toggleTreeItem);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add("水平分割", null, (s, e) => _tabContainer.SplitHorizontal());
            viewMenu.DropDownItems.Add("垂直分割", null, (s, e) => _tabContainer.SplitVertical());
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add("快捷命令栏", null, (s, e) =>
            {
                if (_quickBar != null) _quickBar.Visible = !_quickBar.Visible;
            });
            _menuStrip.Items.Add(viewMenu);

            // 终端
            var termMenu = new ToolStripMenuItem("终端(&E)");
            termMenu.DropDownItems.Add("查找 Ctrl+F", null, (s, e) => ShowSearchBar());
            termMenu.DropDownItems.Add("片段搜索 Ctrl+P", null, (s, e) => ShowSnippetSearch());
            termMenu.DropDownItems.Add("高亮规则", null, (s, e) => ShowSidePanel(CreateHighlightPanel()));
            termMenu.DropDownItems.Add("快捷键绑定", null, (s, e) => ShowSidePanel(CreateKeyBindingPanel()));
            termMenu.DropDownItems.Add("登录脚本", null, (s, e) => ShowSidePanel(CreateLogonScriptPanel()));
            termMenu.DropDownItems.Add(new ToolStripSeparator());
            termMenu.DropDownItems.Add("多通道广播", null, (s, e) => ShowSidePanel(CreateMultiChannelPanel()));
            termMenu.DropDownItems.Add("批量命令", null, (s, e) => ShowSidePanel(CreateBatchPanel()));
            termMenu.DropDownItems.Add("命令历史", null, (s, e) => ShowSidePanel(CreateHistoryPanel()));
            termMenu.DropDownItems.Add("健康监控", null, (s, e) => ShowSidePanel(CreateHealthPanel()));
            termMenu.DropDownItems.Add("端口转发", null, (s, e) => ShowSidePanel(CreatePortForwardPanel()));
            _menuStrip.Items.Add(termMenu);

            // 工具
            var toolsMenu = new ToolStripMenuItem("工具(&T)");
            toolsMenu.DropDownItems.Add("运维工具箱", null, (s, e) => ShowSidePanel(CreateToolboxPanel()));
            toolsMenu.DropDownItems.Add("敏感信息扫描", null, (s, e) => ShowSidePanel(CreateSecretScanPanel()));
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("密码库管理(&K)", null, OnKeePassManager);
            toolsMenu.DropDownItems.Add("密码健康报告(&H)", null, OnPasswordHealth);
            toolsMenu.DropDownItems.Add("🔑 密码生成器(&G)", null, OnPasswordGenerator);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("AI 助手设置(&A)", null, OnAiSettings);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("危险命令规则(&D)", null, OnDangerousCmdSettings);
            _menuStrip.Items.Add(toolsMenu);

            // 帮助
            var helpMenu = new ToolStripMenuItem("帮助(&H)");
            helpMenu.DropDownItems.Add("快捷键列表", null, OnShowHotkeys);
            helpMenu.DropDownItems.Add("关于 gdterm", null, OnAbout);
            _menuStrip.Items.Add(helpMenu);

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
                _connectionStore);
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
            try
            {
                if (_multiChannelManager == null) return;
                var all = _tabContainer.GetConnectedSessions();
                var liveIds = new System.Collections.Generic.HashSet<string>(all.Keys);

                foreach (var info in _multiChannelManager.GetAllSessions())
                {
                    if (info != null && !liveIds.Contains(info.SessionId))
                        _multiChannelManager.Unregister(info.SessionId);
                }

                foreach (var kv in all)
                    _multiChannelManager.Register(kv.Key, kv.Value, kv.Key, null);
            }
            catch { }
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
            if (_securityManager.IsLocked)
            {
                MessageBox.Show("应用已锁定，请先解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            using (var dialog = new Form())
            {
                dialog.Text = "安全验证";
                dialog.Size = new Size(380, 180);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.BackColor = Color.FromArgb(35, 35, 35);

                var label = new Label
                {
                    Text = action + "需要验证主密码：",
                    Font = new Font("Microsoft YaHei", 10f),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    Location = new Point(15, 15),
                    Size = new Size(340, 25)
                };
                var pwdBox = new TextBox
                {
                    Location = new Point(15, 45),
                    Size = new Size(335, 28),
                    Font = new Font("Consolas", 11f),
                    UseSystemPasswordChar = true,
                    BackColor = Color.FromArgb(50, 50, 50),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                var errorLabel = new Label
                {
                    Text = "",
                    Font = new Font("Microsoft YaHei", 9f),
                    ForeColor = Color.FromArgb(255, 100, 100),
                    Location = new Point(15, 78),
                    Size = new Size(335, 20)
                };
                var okBtn = new Button
                {
                    Text = "验证",
                    Size = new Size(80, 32),
                    Location = new Point(270, 105),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0, 122, 204),
                    ForeColor = Color.White
                };
                okBtn.Click += (s, ev) =>
                {
                    if (_securityManager.VerifyMasterPassword(pwdBox.Text))
                    {
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                    }
                    else
                    {
                        errorLabel.Text = "密码不正确";
                        pwdBox.SelectAll();
                        pwdBox.Focus();
                    }
                };
                pwdBox.KeyDown += (s, ev) =>
                {
                    if (ev.KeyCode == Keys.Enter) okBtn.PerformClick();
                };
                dialog.Controls.AddRange(new Control[] { label, pwdBox, errorLabel, okBtn });
                dialog.AcceptButton = okBtn;
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        private void OnAiSettings(object sender, EventArgs e)
        {
            var aiModelStore = new AiModelStore(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "ai-models.json"));
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
            using (var dlg = new OpenFileDialog
            {
                Title = "导入连接",
                Filter = "所有支持格式|*.json;*.csv;*.xml|JSON|*.json|CSV|*.csv|mRemoteNG XML|*.xml"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var imported = ImportExport.ConnectionImporterExporter.ImportFromFile(dlg.FileName);
                    if (imported.Count == 0)
                    {
                        MessageBox.Show("未找到可导入的连接", "导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var existing = _connectionStore.LoadAll();
                    var merge = ImportExport.ConnectionImporterExporter.MergeConnections(existing, imported);
                    foreach (var conn in merge.NewConnections)
                        _connectionStore.Add(conn);
                    _connectionTree.LoadConnections();
                    MessageBox.Show(
                        "导入完成：\n新增 " + merge.NewConnections.Count + "\n跳过 " + merge.Duplicates.Count,
                        "导入成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导入失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnExportConnections(object sender, EventArgs e)
        {
            var connections = _connectionStore.LoadAll();
            if (connections.Count == 0)
            {
                MessageBox.Show("没有可导出的连接", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = "导出连接",
                Filter = "JSON|*.json|CSV|*.csv",
                FileName = "gdterm-connections"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                        ImportExport.ConnectionImporterExporter.ExportAsCsv(connections, dlg.FileName);
                    else
                        ImportExport.ConnectionImporterExporter.ExportAsJson(connections, dlg.FileName);
                    MessageBox.Show("已导出 " + connections.Count + " 个连接", "导出成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ====== 侧边面板 ======

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

        private Control CreateToolboxPanel()
        {
            if (_toolRegistry == null)
                return new Label { Text = "工具箱未初始化", ForeColor = Color.White, Dock = DockStyle.Fill };
            var panel = new ToolboxPanel(_toolRegistry);
            // 注入当前活动 SSH 会话，使 IRemoteToolModule 可执行远程命令
            try { panel.SetSshClient(_tabContainer.GetActiveSshClient()); } catch { }
            return panel;
        }

        private Control CreateSecretScanPanel()
        {
            if (_secretScanner == null)
                return new Label { Text = "扫描器未初始化", ForeColor = Color.White, Dock = DockStyle.Fill };
            return new SecretScanPanel(_secretScanner);
        }

        private Control CreateMultiChannelPanel()
        {
            // 刷新注册：只保留当前在线会话
            var all = _tabContainer.GetConnectedSessions();
            var liveIds = new System.Collections.Generic.HashSet<string>(all.Keys);
            foreach (var info in _multiChannelManager.GetAllSessions())
            {
                if (info != null && !liveIds.Contains(info.SessionId))
                    _multiChannelManager.Unregister(info.SessionId);
            }
            foreach (var kv in all)
                _multiChannelManager.Register(kv.Key, kv.Value, kv.Key, null);
            var panel = new MultiChannelPanel(_multiChannelManager);
            panel.BroadcastCommandRequested += (s, cmd) =>
            {
                if (_dangerousCmdDetector != null)
                {
                    var check = _dangerousCmdDetector.Check(cmd);
                    if (check != null && check.IsDangerous)
                    {
                        using (var dlg = new DangerousCommandDialog(cmd, check))
                        {
                            dlg.ShowDialog(this);
                            if (!dlg.IsConfirmed)
                            {
                                try
                                {
                                    _auditLogger?.LogSecurityEvent(
                                        SecurityEvent.DangerousCommandBlocked,
                                        "broadcast blocked: " + cmd);
                                }
                                catch { }
                                return;
                            }
                            if (dlg.RememberChoice)
                            {
                                try { _dangerousCmdDetector.AddToWhitelist(cmd); } catch { }
                            }
                        }
                    }
                }
                _multiChannelManager.BroadcastCommand(cmd + "\r");
                try
                {
                    _commandHistoryStore?.RecordCommand(new CommandHistoryEntry
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Command = cmd,
                        ExecutedAt = DateTime.UtcNow,
                        IsBroadcast = true
                    });
                }
                catch { }
            };
            return panel;
        }

        private Control CreateBatchPanel()
        {
            var panel = new BatchCommandPanel();
            panel.SetDangerousDetector(_dangerousCmdDetector);
            panel.SetSessions(_tabContainer.GetConnectedSessions());
            return panel;
        }

        private Control CreateHistoryPanel()
        {
            if (_commandHistoryStore == null)
                return new Label { Text = "命令历史未初始化", ForeColor = Color.White, Dock = DockStyle.Fill };
            return new CommandHistoryPanel(_commandHistoryStore);
        }

        private Control CreateHealthPanel()
        {
            var panel = new HealthMonitorPanel();
            var mon = _tabContainer.GetActiveHealthMonitor();
            if (mon != null) panel.SetMonitor(mon);
            return panel;
        }

        private Control CreatePortForwardPanel()
        {
            try
            {
                var mgr = new Gdterm.Tunnel.PortForwardManager();
                var panel = new PortForwardPanel(mgr);
                var client = _tabContainer.GetActiveSshClient();
                if (client != null)
                    panel.SetSshClient(client);
                return panel;
            }
            catch (Exception ex)
            {
                return new Label { Text = "端口转发不可用: " + ex.Message, ForeColor = Color.White, Dock = DockStyle.Fill };
            }
        }

        private Control CreateHighlightPanel()
        {
            if (_highlightStore == null)
                return new Label { Text = "高亮存储未初始化", ForeColor = Color.White, Dock = DockStyle.Fill };
            return new HighlightRulePanel(_highlightStore);
        }

        private Control CreateKeyBindingPanel()
        {
            if (_keyBindingStore == null)
                return new Label { Text = "快捷键存储未初始化", ForeColor = Color.White, Dock = DockStyle.Fill };
            return new KeyBindingPanel(_keyBindingStore);
        }

        private Control CreateLogonScriptPanel()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "logon-scripts.json");
                var store = new LogonScriptStore(path);
                return new LogonScriptPanel(store);
            }
            catch (Exception ex)
            {
                return new Label { Text = "登录脚本: " + ex.Message, ForeColor = Color.White, Dock = DockStyle.Fill };
            }
        }

        private void ShowSearchBar()
        {
            var tc = _tabContainer.GetActiveTerminalControl();
            if (tc == null)
            {
                MessageBox.Show("请先打开终端标签", "查找", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var bar = new TerminalSearchBar();
            bar.Dock = DockStyle.Top;
            bar.Height = 32;
            // 简单挂到 tab 容器顶部
            _tabContainer.Controls.Add(bar);
            bar.BringToFront();
            bar.ShowAndFocus();
            bar.CloseRequested += () =>
            {
                _tabContainer.Controls.Remove(bar);
                bar.Dispose();
            };
        }

        private void ShowSnippetSearch()
        {
            List<QuickCommand> cmds = null;
            try { cmds = _quickCommandStore?.LoadAll(); } catch { }
            var panel = new SnippetSearchPanel(cmds ?? new List<QuickCommand>());
            var snipTc = _tabContainer.GetActiveTerminalControl();
            if (snipTc != null)
                panel.SetActiveTerminal(snipTc);
            else
                panel.SetActiveSession(_tabContainer.GetActiveSession());
            // 统一经 TerminalControl 闸门
            panel.SnippetSent += (cmd, qc) =>
            {
                var tc = _tabContainer.GetActiveTerminalControl();
                if (tc == null) return;
                var line = cmd.EndsWith("\r") || cmd.EndsWith("\n") ? cmd : cmd + "\r";
                tc.SendInput(line);
            };
            ShowSidePanel(panel);
            panel.ShowAndFocus();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try { SaveSessionState(); } catch { }
            _hotkeyManager?.Dispose();
            _tabContainer.CloseAllTabs();
            _tunnelManager.Dispose();
            _keepassService.Dispose();
            _securityManager.Dispose();
        }

        private void SaveSessionState()
        {
            if (_sessionStore == null) return;
            var state = new SessionState
            {
                WindowX = Left,
                WindowY = Top,
                WindowWidth = Width,
                WindowHeight = Height,
                WindowState = WindowState.ToString(),
                ViewMode = _currentViewMode.ToString(),
                ConnectionPanelWidth = _connectionTree?.Width ?? 250,
                ActiveTabIndex = _tabContainer.ActiveTabIndex,
                OpenTabs = _tabContainer.GetOpenTabStates()
            };
            _sessionStore.Save(state);
        }

        private void RestoreSessionState()
        {
            if (_sessionStore == null) return;
            var state = _sessionStore.Load();
            if (state == null) return;
            try
            {
                if (state.WindowWidth > 200 && state.WindowHeight > 200)
                {
                    Width = state.WindowWidth;
                    Height = state.WindowHeight;
                    StartPosition = FormStartPosition.Manual;
                    Left = state.WindowX;
                    Top = state.WindowY;
                }
                if (state.WindowState == "Maximized")
                    WindowState = FormWindowState.Maximized;
                if (Enum.TryParse(state.ViewMode, out ViewMode vm))
                    SetViewMode(vm);
                if (state.ConnectionPanelWidth > 50 && _connectionTree != null)
                    _connectionTree.Width = state.ConnectionPanelWidth;
                if (state.OpenTabs != null)
                {
                    var all = _connectionStore.LoadAll();
                    foreach (var tab in state.OpenTabs)
                    {
                        var config = all.FirstOrDefault(c => c.Id == tab.ConnectionId);
                        if (config != null) _tabContainer.OpenConnection(config);
                    }
                    if (state.ActiveTabIndex >= 0)
                        _tabContainer.SetActiveTabIndex(state.ActiveTabIndex);
                }
            }
            catch { }
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
