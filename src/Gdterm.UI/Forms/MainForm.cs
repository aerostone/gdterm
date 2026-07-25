using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using Gdterm.UI.Hotkeys;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 视图模式（参考 WindTerm）
    /// </summary>
    public enum ViewMode
    {
        /// <summary>标准视图：左侧连接树 + 右侧标签页 + 底部状态栏</summary>
        Standard,
        /// <summary>专注模式：只显示终端标签页，隐藏所有面板</summary>
        Focus,
        /// <summary>紧凑模式：隐藏状态栏和菜单栏，最大化终端空间</summary>
        Compact
    }

    /// <summary>
    /// 主窗口
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly IConnectionStore _connectionStore;
        private readonly TunnelManager _tunnelManager;
        private readonly ITerminalSessionFactory _terminalFactory;
        private readonly ISftpServiceFactory _sftpFactory;
        private readonly IKeePassService _keepassService;
        private readonly IAuditLogger _auditLogger;
        private readonly IAiAssistantService _aiService;
        private readonly ISecurityManager _securityManager;
        private readonly DangerousCommandDetector _dangerousCmdDetector;

        private ConnectionTreeControl _connectionTree;
        private TabContainerControl _tabContainer;
        private StatusBarControl _statusBar;
        private LockOverlayControl _lockOverlay;
        private MenuStrip _menuStrip;
        private GlobalHotkeyManager _hotkeyManager;
        private int _toggleHotkeyId;

        // 视图模式
        private ViewMode _currentViewMode = ViewMode.Standard;
        private ToolStripMenuItem _viewStandardItem;
        private ToolStripMenuItem _viewFocusItem;
        private ToolStripMenuItem _viewCompactItem;

        public MainForm(
            IConnectionStore connectionStore,
            TunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IKeePassService keepassService,
            IAuditLogger auditLogger,
            IAiAssistantService aiService,
            ISecurityManager securityManager,
            DangerousCommandDetector dangerousCmdDetector)
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

            InitializeComponent();
            SetupEventHandlers();

            // 首次启动后解锁（向导已完成）
            if (!_securityManager.IsLocked)
            {
                _lockOverlay.Visible = false;
            }
        }

        private void InitializeComponent()
        {
            Text = "gdterm - 绿色运维客户端";
            Size = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 600);

            // ====== 菜单栏 ======
            _menuStrip = new MenuStrip();

            // 文件菜单
            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add("新建连接(&N)", null, OnNewConnection);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("退出(&X)", null, (s, e) => Close());
            _menuStrip.Items.Add(fileMenu);

            // 连接菜单
            var connectionMenu = new ToolStripMenuItem("连接(&C)");
            connectionMenu.DropDownItems.Add("新建连接", null, OnNewConnection);
            _menuStrip.Items.Add(connectionMenu);

            // 视图菜单
            var viewMenu = new ToolStripMenuItem("视图(&V)");

            _viewStandardItem = new ToolStripMenuItem("标准视图(&S)") { Checked = true, CheckOnClick = false };
            _viewStandardItem.Click += (s, e) => SetViewMode(ViewMode.Standard);

            _viewFocusItem = new ToolStripMenuItem("专注模式(&F)") { CheckOnClick = false };
            _viewFocusItem.Click += (s, e) => SetViewMode(ViewMode.Focus);

            _viewCompactItem = new ToolStripMenuItem("紧凑模式(&C)") { CheckOnClick = false };
            _viewCompactItem.Click += (s, e) => SetViewMode(ViewMode.Compact);

            viewMenu.DropDownItems.Add(_viewStandardItem);
            viewMenu.DropDownItems.Add(_viewFocusItem);
            viewMenu.DropDownItems.Add(_viewCompactItem);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());

            var toggleTreeItem = new ToolStripMenuItem("切换连接面板(&T)");
            toggleTreeItem.ShortcutKeys = Keys.Control | Keys.L;
            toggleTreeItem.Click += (s, e) => ToggleConnectionTree();
            viewMenu.DropDownItems.Add(toggleTreeItem);

            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add("水平分割", null, (s, e) => _tabContainer.SplitHorizontal());
            viewMenu.DropDownItems.Add("垂直分割", null, (s, e) => _tabContainer.SplitVertical());
            _menuStrip.Items.Add(viewMenu);

            // 工具菜单
            var toolsMenu = new ToolStripMenuItem("工具(&T)");
            toolsMenu.DropDownItems.Add("密码库管理(&K)", null, OnKeePassManager);
            toolsMenu.DropDownItems.Add("密码健康报告(&H)", null, OnPasswordHealth);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("🔑 密码生成器(&G)", null, OnPasswordGenerator);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("AI 助手设置(&A)", null, OnAiSettings);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("危险命令规则(&D)", null, OnDangerousCmdSettings);
            _menuStrip.Items.Add(toolsMenu);

            // 帮助菜单
            var helpMenu = new ToolStripMenuItem("帮助(&H)");
            helpMenu.DropDownItems.Add("快捷键列表", null, OnShowHotkeys);
            helpMenu.DropDownItems.Add("关于 gdterm", null, OnAbout);
            _menuStrip.Items.Add(helpMenu);

            MainMenuStrip = _menuStrip;
            Controls.Add(_menuStrip);

            // ====== 主布局 ======

            // 左侧连接面板
            _connectionTree = new ConnectionTreeControl(_connectionStore);
            _connectionTree.Dock = DockStyle.Left;
            _connectionTree.Width = 250;

            // 分割条
            var mainSplitter = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 4,
                BackColor = Color.FromArgb(60, 60, 60)
            };

            // 右侧标签页容器
            _tabContainer = new TabContainerControl(
                _tunnelManager,
                _terminalFactory,
                _sftpFactory,
                _aiService,
                _auditLogger,
                _keepassService);
            _tabContainer.Dock = DockStyle.Fill;

            // 底部状态栏
            _statusBar = new StatusBarControl(
                _tunnelManager,
                _keepassService,
                _aiService,
                _securityManager);
            _statusBar.Dock = DockStyle.Bottom;
            _statusBar.Height = 25;

            // 锁定遮罩
            _lockOverlay = new LockOverlayControl(_securityManager);
            _lockOverlay.Dock = DockStyle.Fill;
            _lockOverlay.Visible = _securityManager.IsLocked;

            // 添加控件（顺序影响 Dock 布局）
            Controls.Add(_tabContainer);
            Controls.Add(mainSplitter);
            Controls.Add(_connectionTree);
            Controls.Add(_statusBar);
            Controls.Add(_lockOverlay);
            // 菜单栏最后添加确保在最顶层
            Controls.Add(_menuStrip);

            _lockOverlay.BringToFront();
        }

        private void SetupEventHandlers()
        {
            _connectionTree.ConnectionDoubleClicked += OnConnectionDoubleClicked;
            _securityManager.LockStateChanged += OnLockStateChanged;

            // 用户操作 → 重置空闲计时器
            MouseMove += (s, e) => _securityManager.ResetIdleTimer();
            KeyDown += (s, e) => _securityManager.ResetIdleTimer();
            Click += (s, e) => _securityManager.ResetIdleTimer();

            InitializeHotkeys();
            FormClosing += OnFormClosing;
        }

        // ====== 视图模式 ======

        private void SetViewMode(ViewMode mode)
        {
            _currentViewMode = mode;

            // 更新菜单勾选状态
            _viewStandardItem.Checked = (mode == ViewMode.Standard);
            _viewFocusItem.Checked = (mode == ViewMode.Focus);
            _viewCompactItem.Checked = (mode == ViewMode.Compact);

            switch (mode)
            {
                case ViewMode.Standard:
                    _connectionTree.Visible = true;
                    _connectionTree.Width = 250;
                    _statusBar.Visible = true;
                    _menuStrip.Visible = true;
                    break;

                case ViewMode.Focus:
                    // 专注模式：只保留终端标签页
                    _connectionTree.Visible = false;
                    _statusBar.Visible = false;
                    _menuStrip.Visible = false;
                    break;

                case ViewMode.Compact:
                    // 紧凑模式：保留连接树和标签页，隐藏状态栏和菜单
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

        // ====== 热键 ======

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
            {
                ToggleWindowVisibility();
            }
        }

        private void ToggleWindowVisibility()
        {
            if (Visible && Form.ActiveForm == this)
            {
                Hide();
            }
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
        }

        // ====== 事件处理 ======

        private void OnConnectionDoubleClicked(object sender, Core.Models.ConnectionConfig config)
        {
            _tabContainer.OpenConnection(config);
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
                // 锁定：同时锁定 KeePass 密码库
                try { _keepassService.Lock(); } catch { }
                _lockOverlay.Visible = true;
                _lockOverlay.BringToFront();
            }
            else
            {
                // 解锁：同步解锁 KeePass 密码库（同一个主密码）
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
            // TODO: 打开新建连接对话框
            MessageBox.Show("新建连接功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnKeePassManager(object sender, EventArgs e)
        {
            // 敏感操作：二次验证主密码
            if (!ReAuthenticate("访问密码库管理"))
                return;

            // TODO: 打开密码库管理对话框
            MessageBox.Show("密码库管理功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnPasswordHealth(object sender, EventArgs e)
        {
            // 敏感操作：二次验证主密码
            if (!ReAuthenticate("查看密码健康报告"))
                return;

            if (!_keepassService.IsUnlocked)
            {
                MessageBox.Show("密码库未解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var form = new PasswordHealthForm(_keepassService))
            {
                form.ShowDialog(this);
            }
        }

        /// <summary>
        /// 敏感操作二次验证——弹出密码输入框验证主密码
        /// 防止离开电脑时他人操作密码库
        /// </summary>
        private bool ReAuthenticate(string action)
        {
            // 如果已锁定，需要先解锁
            if (_securityManager.IsLocked)
            {
                MessageBox.Show("应用已锁定，请先解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // 弹出密码验证
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
                    Text = $"{action}需要验证主密码：",
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
                    ForeColor = Color.White,
                    DialogResult = DialogResult.None
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
            // TODO: 打开 AI 设置对话框
            MessageBox.Show("AI 设置功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnPasswordGenerator(object sender, EventArgs e)
        {
            using (var form = new PasswordGeneratorForm())
            {
                form.ShowDialog(this);
            }
        }

        private void OnDangerousCmdSettings(object sender, EventArgs e)
        {
            // TODO: 打开危险命令规则配置对话框
            MessageBox.Show("危险命令规则配置待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnShowHotkeys(object sender, EventArgs e)
        {
            MessageBox.Show(
                "快捷键列表：\n\n" +
                "Ctrl + `          呼出/隐藏窗口（全局）\n" +
                "Ctrl + L           切换连接面板\n" +
                "Ctrl + Shift + T  水平分屏\n" +
                "Ctrl + Shift + O  垂直分屏\n\n" +
                "在专注模式下，按 Esc 可恢复菜单栏",
                "快捷键", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnAbout(object sender, EventArgs e)
        {
            MessageBox.Show(
                "gdterm - 绿色运维客户端\n" +
                "版本 1.0.0\n\n" +
                "轻量级便携运维工具\n" +
                "SSH / RDP / SFTP / 串口 / AI 助手",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _hotkeyManager?.Dispose();
            _tabContainer.CloseAllTabs();
            _tunnelManager.Dispose();
            _keepassService.Dispose();
            _securityManager.Dispose();
        }

        // ====== 重写按键 ======

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 专注模式下按 Esc 恢复菜单栏
            if (keyData == Keys.Escape && _currentViewMode == ViewMode.Focus)
            {
                _menuStrip.Visible = true;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
