using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 密码库解锁对话框（AntdUI 试点版）。
    /// 首个迁移到 AntdUI 控件体系的窗体：
    ///   Window 基类（暗色边框/自绘标题交互）+ Input（密码框）+ Button（主/次按钮）。
    /// 设计对照：docs/DESIGN-LANGUAGE.md —— 薄荷绿 Primary、石墨暗色。
    /// 若 AntdUI 试点不稳定，回退 git revert 即可恢复原生实现。
    /// </summary>
    public class KeePassUnlockForm : AntdUI.Window
    {
        private readonly IKeePassService _keepassService;
        private AntdUI.Input _passwordBox;
        private AntdUI.Button _unlockButton;
        private AntdUI.Button _cancelButton;

        /// <summary>解锁是否成功。</summary>
        public bool IsUnlocked { get; private set; }

        public KeePassUnlockForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
            InitializeComponent();
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导
        }

        private void InitializeComponent()
        {
            Text = "解锁密码库";
            Size = DpiScale.S(this, 440, 250); // 初始基准，构造末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定坐标在大字号/高 DPI 下控件挤压）
            int clientW = DpiScale.V(this, 440);
            int pad = DpiScale.V(this, 24);
            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            int y = DpiScale.V(this, 24);

            // ── 标题区 ──
            var title = new AntdUI.Label {
                Text = "🔐 密码库已锁定",
                Font = new Font("Segoe UI Emoji", 14F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(title);
            y += Math.Max(DpiScale.V(this, 44), FormFontPolicy.RowStep(this) + DpiScale.V(this, 10));

            var status = new AntdUI.Label {
                Text = "请输入主密码以解锁 KeePass 密码库",
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(status);
            y += Math.Max(DpiScale.V(this, 40), FormFontPolicy.RowStep(this) + DpiScale.V(this, 6));

            // ── 密码输入（AntdUI.Input：占位符/清除钮/回车提交）──
            _passwordBox = new AntdUI.Input {
                Location = new Point(pad, y),
                Size = new Size(clientW - pad * 2, fieldH),
                PlaceholderText = "主密码",
                UseSystemPasswordChar = true
            };
            _passwordBox.TextChanged += (s, e) =>
            {
                // 输入变化即清除旧错误占位（原生版在错误 Label 上显示，这里用 Input 内建错误态）
            };
            Controls.Add(_passwordBox);
            y += fieldH + DpiScale.V(this, 16);

            // ── 按钮行：取消（次）+ 解锁（主，绿，最右）──
            int btnW = DpiScale.V(this, 96);
            int btnGap = DpiScale.V(this, 12);
            _cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - pad - btnW * 2 - btnGap, y),
                Type = AntdUI.TTypeMini.Default
            };
            _cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancelButton);

            _unlockButton = new AntdUI.Button {
                Text = "解锁",
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - pad - btnW, y),
                Type = AntdUI.TTypeMini.Primary
            };
            _unlockButton.Click += OnUnlockClick;
            Controls.Add(_unlockButton);

            ClientSize = new Size(clientW, y + fieldH + DpiScale.V(this, 24));

            AcceptButton = _unlockButton;
            CancelButton = _cancelButton;
        }

        private void OnUnlockClick(object sender, EventArgs e)
        {
            var password = _passwordBox.Text;
            if (string.IsNullOrEmpty(password))
            {
                AntdUI.Message.error(this, "请输入密码");
                return;
            }

            _unlockButton.Enabled = false;
            _unlockButton.Text = "解锁中...";
            UnlockAsync(password);
        }

        private async void UnlockAsync(string password)
        {
            try
            {
                var result = await _keepassService.EnsureDatabaseAsync(password);
                if (result)
                {
                    IsUnlocked = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    AntdUI.Message.error(this, "密码错误或密码库文件损坏");
                    _passwordBox.Text = "";
                    _passwordBox.Focus();
                }
            }
            catch (Exception ex)
            {
                AntdUI.Message.error(this, $"解锁失败：{ex.Message}");
            }
            finally
            {
                _unlockButton.Enabled = true;
                _unlockButton.Text = "解锁";
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _passwordBox.Focus();
        }
    }
}
