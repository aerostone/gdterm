using System;
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

namespace Gdterm.UI.Forms
{
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

        private ConnectionTreeControl _connectionTree;
        private TabContainerControl _tabContainer;
        private StatusBarControl _statusBar;
        private LockOverlayControl _lockOverlay;

        public MainForm(
            IConnectionStore connectionStore,
            TunnelManager tunnelManager,
            ITerminalSessionFactory terminalFactory,
            ISftpServiceFactory sftpFactory,
            IKeePassService keepassService,
            IAuditLogger auditLogger,
            IAiAssistantService aiService,
            ISecurityManager securityManager)
        {
            _connectionStore = connectionStore;
            _tunnelManager = tunnelManager;
            _terminalFactory = terminalFactory;
            _sftpFactory = sftpFactory;
            _keepassService = keepassService;
            _auditLogger = auditLogger;
            _aiService = aiService;
            _securityManager = securityManager;

            InitializeComponent();
            SetupEventHandlers();
        }

        private void InitializeComponent()
        {
            Text = "gdterm - 绿色运维客户端";
            Size = new System.Drawing.Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new System.Drawing.Size(800, 600);

            // 创建菜单栏
            var menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("文件");
            fileMenu.DropDownItems.Add("退出", null, (s, e) => Close());
            menuStrip.Items.Add(fileMenu);

            var connectionMenu = new ToolStripMenuItem("连接");
            connectionMenu.DropDownItems.Add("新建连接", null, OnNewConnection);
            menuStrip.Items.Add(connectionMenu);

            var toolsMenu = new ToolStripMenuItem("工具");
            toolsMenu.DropDownItems.Add("密码库管理", null, OnKeePassManager);
            toolsMenu.DropDownItems.Add("AI 设置", null, OnAiSettings);
            menuStrip.Items.Add(toolsMenu);

            MainMenuStrip = menuStrip;
            Controls.Add(menuStrip);

            // 创建主布局
            var mainSplitter = new Splitter
            {
                Dock = DockStyle.Left,
                Width = 5
            };

            // 左侧连接面板
            _connectionTree = new ConnectionTreeControl(_connectionStore);
            _connectionTree.Dock = DockStyle.Left;
            _connectionTree.Width = 250;

            // 右侧标签页容器
            _tabContainer = new TabContainerControl(
                _tunnelManager,
                _terminalFactory,
                _sftpFactory,
                _aiService,
                _auditLogger);
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
            _lockOverlay.Visible = false;

            // 添加控件
            Controls.Add(_tabContainer);
            Controls.Add(mainSplitter);
            Controls.Add(_connectionTree);
            Controls.Add(_statusBar);
            Controls.Add(_lockOverlay);

            // 锁定遮罩在最上层
            _lockOverlay.BringToFront();
        }

        private void SetupEventHandlers()
        {
            // 连接面板双击 → 打开连接
            _connectionTree.ConnectionDoubleClicked += OnConnectionDoubleClicked;

            // 安全锁定状态变化
            _securityManager.LockStateChanged += OnLockStateChanged;

            // 窗口用户操作 → 重置空闲计时器
            MouseMove += (s, e) => _securityManager.ResetIdleTimer();
            KeyDown += (s, e) => _securityManager.ResetIdleTimer();
            Click += (s, e) => _securityManager.ResetIdleTimer();

            // 窗口关闭
            FormClosing += OnFormClosing;
        }

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

            _lockOverlay.Visible = e.IsLocked;

            if (e.IsLocked)
            {
                _lockOverlay.BringToFront();
            }
        }

        private void OnNewConnection(object sender, EventArgs e)
        {
            // TODO: 打开新建连接对话框
            MessageBox.Show("新建连接功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnKeePassManager(object sender, EventArgs e)
        {
            // TODO: 打开密码库管理对话框
            MessageBox.Show("密码库管理功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnAiSettings(object sender, EventArgs e)
        {
            // TODO: 打开 AI 设置对话框
            MessageBox.Show("AI 设置功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // 清理资源
            _tabContainer.CloseAllTabs();
            _tunnelManager.Dispose();
            _keepassService.Dispose();
            _securityManager.Dispose();
        }
    }
}
