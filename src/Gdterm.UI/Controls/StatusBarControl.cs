using System;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.KeePass;
using Gdterm.Security;
using Gdterm.Tunnel;
using Gdterm.Security.Models;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 状态栏
    /// </summary>
    public class StatusBarControl : UserControl
    {
        private readonly ITunnelManager _tunnelManager;
        private readonly IKeePassService _keepassService;
        private readonly IAiAssistantService _aiService;
        private readonly ISecurityManager _securityManager;
        private EventHandler<LockStateChangedEventArgs> _lockChangedHandler;

        /// <summary>
        /// 状态栏项被点击：参数为 "tunnel" / "keepass" / "ai" / "security"，
        /// 由主窗体路由到对应面板/对话框——把二级菜单里的高频入口提为一击直达。
        /// </summary>
        public event EventHandler<string> StatusClicked;

        private ToolStripStatusLabel _connectionStatus;
        private ToolStripStatusLabel _tunnelStatus;
        private ToolStripStatusLabel _keepassStatus;
        private ToolStripStatusLabel _aiStatus;
        private ToolStripStatusLabel _securityStatus;
        private ToolStripStatusLabel _terminalSizeLabel;
        private ToolStripStatusLabel _encodingLabel;

        public StatusBarControl(
            ITunnelManager tunnelManager,
            IKeePassService keepassService,
            IAiAssistantService aiService,
            ISecurityManager securityManager)
        {
            _tunnelManager = tunnelManager;
            _keepassService = keepassService;
            _aiService = aiService;
            _securityManager = securityManager;
            InitializeComponent();
            UpdateStatus();
        }

        private void InitializeComponent()
        {
            var statusStrip = new StatusStrip();
            try
            {
                statusStrip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                statusStrip.SizingGrip = false;
            }
            catch { }

            _connectionStatus = new ToolStripStatusLabel("连接: 就绪");
            _tunnelStatus = new ToolStripStatusLabel("隧道: 无");
            _keepassStatus = new ToolStripStatusLabel("密码库: 锁定");
            _aiStatus = new ToolStripStatusLabel("AI: 就绪");
            _securityStatus = new ToolStripStatusLabel("安全: 已锁定");
            _terminalSizeLabel = new ToolStripStatusLabel("80×24");
            _encodingLabel = new ToolStripStatusLabel("UTF-8");

            MakeClickable(_tunnelStatus, "点击打开端口转发面板");
            MakeClickable(_keepassStatus, "点击打开密码库管理");
            MakeClickable(_aiStatus, "点击打开 AI 助手设置");
            MakeClickable(_securityStatus, "点击修改主密码");

            statusStrip.Items.Add(_connectionStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_tunnelStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_keepassStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_aiStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_securityStatus);
            // 终端尺寸与编码靠右显示（Spring 标签把右侧空位吃掉）
            var spring = new ToolStripStatusLabel { Spring = true };
            statusStrip.Items.Add(spring);
            statusStrip.Items.Add(_terminalSizeLabel);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_encodingLabel);

            Controls.Add(statusStrip);

            // 订阅事件（存储引用以便 Dispose 时取消订阅）
            _lockChangedHandler = (s, e) => UpdateSecurityStatus();
            _securityManager.LockStateChanged += _lockChangedHandler;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_lockChangedHandler != null)
                {
                    _securityManager.LockStateChanged -= _lockChangedHandler;
                    _lockChangedHandler = null;
                }
            }
            base.Dispose(disposing);
        }

        private void UpdateStatus()
        {
            UpdateSecurityStatus();
            UpdateKeePassStatus();
        }

        /// <summary>把状态标签变成可点击入口（链接外观 + 手型光标 + 点击事件）。</summary>
        private void MakeClickable(ToolStripStatusLabel label, string tooltip)
        {
            label.IsLink = true;
            label.LinkBehavior = LinkBehavior.HoverUnderline;
            label.ToolTipText = tooltip;
            string key = null;
            if (label == _tunnelStatus) key = "tunnel";
            else if (label == _keepassStatus) key = "keepass";
            else if (label == _aiStatus) key = "ai";
            else if (label == _securityStatus) key = "security";
            label.Click += (s, e) => { if (key != null) StatusClicked?.Invoke(this, key); };
        }

        private void UpdateSecurityStatus()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateSecurityStatus));
                return;
            }

            _securityStatus.Text = _securityManager.IsLocked ? "安全: 已锁定" : "安全: 已解锁";
        }

        private void UpdateKeePassStatus()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateKeePassStatus));
                return;
            }

            _keepassStatus.Text = _keepassService.IsUnlocked ? "密码库: 已解锁" : "密码库: 锁定";
        }

        /// <summary>更新当前终端的尺寸与编码显示（由 TerminalControl 在 Resize 时调用）。</summary>
        public void UpdateTerminalInfo(int columns, int rows, string encoding)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, int, string>(UpdateTerminalInfo), columns, rows, encoding);
                return;
            }
            try
            {
                _terminalSizeLabel.Text = columns > 0 && rows > 0 ? (columns + "×" + rows) : "";
                _encodingLabel.Text = string.IsNullOrEmpty(encoding) ? "" : encoding;
            }
            catch { }
        }
    }
}
