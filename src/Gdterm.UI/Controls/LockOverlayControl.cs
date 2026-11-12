using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

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
            // 字体现取全局 UI 字号：本控件是 UserControl，构造期 Font 还是系统默认值，
            // 后面的 LineBox/RowStep 全部按 9pt 算 → 用户把 UI 调到 14pt 时遮罩里的
            // 标题/说明/输入框一个像素都不长（矩阵门字号轴 1.00 实证）
            Font = Services.FormFontPolicy.UiFont();

            // 中心面板：暗色 surface，与全局主题一致
            // 面板高跟随内容：按钮底 + 20（默认字号下约 190；大字号自动长高）
            // 注意：LineBox/RowStep 返回的是当前 DPI 下像素，V() 同样返回当前 DPI 像素，两者可直接相加。
            var titleLabelFont = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold);
            // 纵向节奏一律「设计 px 下限」与「行盒 × 系数」取大者：
            // 只用 DpiScale.V 的固定下限时，150% + 9pt/14pt 两个格子的高度完全相同
            // （矩阵门「字号轴没生效 1.06」实证——大字号下遮罩里一个像素都不长）
            int line = Gdterm.UI.Services.FormFontPolicy.LineBox(Font, this, 1f);
            int lockTitleH = Math.Max(DpiScale.V(this, 24),
                Gdterm.UI.Services.FormFontPolicy.LineBox(titleLabelFont, this, 1.25f));
            int lockMsgBoxH = Math.Max(DpiScale.V(this, 20), (int)Math.Round(line * 1.6));
            int lockTitleY = DpiScale.V(this, 16);
            int lockMsgY = lockTitleY + lockTitleH + DpiScale.V(this, 6);
            int lockInputH2 = Math.Max(DpiScale.V(this, 30), (int)Math.Round(line * 2.0));
            int lockInputY2 = lockMsgY + lockMsgBoxH + DpiScale.V(this, 10);
            int lockBtnH = Math.Max(DpiScale.V(this, 32), (int)Math.Round(line * 2.1));
            int lockBtnY2 = lockInputY2 + lockInputH2 + DpiScale.V(this, 10);
            int lockPanelH = lockBtnY2 + lockBtnH + DpiScale.V(this, 20);
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
                Text = L10n.T("应用已锁定"),
                Location = new Point(DpiScale.V(this, 20), lockTitleY),
                Size = new Size(DpiScale.V(this, 280), lockTitleH),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground,
                Font = titleLabelFont
            };
            _centerPanel.Controls.Add(titleLabel);

            // 消息标签（错误提示用，初始为说明文字）：盒高取行盒，大字号不裁字
            _messageLabel = new AntdUI.Label {
                Text = L10n.T("请输入主密码解锁"),
                Location = new Point(DpiScale.V(this, 20), lockMsgY),
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
                Text = L10n.T("解锁"),
                Location = new Point(DpiScale.V(this, 110), lockBtnY2),
                Size = new Size(DpiScale.V(this, 100), lockBtnH),
                Type = AntdUI.TTypeMini.Primary,
            };
            _unlockButton.Click += (s, e) => OnUnlock();
            _centerPanel.Controls.Add(_unlockButton);

            // 窗口大小变化时重新定位中心面板
            Resize += (s, e) => Recenter();
            Recenter();
        }

        /// <summary>
        /// 居中 + 收口：面板宽 320 设计 px 在窄宿主/高 DPI 下会超出遮罩
        /// （矩阵门 d150 实测 480 宽面板塞进 360 宽宿主 → 越界）。
        /// 面板与内部控件都按宿主宽收口，宽度不够时不外溢。
        /// </summary>
        private void Recenter()
        {
            if (_centerPanel == null) return;
            int side = DpiScale.V(this, 24);
            int maxW = Math.Max(DpiScale.V(this, 160), Width - side);
            int w = Math.Min(DpiScale.V(this, 320), maxW);
            if (_centerPanel.Width != w) _centerPanel.Width = w;

            // 内部控件按面板内宽收口（左右各 20 设计 px 边距）
            int innerPad = DpiScale.V(this, 20);
            int innerW = Math.Max(DpiScale.V(this, 120), w - innerPad * 2);
            foreach (Control child in _centerPanel.Controls)
            {
                if (child == _unlockButton) continue;
                if (child.Width != innerW) child.Width = innerW;
            }
            if (_unlockButton != null)
            {
                int bw = Math.Min(DpiScale.V(this, 100), innerW);
                if (_unlockButton.Width != bw) _unlockButton.Width = bw;
                _unlockButton.Left = (w - bw) / 2;
            }

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
                _messageLabel.Text = L10n.T("请输入密码");
                _messageLabel.ForeColor = GdtermColorTable.Danger;
                _passwordBox.Focus();
                return;
            }

            if (_securityManager.Unlock(password))
            {
                _passwordBox.Text = "";
                _messageLabel.Text = L10n.T("请输入主密码解锁");
                _messageLabel.ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Muted;
            }
            else
            {
                _messageLabel.Text = L10n.T("密码错误，请重试");
                _messageLabel.ForeColor = GdtermColorTable.Danger;
                _passwordBox.SelectAll();
                _passwordBox.Focus();
            }
        }
    }
}
