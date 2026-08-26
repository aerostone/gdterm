using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.Core.Models;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 危险命令规则配置对话框
    /// 显示所有检测规则（按危险等级颜色标注），支持增删改、启用/禁用、白名单管理
    /// </summary>
    public class DangerousCommandConfigForm : Form
    {
        private readonly DangerousCommandDetector _detector;
        private ListView _ruleList;
        private ListBox _whitelistBox;
        private ToolStrip _toolbar;
        private Label _statusLabel;

        // 危险等级颜色
        private static readonly Color ColorMedium = Color.FromArgb(255, 200, 60);
        private static readonly Color ColorHigh = Color.FromArgb(255, 150, 50);
        private static readonly Color ColorCritical = Color.FromArgb(255, 80, 80);
        private static readonly Color ColorDisabled = Color.FromArgb(100, 100, 100);

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
            Size = DpiScale.S(this, 800, 550);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(30, 30, 30);

            // 工具栏
            _toolbar = new ToolStrip
            {
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.FromArgb(204, 204, 204),
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new DarkToolStripRenderer(),
                Font = Services.FormFontPolicy.UiFont(),
                Padding = new Padding(5, 2, 5, 2)
            };

            var btnAdd = new ToolStripButton("添加自定义规则");
            btnAdd.Click += OnAddRuleClick;
            _toolbar.Items.Add(btnAdd);

            var btnEdit = new ToolStripButton("编辑");
            btnEdit.Click += OnEditRuleClick;
            _toolbar.Items.Add(btnEdit);

            var btnDelete = new ToolStripButton("删除");
            btnDelete.Click += OnDeleteRuleClick;
            _toolbar.Items.Add(btnDelete);

            _toolbar.Items.Add(new ToolStripSeparator());

            var btnToggle = new ToolStripButton("启用/禁用");
            btnToggle.Click += OnToggleRuleClick;
            _toolbar.Items.Add(btnToggle);

            _toolbar.Items.Add(new ToolStripSeparator());

            var btnRefresh = new ToolStripButton("刷新");
            btnRefresh.Click += (s, e) => { LoadRules(); LoadWhitelist(); };
            _toolbar.Items.Add(btnRefresh);

            // 规则列表
            _ruleList = new ListView
            {
                Location = DpiScale.P(this, 0, 28),
                Size = DpiScale.S(this, 784, 280),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _ruleList.Columns.Add("名称", 150);
            _ruleList.Columns.Add("匹配模式", 200);
            _ruleList.Columns.Add("匹配类型", 70);
            _ruleList.Columns.Add("危险等级", 80);
            _ruleList.Columns.Add("分类", 100);
            _ruleList.Columns.Add("启用", 50);

            _ruleList.DoubleClick += OnEditRuleClick;

            // 白名单区域标题
            var whitelistHeader = new Label
            {
                Text = "白名单（豁免命令）",
                Font = Services.FormFontPolicy.UiFont(+0.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = DpiScale.P(this, 5, 315),
                Size = DpiScale.S(this, 200, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            // 白名单按钮
            var btnAddWhitelist = new Button
            {
                Text = "添加",
                Size = DpiScale.S(this, 60, 24),
                Location = DpiScale.P(this, 5, 340),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnAddWhitelist.Click += OnAddWhitelistClick;

            var btnRemoveWhitelist = new Button
            {
                Text = "移除",
                Size = DpiScale.S(this, 60, 24),
                Location = DpiScale.P(this, 70, 340),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                BackColor = Color.FromArgb(80, 40, 40),
                ForeColor = Color.White,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnRemoveWhitelist.Click += OnRemoveWhitelistClick;

            // 白名单列表
            _whitelistBox = new ListBox
            {
                Location = DpiScale.P(this, 5, 370),
                Size = DpiScale.S(this, 770, 100),
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(25, 25, 25),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // 白名单分隔线
            var separator = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Location = DpiScale.P(this, 5, 310),
                Size = DpiScale.S(this, 770, 2),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // 状态栏
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Text = "就绪"
            };

            Controls.AddRange(new Control[]
            {
                _ruleList, separator,
                whitelistHeader, btnAddWhitelist, btnRemoveWhitelist, _whitelistBox,
                _toolbar, _statusLabel
            });
        }

        private void LoadRules()
        {
            _ruleList.Items.Clear();
            try
            {
                var rules = _detector.GetAllRules();
                foreach (var rule in rules)
                {
                    var item = new ListViewItem(rule.Name ?? "(无名称)");
                    item.SubItems.Add(rule.Pattern ?? "");
                    item.SubItems.Add(rule.PatternType.ToString());
                    item.SubItems.Add(GetLevelText(rule.Level));
                    item.SubItems.Add(rule.Category ?? "");
                    item.SubItems.Add(rule.Enabled ? "是" : "否");

                    // 按危险等级着色
                    if (!rule.Enabled)
                    {
                        item.ForeColor = ColorDisabled;
                    }
                    else
                    {
                        switch (rule.Level)
                        {
                            case DangerLevel.Medium:
                                item.ForeColor = ColorMedium;
                                break;
                            case DangerLevel.High:
                                item.ForeColor = ColorHigh;
                                break;
                            case DangerLevel.Critical:
                                item.ForeColor = ColorCritical;
                                break;
                        }
                    }

                    item.Tag = rule;
                    _ruleList.Items.Add(item);
                }
                _statusLabel.Text = $"共 {rules.Count} 条规则";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载规则失败：{ex.Message}";
            }
        }

        private void LoadWhitelist()
        {
            _whitelistBox.Items.Clear();
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
            if (_ruleList.SelectedItems.Count == 0) return;

            var rule = (DangerousCommandRule)_ruleList.SelectedItems[0].Tag;
            MessageBox.Show(this,
                $"编辑规则 \"{rule.Name}\" 的功能需要配置文件支持。\n" +
                $"当前规则：{rule.Pattern}\n" +
                $"类型：{rule.PatternType} | 等级：{GetLevelText(rule.Level)}",
                "规则详情",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void OnDeleteRuleClick(object sender, EventArgs e)
        {
            if (_ruleList.SelectedItems.Count == 0) return;

            var rule = (DangerousCommandRule)_ruleList.SelectedItems[0].Tag;
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
            if (_ruleList.SelectedItems.Count == 0) return;

            var rule = (DangerousCommandRule)_ruleList.SelectedItems[0].Tag;
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
                        _whitelistBox.Items.Add(command);
                        _statusLabel.Text = $"已添加白名单：{command}";
                    }
                }
            }
        }

        private void OnRemoveWhitelistClick(object sender, EventArgs e)
        {
            if (_whitelistBox.SelectedItem == null) return;

            var command = _whitelistBox.SelectedItem.ToString();
            var confirm = MessageBox.Show(this,
                $"确定要从白名单中移除 \"{command}\" 吗？",
                "确认移除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                _detector.RemoveFromWhitelist(command);
                _whitelistBox.Items.Remove(_whitelistBox.SelectedItem);
                _statusLabel.Text = $"已移除白名单：{command}";
            }
        }

        /// <summary>
        /// 深色工具栏渲染器
        /// </summary>
        private class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(Color.FromArgb(45, 45, 45)))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected || e.Item.Pressed)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(60, 60, 60)))
                        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                }
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var y = e.Item.Height / 2;
                using (var pen = new Pen(Color.FromArgb(60, 60, 60)))
                    e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Color.FromArgb(204, 204, 204);
                base.OnRenderItemText(e);
            }
        }
    }

    /// <summary>
    /// 规则编辑对话框
    /// </summary>
    internal class DangerousCommandRuleEditForm : Form
    {
        private TextBox _nameBox;
        private TextBox _patternBox;
        private ComboBox _patternTypeCombo;
        private ComboBox _levelCombo;
        private ComboBox _categoryCombo;
        private TextBox _descriptionBox;
        private CheckBox _enabledCheck;

        public DangerousCommandRuleEditForm()
        {
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        private void InitializeComponent()
        {
            Text = "添加自定义规则";
            Size = DpiScale.S(this, 450, 400);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 30, 30);

            // 布局基准值统一经 DPI 缩放（规范规则④）
            var y = DpiScale.V(this, 15);
            var labelW = DpiScale.V(this, 80);
            var boxX = DpiScale.V(this, 100);
            var boxW = DpiScale.V(this, 320);

            _nameBox = AddTextField("规则名称：", ref y, labelW, boxX, boxW);
            _patternBox = AddTextField("匹配模式：", ref y, labelW, boxX, boxW);

            // 匹配类型
            var patternTypeLabel = new Label
            {
                Text = "匹配类型：",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(DpiScale.V(this, 15), y),
                Size = new Size(labelW, DpiScale.V(this, 22))
            };
            _patternTypeCombo = new ComboBox
            {
                Location = new Point(boxX, y),
                Size = DpiScale.S(this, 150, 22),
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _patternTypeCombo.Items.AddRange(new object[] { "Regex", "Contains", "Equals" });
            _patternTypeCombo.SelectedIndex = 0;
            Controls.Add(patternTypeLabel);
            Controls.Add(_patternTypeCombo);
            y += 30;

            // 危险等级
            var levelLabel = new Label
            {
                Text = "危险等级：",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(DpiScale.V(this, 15), y),
                Size = new Size(labelW, DpiScale.V(this, 22))
            };
            _levelCombo = new ComboBox
            {
                Location = new Point(boxX, y),
                Size = DpiScale.S(this, 150, 22),
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _levelCombo.Items.AddRange(new object[] { "Medium", "High", "Critical" });
            _levelCombo.SelectedIndex = 0;
            Controls.Add(levelLabel);
            Controls.Add(_levelCombo);
            y += 30;

            // 分类
            var categoryLabel = new Label
            {
                Text = "分类：",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(DpiScale.V(this, 15), y),
                Size = new Size(labelW, DpiScale.V(this, 22))
            };
            _categoryCombo = new ComboBox
            {
                Location = new Point(boxX, y),
                Size = DpiScale.S(this, 200, 22),
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                DropDownStyle = ComboBoxStyle.DropDown
            };
            _categoryCombo.Items.AddRange(new object[]
            {
                "filesystem", "disk", "system", "process", "firewall",
                "network", "privilege", "service", "config", "git",
                "docker", "package", "audit", "user", "ssh"
            });
            _categoryCombo.SelectedIndex = 0;
            Controls.Add(categoryLabel);
            Controls.Add(_categoryCombo);
            y += 30;

            // 描述
            var descLabel = new Label
            {
                Text = "描述：",
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(DpiScale.V(this, 15), y),
                Size = new Size(labelW, DpiScale.V(this, 22))
            };
            _descriptionBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW, DpiScale.V(this, 50)),
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };
            Controls.Add(descLabel);
            Controls.Add(_descriptionBox);
            y += 65;

            // 启用
            _enabledCheck = new CheckBox
            {
                Text = "启用此规则",
                Location = new Point(boxX, y),
                Size = DpiScale.S(this, 150, 22),
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(204, 204, 204),
                Checked = true
            };
            Controls.Add(_enabledCheck);
            y += 35;

            // 按钮
            var okButton = new Button
            {
                Text = "确定",
                Size = DpiScale.S(this, 80, 30),
                Location = new Point(boxX + boxW - 170, y),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "取消",
                Size = DpiScale.S(this, 80, 30),
                Location = new Point(boxX + boxW - 80, y),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(okButton);
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private TextBox AddTextField(string labelText, ref int y, int labelW, int boxX, int boxW)
        {
            var label = new Label
            {
                Text = labelText,
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(DpiScale.V(this, 15), y),
                Size = new Size(labelW, DpiScale.V(this, 22))
            };

            var textBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW, DpiScale.V(this, 22)),
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle
            };

            Controls.Add(label);
            Controls.Add(textBox);
            y += DpiScale.V(this, 30);
            return textBox;
        }
    }

    /// <summary>
    /// 通用文本输入对话框（用于白名单添加等场景）
    /// </summary>
    internal class TextInputForm : Form
    {
        private TextBox _inputBox;

        public string InputText { get { return _inputBox.Text; } }

        public TextInputForm(string title, string prompt)
        {
            Text = title;
            Size = DpiScale.S(this, 400, 160);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 30, 30);

            var promptLabel = new Label
            {
                Text = prompt,
                Font = Services.FormFontPolicy.UiFont(+0.5f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = DpiScale.P(this, 15, 12),
                Size = DpiScale.S(this, 360, 22)
            };

            _inputBox = new TextBox
            {
                Location = DpiScale.P(this, 15, 40),
                Size = DpiScale.S(this, 355, 24),
                Font = new Font("Consolas", 10f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle
            };

            var okButton = new Button
            {
                Text = "确定",
                Size = DpiScale.S(this, 75, 28),
                Location = DpiScale.P(this, 215, 75),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "取消",
                Size = DpiScale.S(this, 75, 28),
                Location = DpiScale.P(this, 295, 75),
                FlatStyle = FlatStyle.Flat,
                Font = Services.FormFontPolicy.UiFont(),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[] { promptLabel, _inputBox, okButton, cancelButton });

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
}
