using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 首次使用向导——强制设置主密码
    /// 三步流程：欢迎 → 设置密码 → 确认完成
    /// </summary>
    public class SetupWizardForm : AntdUI.Window
    {
        private readonly ISecurityManager _securityManager;
        private Panel _stepPanel;
        private AntdUI.Label _stepIndicator;
        private AntdUI.Button _nextButton;
        private int _currentStep = 0;

        // Step 1: 欢迎
        private Panel _welcomePanel;
        // Step 2: 设置密码
        private Panel _passwordPanel;
        private AntdUI.Input _passwordBox;
        private AntdUI.Input _confirmBox;
        private AntdUI.Label _strengthLabel;
        private AntdUI.Label _errorLabel;
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

            // 全局 UI 字号（规范规则⑤）：向导在主窗体之前显示，直接接入 FormFontPolicy
            Gdterm.UI.Services.FormFontPolicy.Apply(this);

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
            ClientSize = DpiScale.S(this, 560, 480);
            MinimumSize = DpiScale.S(this, 520, 440);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            BackColor = GdtermColorTable.Background;
            // 规范见 docs/UI-SCALING-CONVENTIONS.md：手写窗体不设 AutoScaleMode，字体由 FormFontPolicy 统一注入全局 UI 字号

            // —— 顶部标题 ——
            var headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 78),
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 24), DpiScale.V(this, 14), DpiScale.V(this, 24), DpiScale.V(this, 10))
            };

            var titleLabel = new AntdUI.Label {
                Text = "欢迎使用 gdterm",
                // 标题强调：相对当前字体放大加粗，不硬编码字族/磅值（规范规则③）,
                Font = new Font(Font.FontFamily, Font.Size + 6f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var subtitleLabel = new AntdUI.Label {
                Text = "绿色运维客户端 · 首次使用请完成以下设置",
                ForeColor = GdtermColorTable.Muted,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft
            };

            headerPanel.Controls.Add(subtitleLabel);
            headerPanel.Controls.Add(titleLabel);

            // —— 步骤指示器 ——
            var stepBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 36),
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 16), 0, DpiScale.V(this, 16), 0)
            };

            _stepIndicator = new AntdUI.Label {
                Text = BuildStepText(0),
                ForeColor = GdtermColorTable.Muted,
                Dock = DockStyle.Fill,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter
            };
            stepBar.Controls.Add(_stepIndicator);

            // —— 底部按钮栏 ——
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = DpiScale.V(this, 60),
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 12), DpiScale.V(this, 16), DpiScale.V(this, 12))
            };

            // 右对齐按钮条：FlowLayoutPanel(RightToLeft) 自动靠右，按钮随文字自适应尺寸（规范规则①②）
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttonPanel.Controls.Add(flow);

            _nextButton = new AntdUI.Button {
                Text = "开始设置 →",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Primary,
                Cursor = Cursors.Hand,
                Name = "nextButton"
            };
            _nextButton.Click += OnNextClick;
            flow.Controls.Add(_nextButton);

            // —— 步骤内容区 ——
            _stepPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(DpiScale.V(this, 28), DpiScale.V(this, 20), DpiScale.V(this, 28), DpiScale.V(this, 12)),
                BackColor = GdtermColorTable.Background
            };

            CreateWelcomePanel();
            CreatePasswordPanel();
            CreateCompletePanel();

            // WinForms Dock：先加 Fill（最低 z-order），后加边缘控件（高 z-order 先占边）
            // 否则 Fill 会先占满客户区，顶部栏和按钮栏被挤没或叠在一起
            Controls.Add(_stepPanel);
            Controls.Add(buttonPanel);
            Controls.Add(stepBar);
            Controls.Add(headerPanel);

            FormClosing += (s, e) =>
            {
                if (!IsCompleted && e.CloseReason == CloseReason.UserClosing)
                {
                    var dr = MessageBox.Show(
                        "尚未设置主密码。\n\n点击「是」退出 gdterm（下次启动仍需完成设置）；\n点击「否」返回继续设置。",
                        "确认退出",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    e.Cancel = dr != DialogResult.Yes;
                }
            };
        }

        private static string BuildStepText(int step)
        {
            // 高亮当前步
            string s1 = step == 0 ? "● 欢迎" : "○ 欢迎";
            string s2 = step == 1 ? "● 设置主密码" : "○ 设置主密码";
            string s3 = step == 2 ? "● 完成" : "○ 完成";
            return s1 + "    →    " + s2 + "    →    " + s3;
        }

        private void CreateWelcomePanel()
        {
            _welcomePanel = new Panel { Dock = DockStyle.Fill };

            // 用 TableLayout 纵向均分，避免内容全贴顶
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 8)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 8)));

            var infoLabel = new AntdUI.Label {
                Text =
                    "为了保护您的连接信息和密码数据，gdterm 使用主密码加密所有敏感信息。\r\n\r\n" +
                    "在开始之前，您需要：\r\n\r\n" +
                    "    ·  设置一个强主密码（至少 12 位）\r\n" +
                    "    ·  密码需包含大小写字母、数字和特殊字符\r\n" +
                    "    ·  此密码用于锁定/解锁应用和加密配置\r\n\r\n" +
                    "请牢记此密码，忘记后无法恢复数据。",
                ForeColor = GdtermColorTable.Foreground,
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(infoLabel, 0, 1);
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 2);
            _welcomePanel.Controls.Add(layout);
        }

        private void CreatePasswordPanel()
        {
            _passwordPanel = new Panel { Dock = DockStyle.Fill };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiScale.V(this, 64)));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            // 行高：标题 / 密码 / 确认 / 强度 / 错误 / 显示密码 / 弹性空白（DPI 缩放，规范规则④）
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 36)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 40)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 40)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 28)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 72)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 32)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var promptLabel = new AntdUI.Label {
                Text = "请设置主密码",
                Font = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            layout.SetColumnSpan(promptLabel, 2);
            layout.Controls.Add(promptLabel, 0, 0);

            var pwdLabel = new AntdUI.Label {
                Text = "密码",
                ForeColor = GdtermColorTable.Foreground,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _passwordBox = new AntdUI.Input {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", Program.GlobalAppearance != null ? Program.GlobalAppearance.UIFontSize : 11f),
                UseSystemPasswordChar = true,
                Margin = new Padding(0, 6, 0, 6)
            };
            _passwordBox.TextChanged += OnPasswordChanged;
            layout.Controls.Add(pwdLabel, 0, 1);
            layout.Controls.Add(_passwordBox, 1, 1);

            var confirmLabel = new AntdUI.Label {
                Text = "确认",
                ForeColor = GdtermColorTable.Foreground,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _confirmBox = new AntdUI.Input {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", Program.GlobalAppearance != null ? Program.GlobalAppearance.UIFontSize : 11f),
                UseSystemPasswordChar = true,
                Margin = new Padding(0, 6, 0, 6)
            };
            layout.Controls.Add(confirmLabel, 0, 2);
            layout.Controls.Add(_confirmBox, 1, 2);

            _strengthLabel = new AntdUI.Label {
                Text = "密码强度：未输入",
                ForeColor = GdtermColorTable.Muted,
                Dock = DockStyle.Fill,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            layout.SetColumnSpan(_strengthLabel, 2);
            layout.Controls.Add(_strengthLabel, 0, 3);

            _errorLabel = new AntdUI.Label {
                Text = "",
                ForeColor = GdtermColorTable.Danger,
                Dock = DockStyle.Fill,
                AutoSize = true,
                TextAlign = ContentAlignment.TopLeft
            };
            layout.SetColumnSpan(_errorLabel, 2);
            layout.Controls.Add(_errorLabel, 0, 4);

            var showPwdCheck = new AntdUI.Checkbox {
                Text = "显示密码",
                Dock = DockStyle.Left,
                AutoSize = true,
                Margin = new Padding(0, DpiScale.V(this, 4), 0, 0)
            };
            showPwdCheck.CheckedChanged += (s, e) =>
            {
                _passwordBox.UseSystemPasswordChar = !showPwdCheck.Checked;
                _confirmBox.UseSystemPasswordChar = !showPwdCheck.Checked;
            };
            layout.SetColumnSpan(showPwdCheck, 2);
            layout.Controls.Add(showPwdCheck, 0, 5);

            // 底部弹性空白，把表单内容留在上半但有均匀行距，不再叠成一团
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 6);

            _passwordPanel.Controls.Add(layout);
        }

        private void CreateCompletePanel()
        {
            _completePanel = new Panel { Dock = DockStyle.Fill };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Color.Transparent
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 48)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, DpiScale.V(this, 40)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));

            var doneLabel = new AntdUI.Label {
                Text = "设置完成",
                // 标题强调：相对当前字体放大加粗（规范规则③）,
                Font = new Font(Font.FontFamily, Font.Size + 7f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Success,
                Dock = DockStyle.Fill,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter
            };

            var summaryLabel = new AntdUI.Label {
                Text =
                    "主密码已设置，您的数据将被安全加密。\r\n\r\n" +
                    "·  配置文件保存在 data/ 目录，可整体迁移\r\n" +
                    "·  空闲 5 分钟后自动锁定\r\n" +
                    "·  Ctrl+` 全局热键可快速呼出/隐藏窗口\r\n\r\n" +
                    "点击「进入 gdterm」开始使用。",
                ForeColor = GdtermColorTable.Foreground,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter
            };

            layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(doneLabel, 0, 1);
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 2);
            layout.Controls.Add(summaryLabel, 0, 3);
            layout.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 4);

            _completePanel.Controls.Add(layout);
        }

        private void ShowStep(int step)
        {
            _currentStep = step;
            _stepPanel.Controls.Clear();
            if (_stepIndicator != null)
                _stepIndicator.Text = BuildStepText(step);

            Control panel;
            switch (step)
            {
                case 0:
                    panel = _welcomePanel;
                    _nextButton.Text = "开始设置 →";
                    break;
                case 1:
                    panel = _passwordPanel;
                    _nextButton.Text = "确认设置 →";
                    break;
                case 2:
                    panel = _completePanel;
                    _nextButton.Text = "进入 gdterm";
                    break;
                default:
                    return;
            }

            panel.Dock = DockStyle.Fill;
            _stepPanel.Controls.Add(panel);
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
                _strengthLabel.ForeColor = GdtermColorTable.Muted;
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
            if (score <= 2) { strength = "弱"; color = GdtermColorTable.Danger; }
            else if (score <= 4) { strength = "中"; color = GdtermColorTable.Warning; }
            else { strength = "强"; color = GdtermColorTable.Success; }

            _strengthLabel.Text = string.Format("密码强度：{0}（{1} 字符）", strength, pwd.Length);
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

            try
            {
                _securityManager.SetMasterPassword(null, password);
                // 设置后立即解锁，进入主界面时不显示锁定遮罩
                _securityManager.Unlock(password);
                _errorLabel.Text = "";
                return true;
            }
            catch (WeakPasswordException ex)
            {
                _errorLabel.Text = "密码不符合要求：\n· " + string.Join("\n· ", ex.Violations);
                return false;
            }
            catch (Exception ex)
            {
                _errorLabel.Text = "设置失败：" + ex.Message;
                return false;
            }
        }
    }
}
