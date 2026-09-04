using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 密码库解锁对话框
    /// 在连接时如果密码库未解锁，弹出此对话框
    /// </summary>
    public class KeePassUnlockForm : Form
    {
        private readonly IKeePassService _keepassService;
        private TextBox _passwordBox;
        private Label _errorLabel;
        private Label _statusLabel;
        private Button _unlockButton;

        /// <summary>
        /// 解锁是否成功
        /// </summary>
        public bool IsUnlocked { get; private set; }

        public KeePassUnlockForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        private void InitializeComponent()
        {
            Text = "解锁密码库";
            {
                float grow = FormFontPolicy.UiFontSize / 9f;
                Size = DpiScale.S(this, 420, (int)(252 * Math.Max(1f, grow)));
            }

            // ── 头部：图标 + 标题/状态（Dock=Top，高度字体驱动）──
            var header = new Panel { Dock = DockStyle.Top, BackColor = Color.FromArgb(35, 35, 35) };
            int titleH, statusH;
            using (var hf = FormFontPolicy.UiFont(+4f, FontStyle.Bold))
            using (var sf = FormFontPolicy.UiFont(+0.5f))
            {
                titleH = TextRenderer.MeasureText("密码库已锁定", hf).Height;
                statusH = TextRenderer.MeasureText("请输入主密码以解锁 KeePass 密码库", sf).Height;
            }
            header.Height = Math.Max(DpiScale.V(this, 60), titleH + statusH + DpiScale.V(this, 16));

            var iconLabel = new Label
            {
                Text = "🔐",
                Font = new Font("Segoe UI Emoji", DpiScale.V(this, 28)),
                Location = DpiScale.P(this, 20, 12),
                Size = DpiScale.S(this, 55, 55),
                TextAlign = ContentAlignment.MiddleCenter
            };
            var titleLabel = new Label
            {
                Text = "密码库已锁定",
                Font = Services.FormFontPolicy.UiFont(+4f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = DpiScale.P(this, 80, 12)
            };
            _statusLabel = new Label
            {
                Text = "请输入主密码以解锁 KeePass 密码库",
                Font = Services.FormFontPolicy.UiFont(+0.5f),
                ForeColor = Color.FromArgb(180, 180, 180),
                AutoSize = true,
                Location = new Point(DpiScale.V(this, 80), DpiScale.V(this, 12) + titleH + DpiScale.V(this, 4))
            };
            header.Controls.Add(iconLabel);
            header.Controls.Add(titleLabel);
            header.Controls.Add(_statusLabel);
            Controls.Add(header);

            // ── 中部：密码输入 + 错误提示（TableLayoutPanel，行距字体驱动）──
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.FromArgb(35, 35, 35),
                Padding = new Padding(DpiScale.V(this, 16))
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var pwdLabel = new Label
            {
                Text = "主密码：",
                Font = Services.FormFontPolicy.UiFont(+1f),
                ForeColor = Color.FromArgb(200, 200, 200),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(3, DpiScale.V(this, 6), 3, 0)
            };
            _passwordBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 11f),
                UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(3, DpiScale.V(this, 4), 3, 0)
            };
            _passwordBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) OnUnlockClick(s, e);
            };
            body.Controls.Add(pwdLabel, 0, 0);
            body.Controls.Add(_passwordBox, 1, 0);

            _errorLabel = new Label
            {
                Text = "",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(255, 100, 100),
                AutoSize = true,
                Margin = new Padding(3, DpiScale.V(this, 8), 3, 0)
            };
            body.Controls.Add(_errorLabel, 0, 1);
            body.SetColumnSpan(_errorLabel, 2);
            Controls.Add(body);

            // ── 底部：解锁（主）+ 取消 ──
            _unlockButton = new Button
            {
                Text = "解锁",
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(+1f),
                ForeColor = Color.White
            };
            _unlockButton.Click += OnUnlockClick;

            var cancelButton = new Button
            {
                Text = "取消",
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(+1f),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            // 解锁保持品牌蓝（此窗体历史上即蓝底主按钮；DialogStyle.Primary 为终端绿）
            _unlockButton.BackColor = Color.FromArgb(0, 122, 204);
            _unlockButton.FlatAppearance.BorderSize = 0;
            _unlockButton.AutoSize = true;
            _unlockButton.Padding = new Padding(DpiScale.V(this, 10), 0, DpiScale.V(this, 10), 0);
            cancelButton.BackColor = Color.FromArgb(60, 60, 60);
            cancelButton.FlatAppearance.BorderSize = 0;
            cancelButton.AutoSize = true;
            cancelButton.Padding = new Padding(DpiScale.V(this, 10), 0, DpiScale.V(this, 10), 0);

            Controls.Add(DialogStyle.ButtonStrip(_unlockButton, cancelButton));

            AcceptButton = _unlockButton;
            CancelButton = cancelButton;
        }

        private async void OnUnlockClick(object sender, EventArgs e)
        {
            var password = _passwordBox.Text;
            if (string.IsNullOrEmpty(password))
            {
                _errorLabel.Text = "请输入密码";
                return;
            }

            _unlockButton.Enabled = false;
            _unlockButton.Text = "解锁中...";
            _errorLabel.Text = "";

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
                    _errorLabel.Text = "密码错误或密码库文件损坏";
                    _passwordBox.SelectAll();
                    _passwordBox.Focus();
                }
            }
            catch (Exception ex)
            {
                _errorLabel.Text = $"解锁失败：{ex.Message}";
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
