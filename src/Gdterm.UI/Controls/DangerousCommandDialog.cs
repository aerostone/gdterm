using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Security;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 危险命令三次确认对话框——根据危险等级显示不同级别的警告
    /// Critical: 红色，确认 3 次
    /// High: 橙色，确认 2 次
    /// Medium: 黄色，确认 1 次
    /// </summary>
    public class DangerousCommandDialog : Form
    {
        private readonly string _command;
        private readonly CommandCheckResult _checkResult;
        private int _currentConfirm;
        private Label _titleLabel;
        private Label _commandLabel;
        private Label _descriptionLabel;
        private Label _confirmLabel;
        private Button _confirmButton;
        private Button _cancelButton;
        private ProgressBar _progressBar;
        private CheckBox _rememberChoice;

        /// <summary>
        /// 用户是否确认执行
        /// </summary>
        public bool IsConfirmed { get; private set; }

        /// <summary>
        /// 用户选择记住本次决定（加入白名单）
        /// </summary>
        public bool RememberChoice => _rememberChoice?.Checked ?? false;

        public DangerousCommandDialog(string command, CommandCheckResult checkResult)
        {
            _command = command;
            _checkResult = checkResult;
            _currentConfirm = 0;
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        private void InitializeComponent()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;

            // 根据危险等级设置样式
            Color bgColor, accentColor, textColor;
            string levelText, levelEmoji;
            GetLevelStyle(_checkResult.Level, out bgColor, out accentColor, out textColor, out levelText, out levelEmoji);

            BackColor = bgColor;
            Size = new Size(DpiScale.V(this, 520), DpiScale.V(this, 380));
            Text = $"⚠️ 危险命令确认 - {levelText}";

            // 标题
            _titleLabel = new Label
            {
                Text = $"{levelEmoji} {levelText} - 第 1/{_checkResult.ConfirmCount} 次确认",
                Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(10)
            };

            // 命令显示（等宽语义例外，字号跟随全局）
            _commandLabel = new Label
            {
                Text = _command,
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 10f),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(40, 40, 40),
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 35),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(5)
            };

            // 规则信息
            var infoPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 80),
                Padding = new Padding(15, 10, 15, 10)
            };

            var ruleNameLabel = new Label
            {
                Text = $"规则: {_checkResult.RuleName}  |  分类: {_checkResult.Category}",
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true
            };

            _descriptionLabel = new Label
            {
                Text = _checkResult.Description,
                ForeColor = textColor,
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft
            };

            infoPanel.Controls.Add(_descriptionLabel);
            infoPanel.Controls.Add(ruleNameLabel);

            // 确认进度
            _confirmLabel = new Label
            {
                Text = GetConfirmText(),
                Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter
            };

            // 进度条
            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 8),
                Maximum = _checkResult.ConfirmCount,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };

            // 记住选择
            _rememberChoice = new CheckBox
            {
                Text = "记住此命令，下次不再警告（加入白名单）",
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 20), 0, 0, 0)
            };

            // 按钮面板
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = DpiScale.V(this, 50),
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 8, 10, 8)
            };

            _cancelButton = new Button
            {
                Text = "取消 (Esc)",
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 6), 0, DpiScale.V(this, 6), 0),
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            _cancelButton.Click += (s, e) => { IsConfirmed = false; DialogResult = DialogResult.Cancel; };

            _confirmButton = new Button
            {
                Text = GetConfirmButtonText(),
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 6), 0, DpiScale.V(this, 6), 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, Font.Size + 0.5f, FontStyle.Bold),
                BackColor = accentColor,
                ForeColor = Color.White
            };
            _confirmButton.Click += OnConfirmClick;

            buttonPanel.Controls.Add(_cancelButton);
            buttonPanel.Controls.Add(_confirmButton);

            // 添加控件
            Controls.Add(_confirmLabel);
            Controls.Add(_progressBar);
            Controls.Add(_rememberChoice);
            Controls.Add(infoPanel);
            Controls.Add(_commandLabel);
            Controls.Add(_titleLabel);
            Controls.Add(buttonPanel);

            CancelButton = _cancelButton;
            AcceptButton = _confirmButton;
        }

        private void OnConfirmClick(object sender, EventArgs e)
        {
            _currentConfirm++;
            _progressBar.Value = _currentConfirm;

            if (_currentConfirm >= _checkResult.ConfirmCount)
            {
                // 确认完成
                IsConfirmed = true;
                DialogResult = DialogResult.OK;
            }
            else
            {
                // 还需要继续确认
                _titleLabel.Text = $"⚠️ {_checkResult.Level} - 第 {_currentConfirm + 1}/{_checkResult.ConfirmCount} 次确认";
                _confirmLabel.Text = GetConfirmText();
                _confirmButton.Text = GetConfirmButtonText();

                // 最后一次确认时按钮变红
                if (_currentConfirm == _checkResult.ConfirmCount - 1)
                {
                    _confirmButton.BackColor = Color.Red;
                    _confirmButton.Text = "⚠ 确认执行 ⚠";
                }
            }
        }

        private string GetConfirmText()
        {
            var remaining = _checkResult.ConfirmCount - _currentConfirm;
            if (remaining == _checkResult.ConfirmCount)
                return $"此命令有 {_checkResult.Level} 风险，请确认 {remaining} 次才能执行";
            return $"已确认 {_currentConfirm}/{_checkResult.ConfirmCount}，还需确认 {remaining} 次";
        }

        private string GetConfirmButtonText()
        {
            var remaining = _checkResult.ConfirmCount - _currentConfirm;
            if (remaining == 1) return "⚠ 最终确认 ⚠";
            return $"确认 ({_currentConfirm + 1}/{_checkResult.ConfirmCount})";
        }

        private static void GetLevelStyle(DangerLevel level, out Color bg, out Color accent, out Color text, out string levelText, out string emoji)
        {
            switch (level)
            {
                case DangerLevel.Critical:
                    bg = Color.FromArgb(60, 15, 15);
                    accent = Color.FromArgb(200, 30, 30);
                    text = Color.FromArgb(255, 200, 200);
                    levelText = "严重危险";
                    emoji = "🔴";
                    break;
                case DangerLevel.High:
                    bg = Color.FromArgb(60, 35, 10);
                    accent = Color.FromArgb(220, 120, 20);
                    text = Color.FromArgb(255, 220, 180);
                    levelText = "高风险";
                    emoji = "🟠";
                    break;
                default: // Medium
                    bg = Color.FromArgb(55, 50, 15);
                    accent = Color.FromArgb(180, 160, 30);
                    text = Color.FromArgb(255, 255, 200);
                    levelText = "中等风险";
                    emoji = "🟡";
                    break;
            }
        }
    }
}
