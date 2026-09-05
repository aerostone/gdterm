using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 危险命令三次确认对话框——根据危险等级显示不同级别的警告
    /// Critical: 红色，确认 3 次
    /// High: 橙色，确认 2 次
    /// Medium: 黄色，确认 1 次
    /// </summary>
    public class DangerousCommandDialog : AntdUI.Window
    {
        private readonly string _command;
        private readonly CommandCheckResult _checkResult;
        private int _currentConfirm;
        private AntdUI.Label _titleLabel;
        private AntdUI.Input _commandLabel;
        private AntdUI.Label _descriptionLabel;
        private AntdUI.Label _confirmLabel;
        private AntdUI.Button _confirmButton;
        private AntdUI.Button _cancelButton;
        private ProgressBar _progressBar;   // 原生 ProgressBar：确认次数进度语义简单
        private AntdUI.Checkbox _rememberChoice;

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
        }

        private void InitializeComponent()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            AutoHandDpi = false; // 尺寸已按 DpiScale 预缩放，关掉 AntdUI 的自动 DPI 避免二次放大
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
            _titleLabel = new AntdUI.Label {
                Text = $"{levelEmoji} {levelText} - 第 1/{_checkResult.ConfirmCount} 次确认",
                Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(10)
            };

            // 命令显示（等宽语义例外，字号跟随全局）
            _commandLabel = new AntdUI.Input {
                Text = _command,
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 10f),
                ReadOnly = true,
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 35),
                TextAlign = HorizontalAlignment.Center
            };

            // 规则信息
            var infoPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 80),
                Padding = new Padding(15, 10, 15, 10)
            };

            var ruleNameLabel = new AntdUI.Label {
                Text = $"规则: {_checkResult.RuleName}  |  分类: {_checkResult.Category}",
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true
            };

            _descriptionLabel = new AntdUI.Label {
                Text = _checkResult.Description,
                ForeColor = textColor,
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft
            };

            infoPanel.Controls.Add(_descriptionLabel);
            infoPanel.Controls.Add(ruleNameLabel);

            // 确认进度
            _confirmLabel = new AntdUI.Label {
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
            _rememberChoice = new AntdUI.Checkbox {
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

            _cancelButton = new AntdUI.Button {
                Text = "取消 (Esc)",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Default
            };
            _cancelButton.Click += (s, e) => { IsConfirmed = false; DialogResult = DialogResult.Cancel; };

            _confirmButton = new AntdUI.Button {
                Text = GetConfirmButtonText(),
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size + 0.5f, FontStyle.Bold),
                Type = AccentToTType(accentColor)
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
                // 确认完成,
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
                    _confirmButton.Type = AntdUI.TTypeMini.Error;
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

        /// <summary>危险等级主色 → AntdUI 按钮语义类型（Critical红/High橙→Warn/Medium黄→Warn）。</summary>
        private static AntdUI.TTypeMini AccentToTType(Color accent)
        {
            // Critical: 深红 → Error；High/Medium 橙黄 → Warn
            if (accent.R > 190 && accent.G < 80) return AntdUI.TTypeMini.Error;
            return AntdUI.TTypeMini.Warn;
        }

        private static void GetLevelStyle(DangerLevel level, out Color bg, out Color accent, out Color text, out string levelText, out string emoji)
        {
            switch (level)
            {
                case DangerLevel.Critical:
                    bg = GdtermColorTable.Background;
                    accent = GdtermColorTable.Danger;
                    text = GdtermColorTable.Danger;
                    levelText = "严重危险";
                    emoji = "🔴";
                    break;
                case DangerLevel.High:
                    bg = GdtermColorTable.Background;
                    accent = GdtermColorTable.Warning;
                    text = GdtermColorTable.Warning;
                    levelText = "高风险";
                    emoji = "🟠";
                    break;
                default: // Medium
                    bg = GdtermColorTable.Background;
                    accent = GdtermColorTable.Warning;
                    text = GdtermColorTable.Warning;
                    levelText = "中等风险";
                    emoji = "🟡";
                    break;
            }
        }
    }
}
