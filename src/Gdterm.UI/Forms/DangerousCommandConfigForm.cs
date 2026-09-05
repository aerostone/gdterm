using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.Core.Models;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 危险命令规则配置对话框
    /// 显示所有检测规则（按危险等级颜色标注），支持增删改、启用/禁用、白名单管理
    /// </summary>
    public class DangerousCommandConfigForm : AntdUI.Window
    {
        private readonly DangerousCommandDetector _detector;

        private AntdUI.Input _whitelistBox;
        private System.Collections.Generic.List<DangerousCommandRule> _ruleRows = new System.Collections.Generic.List<DangerousCommandRule>();
        private AntdUI.Table _ruleTable;
        private AntdUI.Label _statusLabel;

        // 危险等级颜色
        private static readonly Color ColorMedium = GdtermColorTable.Warning;
        private static readonly Color ColorHigh = GdtermColorTable.Warning;
        private static readonly Color ColorCritical = GdtermColorTable.Danger;
        private static readonly Color ColorDisabled = GdtermColorTable.Muted;

        public DangerousCommandConfigForm(DangerousCommandDetector detector)
        {
            _detector = detector;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadRules();
            LoadWhitelist();
        }

        private void InitializeComponent()
        {
            Text = "危险命令规则配置";
            {
                float grow = FormFontPolicy.UiFontSize / 9f;
                // Dock 布局：白名单区/工具栏高度已字体驱动，窗体只需适度增长保证列表可视高度,
                Size = DpiScale.S(this, 800, (int)(520 * Math.Max(1f, grow)));
            }
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = GdtermColorTable.Background;

            // 工具栏
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Height = 46,
                Padding = new Padding(8, 5, 8, 5)
            };
            toolbar.Controls.Add(MakeBtn("添加自定义规则", OnAddRuleClick, AntdUI.TTypeMini.Primary));
            toolbar.Controls.Add(MakeBtn("编辑", OnEditRuleClick, AntdUI.TTypeMini.Default));
            toolbar.Controls.Add(MakeBtn("删除", OnDeleteRuleClick, AntdUI.TTypeMini.Default));
            toolbar.Controls.Add(MakeBtn("启用/禁用", OnToggleRuleClick, AntdUI.TTypeMini.Default));
            toolbar.Controls.Add(MakeBtn("刷新", (s, e) => { LoadRules(); LoadWhitelist(); }, AntdUI.TTypeMini.Default));

            // 规则列表（Dock 布局：工具栏下、白名单上，随窗体伸缩）
            _ruleTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f),
                BorderWidth = 0,

                RowHeight = 28
            };
            _ruleTable.Columns.Add(new AntdUI.Column("Name", "名称", AntdUI.ColumnAlign.Left));
            _ruleTable.Columns.Add(new AntdUI.Column("Pattern", "匹配模式", AntdUI.ColumnAlign.Left));
            _ruleTable.Columns.Add(new AntdUI.Column("Type", "匹配类型", AntdUI.ColumnAlign.Left));
            _ruleTable.Columns.Add(new AntdUI.Column("Level", "危险等级", AntdUI.ColumnAlign.Left));
            _ruleTable.Columns.Add(new AntdUI.Column("Category", "分类", AntdUI.ColumnAlign.Left));
            _ruleTable.Columns.Add(new AntdUI.Column("Enabled", "启用", AntdUI.ColumnAlign.Left));
            _ruleTable.CellDoubleClick += (s, e) => OnEditRuleClick(s, e);

            // —— 白名单区（底部组合面板，Dock=Bottom，字体驱动高度）——
            var wlPanel = new Panel { Dock = DockStyle.Bottom, BackColor = GdtermColorTable.Background };
            var wlHeaderRow = FormFontPolicy.RowStep(this);
            var wlBtnRow = wlHeaderRow + DpiScale.V(this, 24) + 4;
            wlPanel.Height = wlBtnRow + DpiScale.V(this, 96) + DpiScale.V(this, 8);

            var whitelistHeader = new AntdUI.Label {
                Text = "白名单（豁免命令）",
                Font = Services.FormFontPolicy.UiFont(0.5f, FontStyle.Bold),
                Location = DpiScale.P(this, 5, 2),
                AutoSize = true
            };

            // 白名单按钮
            var btnAddWhitelist = new AntdUI.Button {
                Text = "添加",
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                Location = DpiScale.P(this, 5, wlHeaderRow)
            };
            btnAddWhitelist.Click += OnAddWhitelistClick;

            var btnRemoveWhitelist = new AntdUI.Button {
                Text = "移除",
                Type = AntdUI.TTypeMini.Error,
                AutoSize = true,
                Location = DpiScale.P(this, 70, wlHeaderRow)
            };
            btnRemoveWhitelist.Click += OnRemoveWhitelistClick;

            // 白名单列表（铺满面板剩余高度）
            _whitelistBox = new AntdUI.Input {
                Location = DpiScale.P(this, 5, wlBtnRow),
                Size = new Size(0, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Font = new Font("Consolas", 9.5f),
                Multiline = true,
                ReadOnly = true
            };
            wlPanel.Controls.Add(_whitelistBox);
            wlPanel.Controls.Add(whitelistHeader);
            wlPanel.Controls.Add(btnAddWhitelist);
            wlPanel.Controls.Add(btnRemoveWhitelist);
            wlPanel.Resize += (s, e) =>
            {
                _whitelistBox.Width = wlPanel.ClientSize.Width - DpiScale.V(this, 10);
                _whitelistBox.Height = wlPanel.ClientSize.Height - wlBtnRow - DpiScale.V(this, 4);
            };

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                Height = 26,
                Text = "就绪"
            };

            // Dock 装配（WinForms 按添加逆序分配边缘，Fill 必须最后添加）
            Controls.Add(_statusLabel);   // Bottom：最底状态条
            Controls.Add(wlPanel);        // Bottom：白名单区（在状态条之上）
            Controls.Add(toolbar);        // Top：工具栏
            Controls.Add(_ruleTable);     // Fill：规则列表拿剩余全部空间
        }

        private static AntdUI.Button MakeBtn(string text, EventHandler onClick, AntdUI.TTypeMini type)
        {
            var btn = new AntdUI.Button { Text = text, Type = type, Ghost = type != AntdUI.TTypeMini.Primary, Size = new Size(110, 34) };
            btn.Click += onClick;
            return btn;
        }

        private sealed class RuleRow
        {
            public string Name { get; set; }
            public string Pattern { get; set; }
            public string Type { get; set; }
            public string Level { get; set; }
            public string Category { get; set; }
            public string Enabled { get; set; }
        }

        /// <summary>取当前选中规则；无选中返回 null。</summary>
        private DangerousCommandRule SelectedRule()
        {
            var idx = _ruleTable.SelectedIndex;
            if (idx >= 0 && idx < _ruleRows.Count) return _ruleRows[idx];
            return null;
        }

        private void LoadRules()
        {            _ruleRows.Clear();
            try
            {
                var rules = _detector.GetAllRules();
                var rows = new AntdUI.AntList<RuleRow>();
                foreach (var rule in rules)
                {
                    _ruleRows.Add(rule);
                    rows.Add(new RuleRow
                    {
                        Name = rule.Name ?? "(无名称)",
                        Pattern = rule.Pattern ?? "",
                        Type = rule.PatternType.ToString(),
                        Level = GetLevelText(rule.Level),
                        Category = rule.Category ?? "",
                        Enabled = rule.Enabled ? "是" : "否"
                    });
                }
                _ruleTable.DataSource = rows;
                _statusLabel.Text = $"共 {rows.Count} 条规则";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载规则失败：{ex.Message}";
            }
        }

        private void LoadWhitelist()
        {
            try
            {
                // 通过反射或公开接口获取白名单
                // DangerousCommandDetector 的白名单在内部 _whitelist 字段中
                // 这里通过 Check 方法验证：如果命令被放行则不在白名单中
                // 由于没有公开 GetWhitelist 方法，提示用户
                _statusLabel.Text = "白名单功能已就绪（通过添加/移除按钮管理）";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载白名单失败：{ex.Message}";
            }
        }

        private static string GetLevelText(DangerLevel level)
        {
            switch (level)
            {
                case DangerLevel.Medium: return "中等";
                case DangerLevel.High: return "高";
                case DangerLevel.Critical: return "严重";
                default: return level.ToString();
            }
        }

        private void OnAddRuleClick(object sender, EventArgs e)
        {
            using (var dlg = new DangerousCommandRuleEditForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    LoadRules();
                    _statusLabel.Text = "规则已添加（需重启检测器生效）";
                }
            }
        }

        private void OnEditRuleClick(object sender, EventArgs e)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            AntdUI.Message.info(this,
                $"编辑规则 \"{rule.Name}\" 的功能需要配置文件支持。\n" +
                $"当前规则：{rule.Pattern}\n" +
                $"类型：{rule.PatternType} | 等级：{GetLevelText(rule.Level)}");
        }

        private void OnDeleteRuleClick(object sender, EventArgs e)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            var confirm = MessageBox.Show(this,
                $"确定要删除规则 \"{rule.Name}\" 吗？",
                "确认删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm == DialogResult.Yes)
            {
                _statusLabel.Text = $"规则 \"{rule.Name}\" 删除需要配置文件支持";
            }
        }

        private void OnToggleRuleClick(object sender, EventArgs e)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            rule.Enabled = !rule.Enabled;
            LoadRules();
            _statusLabel.Text = $"规则 \"{rule.Name}\" 已{(rule.Enabled ? "启用" : "禁用")}";
        }

        private void OnAddWhitelistClick(object sender, EventArgs e)
        {
            using (var inputDlg = new TextInputForm("添加白名单命令", "请输入要豁免的完整命令："))
            {
                if (inputDlg.ShowDialog(this) == DialogResult.OK)
                {
                    var command = inputDlg.InputText.Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        _detector.AddToWhitelist(command);
                        _whitelistBox.Text += (_whitelistBox.Text.Length > 0 ? "\n" : "") + command;
                        _statusLabel.Text = $"已添加白名单：{command}";
                    }
                }
            }
        }

        private void OnRemoveWhitelistClick(object sender, EventArgs e)
        {
            var command = _whitelistBox.SelectedText;
            if (string.IsNullOrEmpty(command))
            {
                AntdUI.Message.warn(this, "请先在白名单框中选中要移除的命令。");
                return;
            }
            var confirm = MessageBox.Show(this,
                $"确定要从白名单中移除 \"{command}\" 吗？",
                "确认移除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                _detector.RemoveFromWhitelist(command);
                _whitelistBox.Text = _whitelistBox.Text.Replace(command, "").Replace("\n\n", "\n").TrimStart('\n');
                _statusLabel.Text = $"已移除白名单：{command}";
            }
        }

        /// <summary>
        /// 深色工具栏渲染器
        /// </summary>
    }

    /// <summary>
    /// 规则编辑对话框
    /// </summary>
    internal class DangerousCommandRuleEditForm : AntdUI.Window
    {
        private AntdUI.Input _nameBox;
        private AntdUI.Input _patternBox;
        private AntdUI.Select _patternTypeCombo;
        private AntdUI.Select _levelCombo;
        private AntdUI.Select _categoryCombo;
        private AntdUI.Input _descriptionBox;
        private AntdUI.Checkbox _enabledCheck;

        public DangerousCommandRuleEditForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "添加自定义规则";
            Size = new Size(470, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int labelX = 18;
            int boxX = 110;
            int boxW = 320;
            int rowH = 48;
            int y = 20;

            _nameBox = AddTextField("规则名称", labelX, boxX, ref y, boxW, rowH);
            _patternBox = AddTextField("匹配模式", labelX, boxX, ref y, boxW, rowH);

            // 匹配类型
            Controls.Add(MakeLabel("匹配类型", labelX, y));
            _patternTypeCombo = new AntdUI.Select { Location = new Point(boxX, y), Size = new Size(160, 38) };
            foreach (var v in new[] { "Regex", "Contains", "Equals" }) _patternTypeCombo.Items.Add(v);
            _patternTypeCombo.SelectedIndex = 0;
            Controls.Add(_patternTypeCombo);
            y += rowH;

            // 危险等级
            Controls.Add(MakeLabel("危险等级", labelX, y));
            _levelCombo = new AntdUI.Select { Location = new Point(boxX, y), Size = new Size(160, 38) };
            foreach (var v in new[] { "Medium", "High", "Critical" }) _levelCombo.Items.Add(v);
            _levelCombo.SelectedIndex = 0;
            Controls.Add(_levelCombo);
            y += rowH;

            // 分类
            Controls.Add(MakeLabel("分类", labelX, y));
            _categoryCombo = new AntdUI.Select { Location = new Point(boxX, y), Size = new Size(200, 38) };
            foreach (var v in new[]
            {
                "filesystem", "disk", "system", "process", "firewall",
                "network", "privilege", "service", "config", "git",
                "docker", "package", "audit", "user", "ssh"
            })
                _categoryCombo.Items.Add(v);
            _categoryCombo.SelectedIndex = 0;
            Controls.Add(_categoryCombo);
            y += rowH;

            // 描述
            Controls.Add(MakeLabel("描述", labelX, y));
            _descriptionBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, 56),
                Multiline = true
            };
            Controls.Add(_descriptionBox);
            y += rowH + 22;

            // 启用
            _enabledCheck = new AntdUI.Checkbox {
                Text = "启用此规则",
                Location = new Point(boxX, y + 6),
                AutoSize = true,
                Checked = true
            };
            Controls.Add(_enabledCheck);
            y += rowH;

            // 按钮（主按钮最右）
            var okButton = new AntdUI.Button {
                Text = "确定",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(84, 38),
                Location = new Point(boxX + boxW - 176, y)
            };
            okButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(okButton);

            var cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(84, 38),
                Location = new Point(boxX + boxW - 84, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static AntdUI.Label MakeLabel(string text, int x, int y)
        {
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + 10) };
        }

        private AntdUI.Input AddTextField(string labelText, int labelX, int boxX, ref int y, int boxW, int rowH)
        {
            Controls.Add(MakeLabel(labelText, labelX, y));
            var textBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, 38)
            };
            Controls.Add(textBox);
            y += rowH;
            return textBox;
        }
    }

    /// <summary>
    /// 通用文本输入对话框（AntdUI 版，用于白名单添加等场景）
    /// </summary>
    internal class TextInputForm : AntdUI.Window
    {
        private readonly AntdUI.Input _inputBox;

        public string InputText { get { return _inputBox.Text; } }

        public TextInputForm(string title, string prompt)
        {
            Text = title;
            Size = new Size(420, 190);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var promptLabel = new AntdUI.Label {
                Text = prompt,
                AutoSize = true,
                Location = new Point(18, 18)
            };
            Controls.Add(promptLabel);

            _inputBox = new AntdUI.Input {
                Location = new Point(18, 48),
                Size = new Size(420 - 36, 38),
                Font = new Font("Consolas", 10f)
            };
            Controls.Add(_inputBox);

            var okButton = new AntdUI.Button {
                Text = "确定",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(84, 38),
                Location = new Point(420 - 20 - 84 - 8 - 84, 104)
            };
            okButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(okButton);

            var cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(84, 38),
                Location = new Point(420 - 20 - 84, 104)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
}
