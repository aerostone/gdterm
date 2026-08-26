using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Models;
using Gdterm.Connections;
using Gdterm.UI.Services;

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
        private Button _btnAdd, _btnEdit, _btnDelete, _btnToggle;

        public event Action RulesChanged;

        public HighlightRulePanel(HighlightStore store)
        {
            _store = store;
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 30, 30);
            _config = _store.Load();
            BuildUI();
            RefreshList();
        }

        private void BuildUI()
        {
            // 工具栏
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(37, 37, 38), Padding = new Padding(8, 5, 8, 5) };
            _btnAdd = CreateBtn("添加", 0);
            _btnEdit = CreateBtn("编辑", 75);
            _btnDelete = CreateBtn("删除", 150);
            _btnToggle = CreateBtn("启用/禁用", 240);
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
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = Services.FormFontPolicy.UiFont(),
                BorderStyle = BorderStyle.None,
                GridLines = false
            };
            _lvRules.Columns.Add("名称", 120);
            _lvRules.Columns.Add("模式", 200);
            _lvRules.Columns.Add("类型", 60);
            _lvRules.Columns.Add("前景色", 80);
            _lvRules.Columns.Add("背景色", 80);
            _lvRules.Columns.Add("加粗", 40);
            _lvRules.Columns.Add("状态", 50);
            _lvRules.DoubleClick += (s, e) => EditRule();

            Controls.Add(_lvRules);
            Controls.Add(toolbar);
        }

        private Button CreateBtn(string text, int x)
        {
            return new Button
            {
                Text = text, Size = DpiScale.S(this, 70, 28), Location = new Point(x, 6),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204), Font = Services.FormFontPolicy.UiFont(-0.5f)
            };
        }

        private void RefreshList()
        {
            _lvRules.Items.Clear();
            foreach (var r in _config.Rules)
            {
                var item = new ListViewItem(r.Name);
                item.SubItems.Add(r.Pattern ?? "");
                item.SubItems.Add(r.IsRegex ? "正则" : "文本");
                item.SubItems.Add(r.ForegroundColor ?? "");
                item.SubItems.Add(r.BackgroundColor ?? "");
                item.SubItems.Add(r.Bold ? "✓" : "");
                item.SubItems.Add(r.Enabled ? "✓" : "✗");
                item.Tag = r;
                if (!r.Enabled) item.ForeColor = Color.FromArgb(80, 80, 80);
                _lvRules.Items.Add(item);
            }
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
            var rule = _lvRules.SelectedItems[0].Tag as HighlightRule;
            if (rule != null && MessageBox.Show("删除规则 \"" + rule.Name + "\"?", "gdterm", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _config.Rules.Remove(rule);
                _store.Save(_config);
                RefreshList();
                RulesChanged?.Invoke();
            }
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
                Text = existing == null ? "添加高亮规则" : "编辑高亮规则",
                Size = DpiScale.S(this, 420, 350),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false
            };
            var font = Services.FormFontPolicy.UiFont();
            int y = 15;

            var lblName = Lbl("名称:", 15, y); var txtName = Txt(100, y, 285); y += 32;
            var lblPattern = Lbl("匹配模式:", 15, y); var txtPattern = Txt(100, y, 285); y += 32;
            var chkRegex = Chk("正则表达式", 100, y); var chkCase = Chk("区分大小写", 220, y); y += 28;
            var lblFg = Lbl("前景色:", 15, y); var txtFg = Txt(100, y, 100); WinFormsCompat.SetCueBanner(txtFg, "#FF4444");
            var lblBg = Lbl("背景色:", 220, y); var txtBg = Txt(290, y, 95); WinFormsCompat.SetCueBanner(txtBg, "#330000"); y += 32;
            var chkBold = Chk("加粗", 100, y); y += 40;

            if (existing != null)
            {
                txtName.Text = existing.Name;
                txtPattern.Text = existing.Pattern;
                chkRegex.Checked = existing.IsRegex;
                chkCase.Checked = existing.CaseSensitive;
                txtFg.Text = existing.ForegroundColor;
                txtBg.Text = existing.BackgroundColor;
                chkBold.Checked = existing.Bold;
            }

            var btnOk = new Button { Text = "确定", Size = DpiScale.S(this, 80, 28), Location = new Point(220, y), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
            var btnCancel = new Button { Text = "取消", Size = DpiScale.S(this, 80, 28), Location = new Point(310, y), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.FromArgb(204, 204, 204) };

            form.Controls.AddRange(new Control[] { lblName, txtName, lblPattern, txtPattern, chkRegex, chkCase, lblFg, txtFg, lblBg, txtBg, chkBold, btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;

            if (form.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(txtName.Text)) return null;
            return new HighlightRule
            {
                Id = existing?.Id ?? ("hl-" + Guid.NewGuid().ToString("N").Substring(0, 6)),
                Name = txtName.Text.Trim(), Pattern = txtPattern.Text.Trim(),
                IsRegex = chkRegex.Checked, CaseSensitive = chkCase.Checked,
                ForegroundColor = txtFg.Text.Trim(), BackgroundColor = txtBg.Text.Trim(),
                Bold = chkBold.Checked, Enabled = true, SortOrder = existing?.SortOrder ?? 99
            };
        }

        private Label Lbl(string t, int x, int y) => new Label { Text = t, Location = new Point(x, y + 3), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = Color.FromArgb(204, 204, 204) };
        private TextBox Txt(int x, int y, int w) => new TextBox { Location = new Point(x, y), Size = new Size(w, DpiScale.V(this, 24)), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle };
        private CheckBox Chk(string t, int x, int y) { var c = new CheckBox { Text = t, Location = new Point(x, y), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = Color.FromArgb(204, 204, 204) }; Controls.Add(c); return c; }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
