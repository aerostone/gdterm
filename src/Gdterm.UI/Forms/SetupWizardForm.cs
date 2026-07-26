using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Security;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 首次使用向导——强制设置主密码
    /// 三步流程：欢迎 → 设置密码 → 确认完成
    /// </summary>
    public class SetupWizardForm : Form
    {
        private readonly ISecurityManager _securityManager;
        private Panel _stepPanel;
        private int _currentStep = 0;

        // Step 1: 欢迎
        private Panel _welcomePanel;
        // Step 2: 设置密码
        private Panel _passwordPanel;
        private TextBox _passwordBox;
        private TextBox _confirmBox;
        private Label _strengthLabel;
        private Label _errorLabel;
        // Step 3: 完成
        private Panel _completePanel;

        /// <summary>
        /// 用户是否完成了向导
        /// </summary>
        public bool IsCompleted { get; private set; }

        public SetupWizardForm(ISecurityManager securityManager)
        {
            _securityManager = securityManager;
            InitializeComponent();

            // 加载应用图标
            try
            {
                var iconStream = typeof(SetupWizardForm).Assembly.GetManifestResourceStream("Gdterm.UI.Resources.gdterm.ico");
                if (iconStream != null)
                {
                    this.Icon = new Icon(iconStream);
                    iconStream.Dispose();
                }
            }
            catch { }

            ShowStep(0);
        }

        private void InitializeComponent()
        {
            Text = "gdterm - 首次使用设置";
            Size = new Size(580, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(30, 30, 30);

            // 顶部标题栏
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = Color.FromArgb(45, 45, 45),
                Padding = new Padding(20, 15, 20, 10)
            };

            var titleLabel = new Label
            {
                Text = "🔧 欢迎使用 gdterm",
                Font = new Font("Microsoft YaHei", 16f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Top,
                Height = 35
            };

            var subtitleLabel = new Label
            {
                Text = "绿色运维客户端 · 首次使用请完成以下设置",
                Font = new Font("Microsoft YaHei", 9.5f),
                ForeColor = Color.FromArgb(180, 180, 180),
                Dock = DockStyle.Fill
            };

            headerPanel.Controls.Add(subtitleLabel);
            headerPanel.Controls.Add(titleLabel);

            // 步骤指示器
            var stepBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(35, 35, 35),
                Padding = new Padding(20, 0, 20, 0)
            };

            var stepLabel = new Label
            {
                Text = "① 欢迎    ② 设置主密码    ③ 完成",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(140, 140, 140),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            stepBar.Controls.Add(stepLabel);

            // 步骤内容面板
            _stepPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(30, 20, 30, 10)
            };

            // 底部按钮栏
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 55,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 10, 10, 10),
                BackColor = Color.FromArgb(35, 35, 35)
            };

            var nextButton = new Button
            {
                Text = "开始设置 →",
                Size = new Size(120, 36),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 10f),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                DialogResult = DialogResult.None
            };
            nextButton.Click += OnNextClick;
            nextButton.Name = "nextButton";

            buttonPanel.Controls.Add(nextButton);

            // 创建三步面板
            CreateWelcomePanel();
            CreatePasswordPanel();
            CreateCompletePanel();

            // Dock 顺序：先 Bottom/Top，最后 Fill，避免欢迎文案被挤没
            Controls.Add(buttonPanel);
            Controls.Add(headerPanel);
            Controls.Add(stepBar);
            Controls.Add(_stepPanel);

            // 禁用关闭按钮（必须完成设置）
            FormClosing += (s, e) =>
            {
                if (!IsCompleted && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    MessageBox.Show("请先完成主密码设置，这是安全要求。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        private void CreateWelcomePanel()
        {
            _welcomePanel = new Panel { Dock = DockStyle.Fill };

            var infoLabel = new Label
            {
                Text =
                    "为了保护您的连接信息和密码数据，\r\n" +
                    "gdterm 使用主密码加密所有敏感信息。\r\n\r\n" +
                    "在开始之前，您需要：\r\n\r\n" +
                    "  ✓  设置一个强主密码（至少 12 位）\r\n" +
                    "  ✓  密码需包含大小写字母、数字和特殊字符\r\n" +
                    "  ✓  此密码用于锁定/解锁应用和加密配置\r\n\r\n" +
                    "⚠ 请牢记此密码，忘记后无法恢复数据！",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(220, 220, 220),
                AutoSize = false,
                Location = new Point(0, 0),
                Size = new Size(480, 280),
                MaximumSize = new Size(480, 0),
                TextAlign = ContentAlignment.TopLeft
            };

            _welcomePanel.AutoScroll = true;
            _welcomePanel.Controls.Add(infoLabel);
        }

        private void CreatePasswordPanel()
        {
            _passwordPanel = new Panel { Dock = DockStyle.Fill };

            var promptLabel = new Label
            {
                Text = "请设置主密码：",
                Font = new Font("Microsoft YaHei", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(0, 10),
                Size = new Size(480, 30)
            };

            var pwdLabel = new Label
            {
                Text = "密码：",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(0, 55),
                Size = new Size(60, 25)
            };

            _passwordBox = new TextBox
            {
                Location = new Point(65, 52),
                Size = new Size(400, 28),
                Font = new Font("Consolas", 11f),
                UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _passwordBox.TextChanged += OnPasswordChanged;

            var confirmLabel = new Label
            {
                Text = "确认：",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(0, 95),
                Size = new Size(60, 25)
            };

            _confirmBox = new TextBox
            {
                Location = new Point(65, 92),
                Size = new Size(400, 28),
                Font = new Font("Consolas", 11f),
                UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            _strengthLabel = new Label
            {
                Text = "密码强度：未输入",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(140, 140, 140),
                Location = new Point(0, 135),
                Size = new Size(480, 25)
            };

            _errorLabel = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(255, 100, 100),
                Location = new Point(0, 165),
                Size = new Size(480, 80)
            };

            var showPwdCheck = new CheckBox
            {
                Text = "显示密码",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(160, 160, 160),
                Location = new Point(0, 250),
                Size = new Size(100, 25)
            };
            showPwdCheck.CheckedChanged += (s, e) =>
            {
                _passwordBox.UseSystemPasswordChar = !showPwdCheck.Checked;
                _confirmBox.UseSystemPasswordChar = !showPwdCheck.Checked;
            };

            _passwordPanel.Controls.AddRange(new Control[]
            {
                promptLabel, pwdLabel, _passwordBox,
                confirmLabel, _confirmBox,
                _strengthLabel, _errorLabel, showPwdCheck
            });
        }

        private void CreateCompletePanel()
        {
            _completePanel = new Panel { Dock = DockStyle.Fill };

            var checkLabel = new Label
            {
                Text = "✅",
                Font = new Font("Segoe UI Emoji", 36f),
                ForeColor = Color.FromArgb(80, 200, 80),
                Location = new Point(200, 30),
                Size = new Size(80, 70),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var doneLabel = new Label
            {
                Text = "设置完成！",
                Font = new Font("Microsoft YaHei", 16f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(0, 110),
                Size = new Size(480, 40),
                TextAlign = ContentAlignment.MiddleCenter
            };

            var summaryLabel = new Label
            {
                Text = "主密码已设置，您的数据将被安全加密。\n\n" +
                       "• 配置文件保存在 data/ 目录，可整体迁移\n" +
                       "• 空闲 5 分钟后自动锁定\n" +
                       "• Ctrl+` 全局热键可快速呼出/隐藏窗口\n\n" +
                       "点击「进入 gdterm」开始使用。",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(0, 160),
                Size = new Size(480, 160),
                TextAlign = ContentAlignment.TopCenter
            };

            _completePanel.Controls.AddRange(new Control[] { checkLabel, doneLabel, summaryLabel });
        }

        private void ShowStep(int step)
        {
            _currentStep = step;
            _stepPanel.Controls.Clear();

            Control panel;
            Button nextBtn = FindButton();

            switch (step)
            {
                case 0:
                    panel = _welcomePanel;
                    if (nextBtn != null) nextBtn.Text = "开始设置 →";
                    break;
                case 1:
                    panel = _passwordPanel;
                    if (nextBtn != null) nextBtn.Text = "确认设置 →";
                    break;
                case 2:
                    panel = _completePanel;
                    if (nextBtn != null) nextBtn.Text = "进入 gdterm";
                    break;
                default:
                    return;
            }

            panel.Dock = DockStyle.Fill;
            _stepPanel.Controls.Add(panel);
        }

        private Button FindButton()
        {
            foreach (Control c in Parent?.Controls ?? Controls)
            {
                if (c is FlowLayoutPanel flp)
                {
                    foreach (Control fc in flp.Controls)
                    {
                        if (fc is Button btn) return btn;
                    }
                }
            }
            // 递归搜索
            return FindButtonRecursive(this);
        }

        private Button FindButtonRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button btn && btn.Name == "nextButton") return btn;
                if (c.HasChildren)
                {
                    var found = FindButtonRecursive(c);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private void OnNextClick(object sender, EventArgs e)
        {
            switch (_currentStep)
            {
                case 0:
                    ShowStep(1);
                    _passwordBox.Focus();
                    break;

                case 1:
                    if (ValidateAndSetPassword())
                    {
                        ShowStep(2);
                    }
                    break;

                case 2:
                    IsCompleted = true;
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
            }
        }

        private void OnPasswordChanged(object sender, EventArgs e)
        {
            var pwd = _passwordBox.Text;
            if (string.IsNullOrEmpty(pwd))
            {
                _strengthLabel.Text = "密码强度：未输入";
                _strengthLabel.ForeColor = Color.FromArgb(140, 140, 140);
                return;
            }

            int score = 0;
            if (pwd.Length >= 12) score++;
            if (pwd.Length >= 16) score++;
            if (pwd.Any(char.IsUpper)) score++;
            if (pwd.Any(char.IsLower)) score++;
            if (pwd.Any(char.IsDigit)) score++;
            if (pwd.Any(ch => !char.IsLetterOrDigit(ch))) score++;

            string strength;
            Color color;
            if (score <= 2) { strength = "弱"; color = Color.FromArgb(255, 80, 80); }
            else if (score <= 4) { strength = "中"; color = Color.FromArgb(255, 200, 60); }
            else { strength = "强"; color = Color.FromArgb(80, 220, 80); }

            _strengthLabel.Text = $"密码强度：{strength}（{pwd.Length} 字符）";
            _strengthLabel.ForeColor = color;
        }

        private bool ValidateAndSetPassword()
        {
            var password = _passwordBox.Text;
            var confirm = _confirmBox.Text;

            if (string.IsNullOrEmpty(password))
            {
                _errorLabel.Text = "密码不能为空";
                return false;
            }

            if (password != confirm)
            {
                _errorLabel.Text = "两次输入的密码不一致";
                return false;
            }

            // 尝试设置密码（会触发强度校验）
            try
            {
                _securityManager.SetMasterPassword(null, password);
                // 设置后立即解锁，这样进入主界面时不会显示锁定遮罩
                _securityManager.Unlock(password);
                _errorLabel.Text = "";
                return true;
            }
            catch (WeakPasswordException ex)
            {
                _errorLabel.Text = "密码不符合要求：\n• " + string.Join("\n• ", ex.Violations);
                return false;
            }
            catch (Exception ex)
            {
                _errorLabel.Text = $"设置失败：{ex.Message}";
                return false;
            }
        }
    }
}
