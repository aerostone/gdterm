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
            Size = DpiScale.S(this, 420, 260);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(35, 35, 35);

            // 图标 + 提示
            var iconLabel = new Label
            {
                Text = "🔐",
                Font = new Font("Segoe UI Emoji", 28f),
                Location = DpiScale.P(this, 20, 15),
                Size = DpiScale.S(this, 55, 55),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var titleLabel = new Label
            {
                Text = "密码库已锁定",
                Font = Services.FormFontPolicy.UiFont(+4f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = DpiScale.P(this, 80, 18),
                Size = DpiScale.S(this, 300, 30)
            };

            _statusLabel = new Label
            {
                Text = "请输入主密码以解锁 KeePass 密码库",
                Font = Services.FormFontPolicy.UiFont(+0.5f),
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = DpiScale.P(this, 80, 48),
                Size = DpiScale.S(this, 300, 22)
            };

            // 密码输入
            var pwdLabel = new Label
            {
                Text = "主密码：",
                Font = Services.FormFontPolicy.UiFont(+1f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = DpiScale.P(this, 20, 90),
                Size = DpiScale.S(this, 70, 25)
            };

            _passwordBox = new TextBox
            {
                Location = DpiScale.P(this, 95, 87),
                Size = DpiScale.S(this, 290, 28),
                Font = new Font("Consolas", 11f),
                UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _passwordBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) OnUnlockClick(s, e);
            };

            // 错误提示
            _errorLabel = new Label
            {
                Text = "",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(255, 100, 100),
                Location = DpiScale.P(this, 20, 120),
                Size = DpiScale.S(this, 370, 25)
            };

            // 按钮
            _unlockButton = new Button
            {
                Text = "解锁",
                Size = DpiScale.S(this, 100, 34),
                Location = DpiScale.P(this, 285, 155),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(+1f),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            _unlockButton.Click += OnUnlockClick;

            var cancelButton = new Button
            {
                Text = "取消",
                Size = DpiScale.S(this, 80, 34),
                Location = DpiScale.P(this, 195, 155),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(+1f),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[]
            {
                iconLabel, titleLabel, _statusLabel,
                pwdLabel, _passwordBox, _errorLabel,
                cancelButton, _unlockButton
            });

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
