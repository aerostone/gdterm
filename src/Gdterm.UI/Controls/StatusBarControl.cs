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

        private ToolStripStatusLabel _connectionStatus;
        private ToolStripStatusLabel _tunnelStatus;
        private ToolStripStatusLabel _keepassStatus;
        private ToolStripStatusLabel _aiStatus;
        private ToolStripStatusLabel _securityStatus;

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

            _connectionStatus = new ToolStripStatusLabel("连接: 就绪");
            _tunnelStatus = new ToolStripStatusLabel("隧道: 无");
            _keepassStatus = new ToolStripStatusLabel("密码库: 锁定");
            _aiStatus = new ToolStripStatusLabel("AI: 就绪");
            _securityStatus = new ToolStripStatusLabel("安全: 已锁定");

            statusStrip.Items.Add(_connectionStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_tunnelStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_keepassStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_aiStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(_securityStatus);

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
    }
}
