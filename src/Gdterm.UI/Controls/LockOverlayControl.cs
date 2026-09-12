using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;

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
            BackColor = GdtermColorTable.Overlay;
            Dock = DockStyle.Fill;

            // 中心面板：暗色 surface，与全局主题一致
            // 面板高跟随内容：按钮底 + 20（默认字号下约 190；大字号自动长高）
            // 注意：LineBox/RowStep 返回的是当前 DPI 下像素，V() 同样返回当前 DPI 像素，两者可直接相加。
            var titleLabelFont = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold);
            int lockTitleH = Math.Max(DpiScale.V(this, 24),
                Gdterm.UI.Services.FormFontPolicy.LineBox(titleLabelFont, this, 1.25f));
            int lockMsgBoxH = Math.Max(DpiScale.V(this, 20),
                Gdterm.UI.Services.FormFontPolicy.LineBox(Font, this, 1.25f));
            int lockInputY2 = DpiScale.V(this, 52) + lockMsgBoxH + DpiScale.V(this, 10);
            int lockInputH2 = Math.Max(DpiScale.V(this, 30), Gdterm.UI.Services.FormFontPolicy.RowStep(this));
            int lockBtnY2 = lockInputY2 + lockInputH2 + DpiScale.V(this, 10);
            int lockPanelH = lockBtnY2 + DpiScale.V(this, 32) + DpiScale.V(this, 20);
            _centerPanel = new Panel
            {
                Size = new Size(DpiScale.V(this, 320), lockPanelH),
                BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Surface
            };
            _centerPanel.Location = new Point(
                (Width - _centerPanel.Width) / 2,
                (Height - _centerPanel.Height) / 2);
            Controls.Add(_centerPanel);

            // 标题标签：盒高取行盒（11pt 字跟随 UI 字号放大时不裁字）
            var titleLabel = new AntdUI.Label {
                Text = "应用已锁定",
                Location = new Point(DpiScale.V(this, 20), DpiScale.V(this, 18)),
                Size = new Size(DpiScale.V(this, 280), lockTitleH),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground,
                Font = titleLabelFont
            };
            _centerPanel.Controls.Add(titleLabel);

            // 消息标签（错误提示用，初始为说明文字）：盒高取行盒，大字号不裁字
            _messageLabel = new AntdUI.Label {
                Text = "请输入主密码解锁",
                Location = new Point(DpiScale.V(this, 20), DpiScale.V(this, 52)),
                Size = new Size(DpiScale.V(this, 280), lockMsgBoxH),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Muted
            };
            _centerPanel.Controls.Add(_messageLabel);

            // 密码输入框：暗色 surface + 浅色前景，圆点遮罩（高度字体驱动，原先 26 在大字号下文字被裁）
            // y 随消息行盒加高下移 52+LineBox(≈20)+10=82，保证 10px 间隙
            _passwordBox = new AntdUI.Input {
                Location = new Point(DpiScale.V(this, 20), lockInputY2),
                Size = new Size(DpiScale.V(this, 280), lockInputH2),
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

            // 解锁按钮：accent 强调色；y 跟随输入框（输入y+输入高+10），面板底留 20
            _unlockButton = new AntdUI.Button {
                Text = "解锁",
                Location = new Point(DpiScale.V(this, 110), lockBtnY2),
                Size = new Size(DpiScale.V(this, 100), DpiScale.V(this, 32)),
                Type = AntdUI.TTypeMini.Primary,
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
                try { _passwordBox?.Focus(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("LockOverlayControl", exSwallowed); } catch { } }
            }
        }

        private void OnUnlock()
        {
            var password = _passwordBox.Text;
            if (string.IsNullOrEmpty(password))
            {
                _messageLabel.Text = "请输入密码";
                _messageLabel.ForeColor = GdtermColorTable.Danger;
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
                _messageLabel.ForeColor = GdtermColorTable.Danger;
                _passwordBox.SelectAll();
                _passwordBox.Focus();
            }
        }
    }
}
