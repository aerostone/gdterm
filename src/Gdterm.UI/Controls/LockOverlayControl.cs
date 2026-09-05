using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 锁定遮罩（覆盖整个 ClientArea，显示密码输入框）。
    ///
    /// 遮罩本身只覆盖 ClientArea（标签页/连接树/按钮等），菜单栏与状态栏是 ToolStrip
    /// 顶栏，由 LockStateCoordinator 在锁定态置 Enabled=false 一并禁用，防止锁定后仍可点击。
    /// 输入框/标签一律跟随全局暗色主题（GdtermColorTable），不再硬编码白底，
    /// 避免浅色前景字 + 白底导致的「看不见字」。
    /// </summary>
    public class LockOverlayControl : UserControl
    {
        private readonly ISecurityManager _securityManager;
        private AntdUI.Input _passwordBox;
        private AntdUI.Button _unlockButton;
        private AntdUI.Label _messageLabel;
        private Panel _centerPanel;

        public LockOverlayControl(ISecurityManager securityManager)
        {
            _securityManager = securityManager;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // 半透明黑遮罩：盖住 ClientArea 所有内容,
            BackColor = Color.FromArgb(210, 10, 14, 18);
            Dock = DockStyle.Fill;

            // 中心面板：暗色 surface，与全局主题一致
            _centerPanel = new Panel
            {
                Size = DpiScale.S(this, 320, 170),
                BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Surface
            };
            _centerPanel.Location = new Point(
                (Width - _centerPanel.Width) / 2,
                (Height - _centerPanel.Height) / 2);
            Controls.Add(_centerPanel);

            // 标题标签
            var titleLabel = new AntdUI.Label {
                Text = "应用已锁定",
                Location = DpiScale.P(this, 20, 18),
                Size = DpiScale.S(this, 280, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground,
                Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold)
            };
            _centerPanel.Controls.Add(titleLabel);

            // 消息标签（错误提示用，初始为说明文字）
            _messageLabel = new AntdUI.Label {
                Text = "请输入主密码解锁",
                Location = DpiScale.P(this, 20, 52),
                Size = DpiScale.S(this, 280, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Muted
            };
            _centerPanel.Controls.Add(_messageLabel);

            // 密码输入框：暗色 surface + 浅色前景，圆点遮罩
            _passwordBox = new AntdUI.Input {
                Location = DpiScale.P(this, 20, 78),
                Size = DpiScale.S(this, 280, 26),
                UseSystemPasswordChar = true,
                BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground,
            };
            _passwordBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                    OnUnlock();
            };
            _centerPanel.Controls.Add(_passwordBox);

            // 解锁按钮：accent 强调色
            _unlockButton = new AntdUI.Button {
                Text = "解锁",
                Location = DpiScale.P(this, 110, 118),
                Size = DpiScale.S(this, 100, 32),
                BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Accent,
                ForeColor = Color.Black,
            };
            _unlockButton.Click += (s, e) => OnUnlock();
            _centerPanel.Controls.Add(_unlockButton);

            // 窗口大小变化时重新定位中心面板
            Resize += (s, e) => Recenter();
            Recenter();
        }

        private void Recenter()
        {
            if (_centerPanel == null) return;
            _centerPanel.Location = new Point(
                (Width - _centerPanel.Width) / 2,
                (Height - _centerPanel.Height) / 2);
        }

        /// <summary>遮罩显示时聚焦到密码框。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                try { _passwordBox?.Focus(); } catch { }
            }
        }

        private void OnUnlock()
        {
            var password = _passwordBox.Text;
            if (string.IsNullOrEmpty(password))
            {
                _messageLabel.Text = "请输入密码";
                _messageLabel.ForeColor = Color.FromArgb(0xEF, 0x44, 0x44);
                _passwordBox.Focus();
                return;
            }

            if (_securityManager.Unlock(password))
            {
                _passwordBox.Text = "";
                _messageLabel.Text = "请输入主密码解锁";
                _messageLabel.ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Muted;
            }
            else
            {
                _messageLabel.Text = "密码错误，请重试";
                _messageLabel.ForeColor = Color.FromArgb(0xEF, 0x44, 0x44);
                _passwordBox.SelectAll();
                _passwordBox.Focus();
            }
        }
    }
}