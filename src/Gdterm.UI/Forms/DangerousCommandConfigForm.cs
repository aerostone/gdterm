using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.Core.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

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
        private TableShell<RuleRow> _shell;
        private AntdUI.Button _btnDeleteSel;
        private AntdUI.Label _statusLabel;
        private EmptyStateView _empty;

        // 危险等级颜色
        private static readonly Color ColorMedium = GdtermColorTable.Warning;
        private static readonly Color ColorHigh = GdtermColorTable.Warning;
        private static readonly Color ColorCritical = GdtermColorTable.Danger;
        private static readonly Color ColorDisabled = GdtermColorTable.Muted;

        public DangerousCommandConfigForm(DangerousCommandDetector detector)
        {
            _detector = detector;
            Font = Gdterm.UI.Services.FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件
            try { UiA11y.AutoName(this); } catch (Exception) { }
            LoadRules();
            LoadWhitelist();
        }


        private void InitializeComponent()
        {
            Text = L10n.T("危险命令规则配置");
            {
                float grow = FormFontPolicy.UiFontSize / 9f;
                // Dock 布局：白名单区/工具栏高度已字体驱动，窗体只需适度增长保证列表可视高度,
                Size = DpiScale.S(this, 800, (int)(520 * Math.Max(1f, grow)));
            }
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            BackColor = GdtermColorTable.Background;

            // 字体驱动 + DPI 缩放：所有尺寸从 fieldH/rowH/pad 派生
            int pad = DpiScale.V(this, 8);
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)); // 表格阅读行：24 地板，不复用输入框 38
            int btnPad = DpiScale.V(this, 10);
            int btnMargin = DpiScale.V(this, 6);
            var btnPadding = new Padding(btnPad, DpiScale.V(this, 4), btnPad, DpiScale.V(this, 4));

            // 工具栏
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(pad, DpiScale.V(this, 5), pad, DpiScale.V(this, 5)),
                BackColor = GdtermColorTable.Background
            };
            toolbar.Controls.Add(MakeBtn(L10n.T("添加自定义规则"), OnAddRuleClick, AntdUI.TTypeMini.Primary, btnPadding, btnMargin));
            toolbar.Controls.Add(MakeBtn(L10n.T("编辑"), OnEditRuleClick, AntdUI.TTypeMini.Default, btnPadding, btnMargin));
            toolbar.Controls.Add(MakeBtn(L10n.T("删除"), OnDeleteRuleClick, AntdUI.TTypeMini.Default, btnPadding, btnMargin));
            // 批量：AntdUI.Table 本身单选，多选层见 TableShell.EnableMultiSelect（Ctrl/Shift+点击）
            _btnDeleteSel = MakeBtn(L10n.T("删除选中"), OnDeleteSelectedClick, AntdUI.TTypeMini.Default, btnPadding, btnMargin);
            _btnDeleteSel.Enabled = false;
            _btnDeleteSel.Name = "DangerRuleDeleteSelButton";
            toolbar.Controls.Add(_btnDeleteSel);
            toolbar.Controls.Add(MakeBtn(L10n.T("启用/禁用"), OnToggleRuleClick, AntdUI.TTypeMini.Default, btnPadding, btnMargin));
            toolbar.Controls.Add(MakeBtn(L10n.T("刷新"), (s, e) => { LoadRules(); LoadWhitelist(); }, AntdUI.TTypeMini.Default, btnPadding, btnMargin));

            // 规则列表（Dock 布局：工具栏下、白名单上，随窗体伸缩）
            _ruleTable = new AntdUI.Table
            {
                Name = "DangerRuleTable",
                Dock = DockStyle.Fill,
                Font = Services.FormFontPolicy.MonoFont(),
                BorderWidth = 0,
                RowHeight = rowH
            };
            _ruleTable.Columns.Add(new AntdUI.Column("Name", L10n.T("名称"), AntdUI.ColumnAlign.Left) { Width = "16%" });
            _ruleTable.Columns.Add(new AntdUI.Column("Pattern", L10n.T("匹配模式"), AntdUI.ColumnAlign.Left) { Width = "28%" });
            _ruleTable.Columns.Add(new AntdUI.Column("Type", L10n.T("匹配类型"), AntdUI.ColumnAlign.Left) { Width = "12%" });
            _ruleTable.Columns.Add(new AntdUI.Column("Level", L10n.T("危险等级"), AntdUI.ColumnAlign.Left) { Width = "12%" });
            _ruleTable.Columns.Add(new AntdUI.Column("Category", L10n.T("分类"), AntdUI.ColumnAlign.Left) { Width = "16%" });
            _ruleTable.Columns.Add(new AntdUI.Column("Enabled", L10n.T("启用"), AntdUI.ColumnAlign.Left) { Width = "16%" });
            _ruleTable.CellDoubleClick += (s, e) => OnEditRuleClick(s, e);

            // 表壳：过滤 + 排序 + 导出（规则几十条时靠肉眼找"rm -rf"很费劲）
            _shell = new TableShell<RuleRow>(_ruleTable,
                new[] { L10n.T("名称"), L10n.T("匹配模式"), L10n.T("匹配类型"), L10n.T("危险等级"), L10n.T("分类"), L10n.T("启用") },
                r => string.Join(" ", new[] { r.Name, r.Pattern, r.Type, r.Level, r.Category, r.Enabled }),
                (r, i) =>
                {
                    switch (i)
                    {
                        case 0: return r.Name;
                        case 1: return r.Pattern;
                        case 2: return r.Type;
                        case 3: return r.Level;
                        case 4: return r.Category;
                        default: return r.Enabled;
                    }
                });
            _shell.AddAction(L10n.T("导出"), (s2, e2) => _shell.ExportCsv(FindForm(), "dangerous-command-rules")).Name = "DangerRuleExportButton";
            // 多选 + Del 键批量删（内置规则由 RemoveRule 拒绝并计入跳过数）
            var multi = _shell.EnableMultiSelect();
            multi.Changed += () => { _btnDeleteSel.Enabled = multi.Count > 0; };
            _ruleTable.KeyDown += (s2, e2) =>
            {
                if (e2.KeyCode != Keys.Delete) return;
                if (multi.Count > 0) OnDeleteSelectedClick(s2, e2); else OnDeleteRuleClick(s2, e2);
                e2.Handled = true;
            };
            _shell.ViewChanged += list =>
            {
                _ruleRows.Clear();
                foreach (var r in list) if (r.Source != null) _ruleRows.Add(r.Source);
            };

            // —— 白名单区（底部组合面板，Dock=Bottom，字体驱动高度）——
            var wlPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                BackColor = GdtermColorTable.Background,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(DpiScale.V(this, 5), DpiScale.V(this, 2), DpiScale.V(this, 5), DpiScale.V(this, 4))
            };
            wlPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            wlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            wlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            wlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var wlHeaderRow = FormFontPolicy.RowStep(this);
            var wlBtnRow = wlHeaderRow + DpiScale.V(this, 24) + 4;
            wlPanel.Height = wlBtnRow + DpiScale.V(this, 96) + DpiScale.V(this, 8);

            // 白名单工具栏按钮统一字体驱动尺寸
            var wlBtnPadding = new Padding(btnPad, DpiScale.V(this, 3), btnPad, DpiScale.V(this, 3));

            var whitelistHeader = new AntdUI.Label {
                Text = L10n.T("白名单（豁免命令）"),
                Font = Services.FormFontPolicy.UiFont(0.5f, FontStyle.Bold),
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 4))
            };

            // 白名单按钮
            var whitelistButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 4))
            };
            var btnAddWhitelist = new AntdUI.Button { Text = L10n.T("添加"), Type = AntdUI.TTypeMini.Primary, AutoSize = true, Padding = wlBtnPadding, Margin = new Padding(0, 0, DpiScale.V(this, 6), 0) };
            btnAddWhitelist.Click += OnAddWhitelistClick;

            var btnRemoveWhitelist = new AntdUI.Button { Text = L10n.T("移除"), Type = AntdUI.TTypeMini.Error, AutoSize = true, Padding = wlBtnPadding };
            btnRemoveWhitelist.Click += OnRemoveWhitelistClick;
            whitelistButtons.Controls.Add(btnAddWhitelist);
            whitelistButtons.Controls.Add(btnRemoveWhitelist);

            // 白名单列表（铺满面板剩余高度）
            _whitelistBox = new AntdUI.Input {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Font = Services.FormFontPolicy.MonoFont(),
                Multiline = true,
                ReadOnly = true
            };
            wlPanel.Controls.Add(whitelistHeader, 0, 0);
            wlPanel.Controls.Add(whitelistButtons, 0, 1);
            wlPanel.Controls.Add(_whitelistBox, 0, 2);

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Text = L10n.T("就绪"),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = GdtermColorTable.Muted,
                Padding = new Padding(pad, DpiScale.V(this, 5), pad, DpiScale.V(this, 5))
            };

            // Dock 装配：布局引擎按 Controls 集合从最大索引向 0 迭代分配，索引越大越先分配；
            // 且 Dock=Fill 拿的是"此刻尚未被边缘兄弟消耗的剩余区"、它自己并不消耗剩余区——
            // 于是 Fill 若不是最低索引（最先 Add），它就先吃掉整个客户区，而后处理的边缘兄弟
            // 仍停在算出来的正确位置、只是与它重叠（工具栏/白名单/状态条压在表体上）。
            // 原注释"Fill 必须最后添加"已被真实 dump 几何证伪（CI 323 dangerous-cmd 三处遮挡），勿改回。
            Controls.Add(_shell);     // Fill：最先添加 → 最后参与分配，拿中间剩余区
            Controls.Add(toolbar);        // Top：工具栏
            Controls.Add(wlPanel);        // Bottom：白名单区
            Controls.Add(_statusLabel);   // Bottom：最底状态条（最后添加 → 最先分配，贴最底）

            // 审计 C1：规则表为空时此前是一片空白表体，分不清「没有规则」与「加载失败」
            _empty = EmptyStateView.Attach(this, L10n.T("还没有自定义规则"),
                L10n.T("内置规则在代码里默认生效；这里管理的是你自己追加/覆盖的规则"),
                L10n.T("添加自定义规则"), (s2, e2) => OnAddRuleClick(s2, e2));
        }

        private static AntdUI.Button MakeBtn(string text, EventHandler onClick, AntdUI.TTypeMini type, Padding padding, int margin)
        {
            var btn = new AntdUI.Button { Text = text, Type = type, Ghost = type != AntdUI.TTypeMini.Primary, AutoSize = true, Padding = padding, Margin = new Padding(0, 0, margin, 0) };
            btn.Click += onClick;
            return btn;
        }

        private sealed class RuleRow
        {
            public DangerousCommandRule Source { get; set; }
            public string Name { get; set; }
            public string Pattern { get; set; }
            public string Type { get; set; }
            public string Level { get; set; }
            public string Category { get; set; }
            public string Enabled { get; set; }
        }

        /// <summary>取当前选中规则；无选中返回 null。
        /// AntdUI Table 行号 1 开始（表头占 0），映射 _ruleRows 须减 1。</summary>
        private DangerousCommandRule SelectedRule()
        {
            var idx = _ruleTable.SelectedIndex - 1;
            if (idx >= 0 && idx < _ruleRows.Count) return _ruleRows[idx];
            return null;
        }

        private void LoadRules()
        {
            _ruleRows.Clear();
            try
            {
                var rules = _detector.GetAllRules();
                var rows = new List<RuleRow>();
                using (BusyScope.Begin(this, L10n.T("正在读取规则…"), false))
                foreach (var rule in rules)
                {
                    _ruleRows.Add(rule);
                    rows.Add(new RuleRow
                    {
                        Source = rule,
                        Name = rule.Name ?? L10n.T("(无名称)"),
                        Pattern = rule.Pattern ?? "",
                        Type = rule.PatternType.ToString(),
                        Level = GetLevelText(rule.Level),
                        Category = rule.Category ?? "",
                        Enabled = rule.Enabled ? L10n.T("是") : L10n.T("否")
                    });
                }
                _shell.SetItems(rows);
                _statusLabel.Text = L10n.Tf("共 {0} 条规则", rows.Count);
                if (_empty != null) _empty.SetEmpty(rows.Count == 0);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = L10n.Tf("加载规则失败：{0}", ex.Message);
                try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.LoadRules", ex); } catch (Exception) { }
                AntdUI.Message.error(this, L10n.T("加载规则失败：") + ex.Message);
                if (_empty != null)
                {
                    _empty.SetEmpty(true, L10n.T("规则加载失败"), ex.Message);
                    if (_empty.ActionButton != null) _empty.ActionButton.Visible = false;
                }
            }
        }

        private void LoadWhitelist()
        {
            try
            {
                // 2026-11 修：此前这里只有一句「功能已就绪」的假提示，白名单框打开永远是空的
                var list = _detector.GetWhitelist();
                _whitelistBox.Text = string.Join(Environment.NewLine, list);
                _statusLabel.Text = L10n.Tf("白名单 {0} 条", list.Count);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = L10n.Tf("加载白名单失败：{0}", ex.Message);
                try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.LoadWhitelist", ex); } catch (Exception) { }
                AntdUI.Message.error(this, L10n.T("加载白名单失败：") + ex.Message);
            }
        }

        private static string GetLevelText(DangerLevel level)
        {
            switch (level)
            {
                case DangerLevel.Medium: return L10n.T("中等");
                case DangerLevel.High: return L10n.T("高");
                case DangerLevel.Critical: return L10n.T("严重");
                default: return level.ToString();
            }
        }

        private void OnAddRuleClick(object sender, EventArgs e)
        {
            using (var dlg = new DangerousCommandRuleEditForm(_detector.GetCategories()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var rule = dlg.Result;
                if (rule == null) return;
                try
                {
                    _detector.AddRule(rule);
                    if (_detector.LastSaveError != null)
                    {
                        // 内存已生效但文件没写进去：必须说清楚，否则用户重启后规则消失还以为自己没保存
                        _statusLabel.Text = L10n.T("规则已生效，但配置文件写入失败：") + _detector.LastSaveError;
                        AntdUI.Message.error(this, L10n.T("规则已生效，但配置文件写入失败：") + _detector.LastSaveError);
                    }
                    else _statusLabel.Text = L10n.Tf("已添加规则：{0}", rule.Name);
                    LoadRules();
                }
                catch (Exception ex)
                {
                    try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.AddRule", ex); } catch (Exception) { }
                    AntdUI.Message.error(this, L10n.T("添加规则失败：") + ex.Message);
                }
            }
        }

        private void OnEditRuleClick(object sender, EventArgs e)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            using (var dlg = new DangerousCommandRuleEditForm(_detector.GetCategories(), rule))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var updated = dlg.Result;
                if (updated == null) return;
                try
                {
                    if (!_detector.UpdateRule(rule.Id, updated))
                    {
                        AntdUI.Message.warn(this, L10n.T("这条规则已不在配置文件里（可能被外部改动），已重新加载"));
                        LoadRules();
                        return;
                    }
                    bool saved = _detector.LastSaveError == null;
                    _statusLabel.Text = saved ? L10n.Tf("已保存规则：{0}", updated.Name)
                        : L10n.T("规则已生效，但配置文件写入失败：") + _detector.LastSaveError;
                    if (!saved) AntdUI.Message.error(this, L10n.T("配置文件写入失败：") + _detector.LastSaveError);
                    LoadRules();
                }
                catch (Exception ex)
                {
                    try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.EditRule", ex); } catch (Exception) { }
                    AntdUI.Message.error(this, L10n.T("保存规则失败：") + ex.Message);
                }
            }
        }

        private void OnDeleteSelectedClick(object sender, EventArgs e)
        {
            var rules = new System.Collections.Generic.List<DangerousCommandRule>();
            foreach (var r in _shell.GetSelectedRows())
                if (r.Source != null) rules.Add(r.Source);
            if (rules.Count == 0) return;
            var confirm = MessageBox.Show(this,
                L10n.Tf("删除选中的 {0} 条规则？内置默认规则不可删除，将被跳过。", rules.Count),
                L10n.T("确认删除"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            try
            {
                int deleted = 0, skipped = 0;
                foreach (var rule in rules)
                {
                    if (_detector.RemoveRule(rule.Id)) deleted++;
                    else skipped++;
                }
                LoadRules();
                string msg = L10n.Tf("已删除 {0} 条规则", deleted);
                if (skipped > 0) msg += L10n.Tf("，{0} 条内置规则不可删除已跳过", skipped);
                _statusLabel.Text = msg;
                if (_detector.LastSaveError != null)
                    AntdUI.Message.error(this, L10n.T("配置文件写入失败：") + _detector.LastSaveError);
                ToastNotifier.Success(msg, 2500);
            }
            catch (Exception ex)
            {
                try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.RemoveRules", ex); } catch (Exception) { }
                AntdUI.Message.error(this, L10n.T("删除规则失败：") + ex.Message);
            }
        }

        private void OnDeleteRuleClick(object sender, EventArgs e)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            var confirm = MessageBox.Show(this,
                L10n.Tf("确定要删除规则 \"{0}\" 吗？此操作会写入配置文件，内置默认规则不受影响。", rule.Name),
                L10n.T("确认删除"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;
            try
            {
                bool ok = _detector.RemoveRule(rule.Id);
                if (!ok)
                {
                    AntdUI.Message.warn(this, L10n.T("这条规则已不在配置文件里，已重新加载"));
                }
                else
                {
                    _statusLabel.Text = L10n.Tf("已删除规则：{0}", rule.Name);
                    if (_detector.LastSaveError != null)
                        AntdUI.Message.error(this, L10n.T("配置文件写入失败：") + _detector.LastSaveError);
                    ToastNotifier.Success(L10n.T("已删除规则：") + rule.Name, 2500);
                }
                LoadRules();
            }
            catch (Exception ex)
            {
                try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.RemoveRule", ex); } catch (Exception) { }
                AntdUI.Message.error(this, L10n.T("删除规则失败：") + ex.Message);
            }
        }

        private void OnToggleRuleClick(object sender, EventArgs e)
        {
            var rule = SelectedRule();
            if (rule == null) return;
            bool want = !rule.Enabled;
            try
            {
                if (!_detector.SetRuleEnabled(rule.Id, want))
                {
                    AntdUI.Message.warn(this, L10n.T("这条规则已不在配置文件里，已重新加载"));
                    LoadRules();
                    return;
                }
                LoadRules();
                _statusLabel.Text = L10n.Tf("规则 \"{0}\" 已{1}", rule.Name, want ? L10n.T("启用") : L10n.T("禁用"));
                if (_detector.LastSaveError != null)
                    AntdUI.Message.error(this, L10n.T("配置文件写入失败：") + _detector.LastSaveError);
            }
            catch (Exception ex)
            {
                try { Diagnostics.DiagLog.Error("DangerousCommandConfigForm.ToggleRule", ex); } catch (Exception) { }
                AntdUI.Message.error(this, L10n.T("切换规则状态失败：") + ex.Message);
            }
        }

        private void OnAddWhitelistClick(object sender, EventArgs e)
        {
            using (var inputDlg = new TextInputForm(L10n.T("添加白名单命令"), L10n.T("请输入要豁免的完整命令：")))
            {
                if (inputDlg.ShowDialog(this) == DialogResult.OK)
                {
                    var command = inputDlg.InputText.Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        _detector.AddToWhitelist(command);
                        LoadWhitelist();
                        _statusLabel.Text = L10n.Tf("已添加白名单：{0}", command);
                        if (_detector.LastSaveError != null)
                            AntdUI.Message.error(this, L10n.T("白名单已生效，但配置文件写入失败：") + _detector.LastSaveError);
                    }
                }
            }
        }

        private void OnRemoveWhitelistClick(object sender, EventArgs e)
        {
            var command = _whitelistBox.SelectedText;
            if (string.IsNullOrEmpty(command))
            {
                AntdUI.Message.warn(this, L10n.T("请先在白名单框中选中要移除的命令。"));
                return;
            }
            var confirm = MessageBox.Show(this,
                L10n.Tf("确定要从白名单中移除 \"{0}\" 吗？", command),
                L10n.T("确认移除"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                _detector.RemoveFromWhitelist(command);
                LoadWhitelist();
                _statusLabel.Text = L10n.Tf("已移除白名单：{0}", command);
                if (_detector.LastSaveError != null)
                    AntdUI.Message.error(this, L10n.T("白名单已生效，但配置文件写入失败：") + _detector.LastSaveError);
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
        private readonly DangerousCommandRule _editing;
        private readonly System.Collections.Generic.IReadOnlyList<string> _categories;

        /// <summary>编辑结果（OK 时非 null）。调用方拿它去 AddRule / UpdateRule。</summary>
        public DangerousCommandRule Result { get; private set; }

        public DangerousCommandRuleEditForm() : this(null, null) { }

        public DangerousCommandRuleEditForm(System.Collections.Generic.IReadOnlyList<string> categories)
            : this(categories, null) { }

        public DangerousCommandRuleEditForm(System.Collections.Generic.IReadOnlyList<string> categories, DangerousCommandRule edit)
        {
            _categories = categories;
            _editing = edit;
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            Prefill();
        }

        /// <summary>回填既有规则（编辑态）；新建时不填。</summary>
        private void Prefill()
        {
            if (_editing == null) return;
            Text = L10n.T("编辑规则");
            _nameBox.Text = _editing.Name ?? "";
            _patternBox.Text = _editing.Pattern ?? "";
            _patternTypeCombo.SelectedIndex = _editing.PatternType == PatternType.Contains ? 1
                                            : _editing.PatternType == PatternType.Equals ? 2 : 0;
            _levelCombo.SelectedIndex = _editing.Level == Gdterm.Core.Models.DangerLevel.High ? 1
                                      : _editing.Level == Gdterm.Core.Models.DangerLevel.Critical ? 2 : 0;
            var cat = _editing.Category;
            if (!string.IsNullOrEmpty(cat))
            {
                try { _categoryCombo.SelectedValue = cat; }
                catch (ArgumentException) { /* 分类不在候选里：留默认，不影响保存 */ }
            }
            _descriptionBox.Text = _editing.Description ?? "";
            _enabledCheck.Checked = _editing.Enabled;
        }

        /// <summary>收集并校验输入；不合法返回 null 并给出可见提示。</summary>
        private DangerousCommandRule Collect()
        {
            var name = (_nameBox.Text ?? "").Trim();
            var pattern = (_patternBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                AntdUI.Message.warn(this, L10n.T("请填写规则名称"));
                _nameBox.Focus();
                return null;
            }
            if (pattern.Length == 0)
            {
                AntdUI.Message.warn(this, L10n.T("请填写匹配模式"));
                _patternBox.Focus();
                return null;
            }
            var type = _patternTypeCombo.SelectedIndex == 1 ? PatternType.Contains
                     : _patternTypeCombo.SelectedIndex == 2 ? PatternType.Equals
                     : PatternType.Regex;
            if (type == PatternType.Regex)
            {
                try { new System.Text.RegularExpressions.Regex(pattern); }
                catch (Exception ex)
                {
                    // 非法正则会静默匹配不了任何东西（检测器里是 continue 跳过），必须当场拦住
                    AntdUI.Message.error(this, L10n.T("正则表达式无效：") + ex.Message);
                    _patternBox.Focus();
                    return null;
                }
            }
            var level = _levelCombo.SelectedIndex == 1 ? Gdterm.Core.Models.DangerLevel.High
                      : _levelCombo.SelectedIndex == 2 ? Gdterm.Core.Models.DangerLevel.Critical
                      : Gdterm.Core.Models.DangerLevel.Medium;
            return new DangerousCommandRule
            {
                Id = _editing != null ? _editing.Id : null,
                Name = name,
                Pattern = pattern,
                PatternType = type,
                Level = level,
                ConfirmCount = _editing != null ? _editing.ConfirmCount : (level == Gdterm.Core.Models.DangerLevel.Critical ? 3 : level == Gdterm.Core.Models.DangerLevel.High ? 2 : 1),
                Description = (_descriptionBox.Text ?? "").Trim(),
                Category = (_categoryCombo.SelectedValue as string) ?? "custom",
                Enabled = _enabledCheck.Checked
            };
        }

        private void InitializeComponent()
        {
            Text = L10n.T("添加自定义规则");
            Size = DpiScale.S(this, 470, 560); // 初始基准，构造末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
            ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            Font = Gdterm.UI.Services.FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定像素步进在大字号/高 DPI 下控件重叠）
            int clientW = DpiScale.V(this, 470);
            int labelX = DpiScale.V(this, 18);
            int boxX = DpiScale.V(this, 110);
            int boxW = clientW - boxX - labelX;
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = fieldH + DpiScale.V(this, 10);
            int y = DpiScale.V(this, 20);

            _nameBox = AddTextField(L10n.T("规则名称"), labelX, boxX, ref y, boxW, fieldH, rowH);
            _patternBox = AddTextField(L10n.T("匹配模式"), labelX, boxX, ref y, boxW, fieldH, rowH);

            // 匹配类型
            Controls.Add(MakeLabel(L10n.T("匹配类型"), labelX, y, fieldH));
            _patternTypeCombo = new AntdUI.Select { Location = new Point(boxX, y), Size = new Size(DpiScale.V(this, 160), fieldH) };
            foreach (var v in new[] { "Regex", "Contains", "Equals" }) _patternTypeCombo.Items.Add(v);
            _patternTypeCombo.SelectedIndex = 0;
            Controls.Add(_patternTypeCombo);
            y += rowH;

            // 危险等级
            Controls.Add(MakeLabel(L10n.T("危险等级"), labelX, y, fieldH));
            _levelCombo = new AntdUI.Select { Location = new Point(boxX, y), Size = new Size(DpiScale.V(this, 160), fieldH) };
            foreach (var v in new[] { "Medium", "High", "Critical" }) _levelCombo.Items.Add(v);
            _levelCombo.SelectedIndex = 0;
            Controls.Add(_levelCombo);
            y += rowH;

            // 分类
            Controls.Add(MakeLabel(L10n.T("分类"), labelX, y, fieldH));
            _categoryCombo = new AntdUI.Select { Location = new Point(boxX, y), Size = new Size(DpiScale.V(this, 200), fieldH) };
            var cats = new System.Collections.Generic.List<string>();
            if (_categories != null)
            {
                foreach (var c in _categories)
                    if (!string.IsNullOrEmpty(c) && !cats.Contains(c)) cats.Add(c);
            }
            if (cats.Count == 0)
                cats.AddRange(new[]
                {
                    "filesystem", "disk", "system", "process", "firewall",
                    "network", "privilege", "service", "config", "git",
                    "docker", "package", "audit", "user", "ssh"
                });
            if (!cats.Contains("custom")) cats.Add("custom");
            foreach (var v in cats) _categoryCombo.Items.Add(v);
            _categoryCombo.SelectedIndex = 0;
            Controls.Add(_categoryCombo);
            y += rowH;

            // 描述
            Controls.Add(MakeLabel(L10n.T("描述"), labelX, y, fieldH));
            int descH = fieldH + DpiScale.V(this, 18);
            _descriptionBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, descH),
                Multiline = true
            };
            Controls.Add(_descriptionBox);
            y += descH + DpiScale.V(this, 14);

            // 启用
            _enabledCheck = new AntdUI.Checkbox {
                Text = L10n.T("启用此规则"),
                Location = new Point(boxX, y + DpiScale.V(this, 6)),
                AutoSize = true,
                Checked = true
            };
            Controls.Add(_enabledCheck);
            y += rowH;

            int btnW = DpiScale.V(this, 84);
            int btnGap = DpiScale.V(this, 8);
            // 按钮（主按钮最右）
            var okButton = new AntdUI.Button {
                Text = L10n.T("保存"),
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX + boxW - btnW * 2 - btnGap, y)
            };
            okButton.Click += (s, e) =>
            {
                var rule = Collect();
                if (rule == null) return;   // 校验失败：留在对话框里改，不能默默关掉
                Result = rule;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(okButton);

            var cancelButton = new AntdUI.Button {
                Text = L10n.T("取消"),
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX + boxW - btnW, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            ClientSize = new Size(clientW, y + fieldH + DpiScale.V(this, 18));

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private AntdUI.Label MakeLabel(string text, int x, int y, int fieldH)
        {
            int offset = Math.Max(4, (fieldH - FontHeight) / 2);
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + offset) };
        }

        private AntdUI.Input AddTextField(string labelText, int labelX, int boxX, ref int y, int boxW, int fieldH, int rowH)
        {
            Controls.Add(MakeLabel(labelText, labelX, y, fieldH));
            var textBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, fieldH)
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

        public TextInputForm(string title, string prompt, string initialValue = null)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
            ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            Font = Gdterm.UI.Services.FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定坐标在大字号/高 DPI 下控件挤压）
            int clientW = DpiScale.V(this, 420);
            int pad = DpiScale.V(this, 18);
            int fieldH = FormFontPolicy.FieldHeight(this);
            int y = pad;

            var promptLabel = new AntdUI.Label {
                Text = prompt,
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(promptLabel);
            y += Math.Max(DpiScale.V(this, 30), Gdterm.UI.Services.FormFontPolicy.RowStep(this));

            _inputBox = new AntdUI.Input {
                Location = new Point(pad, y),
                Size = new Size(clientW - pad * 2, fieldH),
                Font = Services.FormFontPolicy.MonoFont(),
                Text = initialValue ?? ""
            };
            Controls.Add(_inputBox);
            y += fieldH + DpiScale.V(this, 18);

            int btnW = DpiScale.V(this, 84);
            int btnGap = DpiScale.V(this, 8);
            var cancelButton = new AntdUI.Button {
                Text = L10n.T("取消"),
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - pad - btnW, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            var okButton = new AntdUI.Button {
                Text = L10n.T("保存"),
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - pad - btnW * 2 - btnGap, y)
            };
            okButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(okButton);

            ClientSize = new Size(clientW, y + fieldH + pad);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }
    }
}
