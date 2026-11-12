using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Models;
using Gdterm.Connections;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 关键词高亮规则管理面板
    /// </summary>
    public class HighlightRulePanel : UserControl
    {
        private readonly HighlightStore _store;
        private HighlightRuleConfig _config;
        private ListView _lvRules;
        private AntdUI.Button _btnAdd, _btnEdit, _btnDelete, _btnToggle;
        private EmptyStateView _empty;
        private ListShell _shell;

        public event Action RulesChanged;

        public HighlightRulePanel(HighlightStore store)
        {
            _store = store;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            _config = _store.Load();
            BuildUI();
            // F16 回收站：删除的高亮规则可经「工具 → 回收站」恢复
            TrashStore.Default.RegisterRestorer("highlightrule", entry =>
            {
                var r = entry.Payload as HighlightRule;
                if (r != null)
                {
                    _config.Rules.Add(r);
                    _store.Save(_config);
                    if (!IsDisposed) { RefreshList(); RulesChanged?.Invoke(); }
                }
            });
            RefreshList();
        }

        private void BuildUI()
        {
            // 工具栏
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 5), DpiScale.V(this, 8), DpiScale.V(this, 5))
            };
            _btnAdd = CreateBtn(L10n.T("添加"));
            _btnEdit = CreateBtn(L10n.T("编辑"));
            _btnDelete = CreateBtn(L10n.T("删除"));
            _btnToggle = CreateBtn(L10n.T("启用/禁用"));
            _btnAdd.Click += (s, e) => AddRule();
            _btnEdit.Click += (s, e) => EditRule();
            _btnDelete.Click += (s, e) => DeleteRule();
            _btnToggle.Click += (s, e) => ToggleRule();
            toolbar.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete, _btnToggle });

            // 列表
            _lvRules = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true, // F7：Ctrl/Shift 多选后「删除」批量删
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(),
                GridLines = false
            };
            _lvRules.Columns.Add(L10n.T("名称"), 120);
            _lvRules.Columns.Add(L10n.T("模式"), 200);
            _lvRules.Columns.Add(L10n.T("类型"), 60);
            _lvRules.Columns.Add(L10n.T("前景色"), 80);
            _lvRules.Columns.Add(L10n.T("背景色"), 80);
            _lvRules.Columns.Add(L10n.T("加粗"), 40);
            _lvRules.Columns.Add(L10n.T("状态"), 50);
            _lvRules.DoubleClick += (s, e) => EditRule();

            // 列表壳：过滤 + 排序 + 导出（规则多时靠肉眼找某条正则很费劲；此前只有 4 个操作钮）
            _shell = new ListShell(_lvRules, L10n.T("过滤规则名/模式/颜色…"));
            _shell.AddAction(L10n.T("导出"), (s, e) => _shell.ExportCsv(FindForm(), "highlight-rules")).Name = "HlRuleExportButton";

            Controls.Add(_shell);
            Controls.Add(toolbar);

            // 审计 C1：空态引导 —— 此前规则为空时就是一片背景色，分不清「没有规则」与「加载坏了」
            _empty = EmptyStateView.Attach(this, L10n.T("还没有高亮规则"),
                L10n.T("规则命中的行会按设定前景/背景色着色，可区分生产库报错、告警关键字等"),
                L10n.T("添加规则"), (s2, e2) => AddRule());
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private AntdUI.Button CreateBtn(string text)
        {
            var b = new AntdUI.Button
            {
                Text = text,
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.UiFont(-0.5f)
            };
            // AutoSize 不吃 DPI/字号，统一由 FormFontPolicy 度量（矩阵在 d150 抓到 46x34 触击不足）
            Services.FormFontPolicy.SizeActionButton(this, b, 10, 4);
            return b;
        }

        private void RefreshList()
        {
            var items = new List<ListViewItem>();
            foreach (var r in _config.Rules)
            {
                var item = new ListViewItem(r.Name);
                item.SubItems.Add(r.Pattern ?? "");
                item.SubItems.Add(r.IsRegex ? L10n.T("正则") : L10n.T("文本"));
                item.SubItems.Add(r.ForegroundColor ?? "");
                item.SubItems.Add(r.BackgroundColor ?? "");
                item.SubItems.Add(r.Bold ? "✓" : "");
                item.SubItems.Add(r.Enabled ? "✓" : "✗");
                item.Tag = r;
                if (!r.Enabled) item.ForeColor = GdtermColorTable.Muted; // 禁用行置灰：Border 太深看不见，Muted 才是状态文字色
                items.Add(item);
            }
            _shell.SetItems(items);
            if (_empty != null) _empty.SetEmpty(items.Count == 0);
        }

        private void AddRule()
        {
            var rule = ShowEditor(null);
            if (rule != null)
            {
                _config.Rules.Add(rule);
                _store.Save(_config);
                RefreshList();
                RulesChanged?.Invoke();
            }
        }

        private void EditRule()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rule = _lvRules.SelectedItems[0].Tag as HighlightRule;
            if (rule == null) return;
            var updated = ShowEditor(rule);
            if (updated != null)
            {
                rule.Name = updated.Name;
                rule.Pattern = updated.Pattern;
                rule.IsRegex = updated.IsRegex;
                rule.CaseSensitive = updated.CaseSensitive;
                rule.ForegroundColor = updated.ForegroundColor;
                rule.BackgroundColor = updated.BackgroundColor;
                rule.Bold = updated.Bold;
                _store.Save(_config);
                RefreshList();
                RulesChanged?.Invoke();
            }
        }

        private void DeleteRule()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rules = new List<HighlightRule>();
            foreach (ListViewItem it in _lvRules.SelectedItems)
            {
                var r = it.Tag as HighlightRule;
                if (r != null) rules.Add(r);
            }
            if (rules.Count == 0) return;

            var msg = rules.Count == 1
                ? L10n.Tf("删除规则 \"{0}\"?", rules[0].Name)
                : L10n.Tf("删除选中的 {0} 条高亮规则（含 \"{1}\" 等）？此操作不可撤销。", rules.Count, rules[0].Name);
            if (MessageBox.Show(this, msg, L10n.T("确认删除"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var r in rules) { _config.Rules.Remove(r); TrashStore.Default.Add("highlightrule", L10n.T("高亮规则"), r.Name, r); }
            _store.Save(_config);
            RefreshList();
            RulesChanged?.Invoke();
        }

        private void ToggleRule()
        {
            if (_lvRules.SelectedItems.Count == 0) return;
            var rule = _lvRules.SelectedItems[0].Tag as HighlightRule;
            if (rule != null)
            {
                rule.Enabled = !rule.Enabled;
                _store.Save(_config);
                RefreshList();
                RulesChanged?.Invoke();
            }
        }

        private HighlightRule ShowEditor(HighlightRule existing)
        {
            var form = new Form
            {
                Text = existing == null ? L10n.T("添加高亮规则") : L10n.T("编辑高亮规则"),
                StartPosition = FormStartPosition.CenterParent, // ClientSize 在按钮行后按行高动态计算（见下方）
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false
            };
            var font = Services.FormFontPolicy.UiFont();
            // P0-1：固定 y 步进→字体驱动行高（原先 32/28/40 在大字号下重叠）
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = fieldH + DpiScale.V(this, 6);
            int rowHChk = Math.Max(DpiScale.V(this, 28), fieldH);
            int y = 15;

            var lblName = Lbl(L10n.T("名称:"), 15, y); var txtName = Txt(100, y, 285, fieldH); y += rowH;
            var lblPattern = Lbl(L10n.T("匹配模式:"), 15, y); var txtPattern = Txt(100, y, 285, fieldH); y += rowH;
            var chkRegex = Chk(L10n.T("正则表达式"), 100, y); var chkCase = Chk(L10n.T("区分大小写"), 220, y); y += rowHChk;
            var lblFg = Lbl(L10n.T("前景色:"), 15, y); var txtFg = Txt(100, y, 100, fieldH); txtFg.PlaceholderText = "#FF4444";
            var lblBg = Lbl(L10n.T("背景色:"), 220, y); var txtBg = Txt(290, y, 95, fieldH); txtBg.PlaceholderText = "#330000"; y += rowH;
            var chkBold = Chk(L10n.T("加粗"), 100, y); y += rowHChk;

            // P1-7 触发器：命中后做什么
            var lblAction = Lbl(L10n.T("命中动作:"), 15, y);
            var cmbAction = new AntdUI.Select { Location = DpiScale.P(this, 100, y), Size = new Size(DpiScale.V(this, 150), fieldH) };
            cmbAction.Items.Add(L10n.T("只高亮"));
            cmbAction.Items.Add(L10n.T("发送命令"));
            cmbAction.Items.Add(L10n.T("弹通知"));
            cmbAction.Items.Add(L10n.T("切到标签"));
            cmbAction.SelectedIndex = 0;
            var lblArg = Lbl(L10n.T("动作参数:"), 15, y + rowH);
            var txtArg = Txt(100, y + rowH, 285, fieldH);
            var lblCd = Lbl(L10n.T("冷却(秒):"), 15, y + rowH * 2);
            var numCd = new AntdUI.InputNumber { Location = DpiScale.P(this, 100, y + rowH * 2), Size = new Size(DpiScale.V(this, 100), fieldH), Minimum = 0, Maximum = 600, Value = 5 };
            var hintAction = new AntdUI.Label
            {
                Text = L10n.T("命令走危险命令闸门；通知进通知中心+托盘；切标签参数是标题关键字。"),
                Location = DpiScale.P(this, 15, y + rowH * 3),
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Font = Services.FormFontPolicy.UiFont(-1f)
            };
            y += rowH * 3 + DpiScale.V(this, 30);

            if (existing != null)
            {
                txtName.Text = existing.Name;
                txtPattern.Text = existing.Pattern;
                chkRegex.Checked = existing.IsRegex;
                chkCase.Checked = existing.CaseSensitive;
                txtFg.Text = existing.ForegroundColor;
                txtBg.Text = existing.BackgroundColor;
                chkBold.Checked = existing.Bold;
                // 配置被手改成越界值时别让下拉抛异常
                cmbAction.SelectedIndex = Math.Max(0, Math.Min(3, (int)existing.Action));
                txtArg.Text = existing.ActionArgument;
                numCd.Value = Math.Max(0, Math.Min(600, existing.ActionCooldownSeconds));
            }

            int btnH = Math.Max(DpiScale.V(this, 30), fieldH);
            var btnOk = new AntdUI.Button { Text = L10n.T("保存"), Size = new Size(DpiScale.V(this, 80), btnH), Location = DpiScale.P(this, 220, y), DialogResult = DialogResult.OK, Type = AntdUI.TTypeMini.Primary, };
            var btnCancel = new AntdUI.Button { Text = L10n.T("取消"), Size = new Size(DpiScale.V(this, 80), btnH), Location = DpiScale.P(this, 310, y), DialogResult = DialogResult.Cancel, BackColor = GdtermColorTable.Surface2, ForeColor = GdtermColorTable.Foreground };

            form.Controls.AddRange(new Control[] { lblName, txtName, lblPattern, txtPattern, chkRegex, chkCase, lblFg, txtFg, lblBg, txtBg, chkBold,
                lblAction, cmbAction, lblArg, txtArg, lblCd, numCd, hintAction, btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;
            // 客户区高度随行高自适应（原先固定 350 在大字号下裁剪）
            form.ClientSize = new Size(DpiScale.V(this, 420), y + btnH + DpiScale.V(this, 16));

            if (form.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(txtName.Text)) return null;
            return new HighlightRule
            {
                Id = existing?.Id ?? ("hl-" + Guid.NewGuid().ToString("N").Substring(0, 6)),
                Name = txtName.Text.Trim(), Pattern = txtPattern.Text.Trim(),
                IsRegex = chkRegex.Checked, CaseSensitive = chkCase.Checked,
                ForegroundColor = txtFg.Text.Trim(), BackgroundColor = txtBg.Text.Trim(),
                Bold = chkBold.Checked, Enabled = true, SortOrder = existing?.SortOrder ?? 99,
                Action = (Gdterm.Core.Models.HighlightAction)Math.Max(0, Math.Min(3, cmbAction.SelectedIndex)),
                ActionArgument = string.IsNullOrWhiteSpace(txtArg.Text) ? null : txtArg.Text.Trim(),
                // 冷却夹到 0..600（InputNumber.Value 在本版 AntdUI 为 decimal，不校验会存进离谱值）
                ActionCooldownSeconds = (int)Math.Max(0, Math.Min(600, numCd.Value))
            };
        }

        private AntdUI.Label Lbl(string t, int x, int y) => new AntdUI.Label { Text = t, Location = DpiScale.P(this, x, y + 3), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = GdtermColorTable.Foreground };
        private AntdUI.Input Txt(int x, int y, int w, int fieldH) => new AntdUI.Input { Location = DpiScale.P(this, x, y), Size = new Size(DpiScale.V(this, w), fieldH), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.MonoFont()};
        private AntdUI.Checkbox Chk(string t, int x, int y) { var c = new AntdUI.Checkbox { Text = t, Location = DpiScale.P(this, x, y), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = GdtermColorTable.Foreground }; Controls.Add(c); return c; }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
