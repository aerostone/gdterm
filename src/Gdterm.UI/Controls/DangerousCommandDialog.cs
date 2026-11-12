using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 危险命令确认对话框——根据危险等级显示不同级别的警告
    /// Critical: 红色，确认 3 次
    /// High: 橙色，确认 2 次
    /// Medium: 黄色，确认 1 次
    ///
    /// 2026-11 审计后：一次输入可能是多行（粘贴 20 行、宏回放、批量），
    /// 所以对话框显示的是“整批判定”——把命中行逐条列出来、把命中片段高亮，
    /// 让用户看见自己到底要放过什么，而不是对着一个被截断的单行发呆。
    /// 只接受 GateDecision：把"单条命令 + 一次 Check"的旧构造删掉，就没人能绕过整批判定直接弹窗。
    /// </summary>
    public class DangerousCommandDialog : AntdUI.Window
    {
        private readonly GateDecision _decision;
        private readonly string _command;
        private int _currentConfirm;
        private AntdUI.Label _titleLabel;
        private RichTextBox _preview;
        private AntdUI.Label _descriptionLabel;
        private AntdUI.Label _confirmLabel;
        private AntdUI.Button _confirmButton;
        private AntdUI.Button _cancelButton;
        private ProgressBar _progressBar;   // 原生 ProgressBar：确认次数进度语义简单
        private AntdUI.Checkbox _sessionExempt;
        private AntdUI.Checkbox _permanentExempt;

        /// <summary>
        /// 用户是否确认执行
        /// </summary>
        public bool IsConfirmed { get; private set; }

        /// <summary>本次运行内不再提示命中规则（内存态，重启失效）</summary>
        public bool SessionExemptRequested { get; private set; }

        /// <summary>把命中规则永久豁免（写入配置文件）</summary>
        public bool PermanentExemptRequested { get; private set; }

        /// <summary>本次需要豁免的规则 Id 集合（Critical 不参与豁免）</summary>
        public IReadOnlyList<string> ExemptRuleIds
        {
            get
            {
                return _decision.Hits
                    .Where(h => h.Level != DangerLevel.Critical)
                    .Select(h => h.RuleId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
            }
        }

        /// <summary>是否存在不可豁免的命中（Critical 级）</summary>
        public bool HasCriticalHit
        {
            get { return _decision.Hits.Any(h => h.Level == DangerLevel.Critical); }
        }

        public DangerousCommandDialog(GateDecision decision)
        {
            if (decision == null) throw new ArgumentNullException("decision");
            _decision = decision;
            _command = decision.Text ?? "";
            _currentConfirm = 0;
            InitializeComponent();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private DangerLevel HighestLevel { get { return _decision.HighestLevel; } }

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
            GetLevelStyle(HighestLevel, out bgColor, out accentColor, out textColor, out levelText, out levelEmoji);

            bool batch = _decision.IsBatch || _decision.Hits.Count > 1;

            BackColor = bgColor;
            Size = new Size(DpiScale.V(this, 560), DpiScale.V(this, batch ? 500 : 400));
            Text = L10n.Tf("⚠️ 危险命令确认 - {0}", levelText);

            // 标题
            _titleLabel = new AntdUI.Label {
                Text = L10n.Tf("⚠️ {0} {1} - 第 1/{2} 次确认", levelEmoji, levelText, ConfirmationCount),
                Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(10)
            };

            // 命令预览（命中片段高亮）
            _preview = new RichTextBox {
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = GdtermColorTable.Background,
                ForeColor = textColor,
                Font = Services.FormFontPolicy.MonoFont(),
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, batch ? 170 : 62),
                WordWrap = false,
                ScrollBars = batch ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None,
                DetectUrls = false,
                TabStop = false
            };
            FillPreview();

            // 规则信息
            var infoPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, batch ? 96 : 84),
                Padding = DpiScale.Pad(this, 15, 10, 15, 10)
            };

            var ruleNameLabel = new AntdUI.Label {
                Text = BuildRuleSummary(),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true
            };

            _descriptionLabel = new AntdUI.Label {
                Text = BuildDescription(),
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
                Maximum = ConfirmationCount,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };

            // 豁免选择：粒度是“规则”，不是“这一条命令”
            // （整条命令匹配对带参数的命令形同虚设：豁免了 rm -rf /tmp/a，rm -rf /tmp/b 照样弹）
            _sessionExempt = new AntdUI.Checkbox {
                Text = L10n.T("本次运行内不再提示命中的规则"),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                Enabled = !HasCriticalHit,
                Padding = new Padding(DpiScale.V(this, 20), 0, 0, 0)
            };

            _permanentExempt = new AntdUI.Checkbox {
                Text = L10n.T("永久豁免命中的规则（写入配置）"),
                ForeColor = textColor,
                Dock = DockStyle.Top,
                AutoSize = true,
                Visible = !HasCriticalHit,
                Padding = new Padding(DpiScale.V(this, 20), 0, 0, 0)
            };

            // 按钮面板
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = DpiScale.V(this, 50),
                FlowDirection = FlowDirection.RightToLeft,
                Padding = DpiScale.Pad(this, 10, 8, 10, 8)
            };

            _cancelButton = new AntdUI.Button {
                Text = L10n.T("取消 (Esc)"),
                AutoSize = true,
                Type = AntdUI.TTypeMini.Default,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5))
            };
            _cancelButton.Click += (s, e) => { IsConfirmed = false; DialogResult = DialogResult.Cancel; };

            _confirmButton = new AntdUI.Button {
                Text = GetConfirmButtonText(),
                AutoSize = true,
                Font = new Font(Font.FontFamily, Font.Size + 0.5f, FontStyle.Bold),
                Type = AccentToTType(accentColor),
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5))
            };
            _confirmButton.Click += OnConfirmClick;

            buttonPanel.Controls.Add(_cancelButton);
            buttonPanel.Controls.Add(_confirmButton);

            // 添加控件（Dock=Top 顺序：后加的在上，因此倒序加入）
            Controls.Add(_confirmLabel);
            Controls.Add(_progressBar);
            Controls.Add(_permanentExempt);
            Controls.Add(_sessionExempt);
            Controls.Add(infoPanel);
            Controls.Add(_preview);
            Controls.Add(_titleLabel);
            Controls.Add(buttonPanel);

            CancelButton = _cancelButton;
            AcceptButton = _confirmButton;

            // 该确认框由终端运行时动态弹出，也必须跟随外观设置中的 UI 字号；
            // 控件尺寸由自身 AutoSize/布局容器负责。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        private int ConfirmationCount
        {
            get { return Math.Max(1, _decision.Hits.Count == 0 ? 1 : _decision.ConfirmCount); }
        }

        /// <summary>
        /// 预览正文：逐行列出命中行（带原始行号），命中片段用浅红底选中高亮。
        /// 多行时把“整批 20 行里有 3 行命中”这件事显式说出来——用户最怕的是不知道还剩什么没看见。
        /// </summary>
        private void FillPreview()
        {
            var sb = new StringBuilder();
            var ranges = new List<Tuple<int, int, DangerLevel>>();   // start, length, level

            foreach (var hit in _decision.Hits)
            {
                if (sb.Length > 0) sb.Append('\n');
                var prefix = _decision.IsBatch ? (hit.LineNumber.ToString() + " │ ") : "";
                sb.Append(prefix).Append(hit.Line);

                var check = hit.Check;
                if (check != null && check.MatchIndex >= 0 && check.MatchLength > 0
                    && check.MatchIndex + check.MatchLength <= hit.Line.Length)
                {
                    ranges.Add(Tuple.Create(sb.Length - hit.Line.Length + check.MatchIndex, check.MatchLength, hit.Level));
                }
            }

            _preview.Text = sb.ToString();

            foreach (var range in ranges)
            {
                try
                {
                    _preview.Select(range.Item1, range.Item2);
                    _preview.SelectionBackColor = range.Item3 == DangerLevel.Critical
                        ? GdtermColorTable.Translucent(GdtermColorTable.Danger, 120)
                        : GdtermColorTable.Translucent(GdtermColorTable.Warning, 120);
                    _preview.SelectionColor = Color.White;
                }
                catch (Exception ex)
                {
                    try { Diagnostics.DiagLog.Swallowed("DangerousCommandDialog.Highlight", ex); } catch { }
                }
            }
            _preview.Select(0, 0);
        }

        private string BuildRuleSummary()
        {
            var rules = _decision.Hits
                .Select(h => h.RuleName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ruleText = rules.Count == 0 ? L10n.T("未知规则") : string.Join("、", rules);
            var category = _decision.Hits.Where(h => h.Check != null && !string.IsNullOrEmpty(h.Check.Category))
                .Select(h => h.Check.Category).FirstOrDefault() ?? "";

            var source = L10n.Tf("来源: {0}", InputSourceNames.Describe(_decision.Source));

            if (_decision.IsBatch)
            {
                return L10n.Tf("规则: {0}  |  {1}  |  共 {2} 行，{3} 行命中",
                    ruleText, source, _decision.LineCount, _decision.Hits.Count);
            }

            return L10n.Tf("规则: {0}  |  分类: {1}  |  {2}", ruleText, category, source);
        }

        private string BuildDescription()
        {
            var descriptions = _decision.Hits
                .Where(h => h.Check != null && !string.IsNullOrEmpty(h.Check.Description))
                .Select(h => h.Check.Description)
                .Distinct()
                .ToList();

            var text = string.Join("\n", descriptions);
            if (HasCriticalHit)
                text += (text.Length > 0 ? "\n" : "") + L10n.T("严重级命令不允许豁免，每次都必须确认。");
            return text;
        }

        private void OnConfirmClick(object sender, EventArgs e)
        {
            _currentConfirm++;
            _progressBar.Value = _currentConfirm;

            if (_currentConfirm >= ConfirmationCount)
            {
                // 确认完成
                IsConfirmed = true;
                SessionExemptRequested = _sessionExempt != null && _sessionExempt.Checked;
                PermanentExemptRequested = _permanentExempt != null && _permanentExempt.Visible && _permanentExempt.Checked;
                if (HasCriticalHit)
                {
                    // Critical 一票否决：即使勾选框因状态残留被置上也不认
                    SessionExemptRequested = false;
                    PermanentExemptRequested = false;
                }
                DialogResult = DialogResult.OK;
            }
            else
            {
                // 还需要继续确认
                _titleLabel.Text = L10n.Tf("⚠️ {0} - 第 {1}/{2} 次确认", HighestLevel, _currentConfirm + 1, ConfirmationCount);
                _confirmLabel.Text = GetConfirmText();
                _confirmButton.Text = GetConfirmButtonText();

                // 最后一次确认时按钮变红
                if (_currentConfirm == ConfirmationCount - 1)
                {
                    _confirmButton.Type = AntdUI.TTypeMini.Error;
                    _confirmButton.Text = L10n.T("⚠ 确认执行 ⚠");
                }
            }
        }

        private string GetConfirmText()
        {
            var remaining = ConfirmationCount - _currentConfirm;
            if (remaining == ConfirmationCount)
                return L10n.Tf("此命令有 {0} 风险，请确认 {1} 次才能执行", HighestLevel, remaining);
            return L10n.Tf("已确认 {0}/{1}，还需确认 {2} 次", _currentConfirm, ConfirmationCount, remaining);
        }

        private string GetConfirmButtonText()
        {
            var remaining = ConfirmationCount - _currentConfirm;
            if (remaining == 1) return L10n.T("⚠ 最终确认 ⚠");
            return L10n.Tf("确认 ({0}/{1})", _currentConfirm + 1, ConfirmationCount);
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
                    levelText = L10n.T("严重危险");
                    emoji = "🔴";
                    break;
                case DangerLevel.High:
                    bg = GdtermColorTable.Background;
                    accent = GdtermColorTable.Warning;
                    text = GdtermColorTable.Warning;
                    levelText = L10n.T("高风险");
                    emoji = "🟠";
                    break;
                default: // Medium
                    bg = GdtermColorTable.Background;
                    accent = GdtermColorTable.Warning;
                    text = GdtermColorTable.Warning;
                    levelText = L10n.T("中等风险");
                    emoji = "🟡";
                    break;
            }
        }
    }
}
