using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 锁定遮罩（覆盖整个窗口，显示密码输入框）
    /// </summary>
    public class LockOverlayControl : UserControl
    {
        private readonly ISecurityManager _securityManager;
        private TextBox _passwordBox;
        private Button _unlockButton;
        private Label _messageLabel;

        public LockOverlayControl(ISecurityManager securityManager)
        {
            _securityManager = securityManager;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            BackColor = Color.FromArgb(200, 0, 0, 0);
            Dock = DockStyle.Fill;

            // 中心面板
            var centerPanel = new Panel
            {
                Size = DpiScale.S(this, 300, 150),
                BackColor = Color.White,
                Location = new Point(
                    (Width - 300) / 2,
                    (Height - 150) / 2)
            };
            Controls.Add(centerPanel);

            // 消息标签
            _messageLabel = new Label
            {
                Text = "应用已锁定，请输入主密码解锁",
                Location = DpiScale.P(this, 20, 20),
                Size = DpiScale.S(this, 260, 20),
                TextAlign = ContentAlignment.MiddleCenter
            };
            centerPanel.Controls.Add(_messageLabel);

            // 密码输入框
            _passwordBox = new TextBox
            {
                Location = DpiScale.P(this, 20, 60),
                Size = DpiScale.S(this, 260, 25),
                UseSystemPasswordChar = true
            };
            _passwordBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    OnUnlock();
                }
            };
            centerPanel.Controls.Add(_passwordBox);

            // 解锁按钮
            _unlockButton = new Button
            {
                Text = "解锁",
                Location = DpiScale.P(this, 100, 100),
                Size = DpiScale.S(this, 100, 30)
            };
            _unlockButton.Click += (s, e) => OnUnlock();
            centerPanel.Controls.Add(_unlockButton);

            // 窗口大小变化时重新定位中心面板
            Resize += (s, e) =>
            {
                centerPanel.Location = new Point(
                    (Width - centerPanel.Width) / 2,
                    (Height - centerPanel.Height) / 2);
            };
        }

        private void OnUnlock()
        {
            var password = _passwordBox.Text;
            if (string.IsNullOrEmpty(password))
            {
                _messageLabel.Text = "请输入密码";
                _messageLabel.ForeColor = Color.Red;
                return;
            }

            if (_securityManager.Unlock(password))
            {
                _passwordBox.Text = "";
                _messageLabel.Text = "应用已锁定，请输入主密码解锁";
                _messageLabel.ForeColor = Color.Black;
            }
            else
            {
                _messageLabel.Text = "密码错误，请重试";
                _messageLabel.ForeColor = Color.Red;
                _passwordBox.SelectAll();
            }
        }
    }
}
