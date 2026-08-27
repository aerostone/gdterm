using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Connections;
using Gdterm.Terminal;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 登录脚本管理面板——创建/编辑/关联连接
    /// </summary>
    public class LogonScriptPanel : UserControl
    {
        private readonly LogonScriptStore _store;
        private List<LogonScript> _scripts;
        private ListView _lvScripts;
        private Button _btnAdd, _btnEdit, _btnDelete;

        public LogonScriptPanel(LogonScriptStore store)
        {
            _store = store;
            _scripts = _store.Load();
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 30, 30);
            BuildUI();
            RefreshList();
        }

        private void BuildUI()
        {
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(37, 37, 38) };
            _btnAdd = CreateBtn("添加", 8);
            _btnEdit = CreateBtn("编辑", 90);
            _btnDelete = CreateBtn("删除", 172);
            _btnAdd.Click += (s, e) => AddScript();
            _btnEdit.Click += (s, e) => EditScript();
            _btnDelete.Click += (s, e) => DeleteScript();
            toolbar.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete });

            _lvScripts = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = Services.FormFontPolicy.UiFont(),
                BorderStyle = BorderStyle.None
            };
            _lvScripts.Columns.Add("名称", 120);
            _lvScripts.Columns.Add("步骤数", 60);
            _lvScripts.Columns.Add("关联连接", 100);
            _lvScripts.Columns.Add("状态", 50);
            _lvScripts.Columns.Add("说明", 200);
            _lvScripts.DoubleClick += (s, e) => EditScript();

            Controls.Add(_lvScripts);
            Controls.Add(toolbar);
        }

        private Button CreateBtn(string text, int x)
        {
            return new Button
            {
                Text = text, Size = DpiScale.S(this, 75, 28), Location = DpiScale.P(this, x, 6),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204), Font = Services.FormFontPolicy.UiFont(-0.5f)
            };
        }

        private void RefreshList()
        {
            _lvScripts.Items.Clear();
            foreach (var s in _scripts)
            {
                var item = new ListViewItem(s.Name);
                item.SubItems.Add((s.Steps?.Count ?? 0).ToString());
                item.SubItems.Add(s.AssociatedConnectionId ?? "全部");
                item.SubItems.Add(s.Enabled ? "✓" : "✗");
                item.SubItems.Add(s.Description ?? "");
                item.Tag = s;
                _lvScripts.Items.Add(item);
            }
        }

        private void AddScript()
        {
            var script = ShowEditor(null);
            if (script != null)
            {
                _scripts.Add(script);
                _store.Save(_scripts);
                RefreshList();
            }
        }

        private void EditScript()
        {
            if (_lvScripts.SelectedItems.Count == 0) return;
            var script = _lvScripts.SelectedItems[0].Tag as LogonScript;
            if (script == null) return;
            var updated = ShowEditor(script);
            if (updated != null)
            {
                script.Name = updated.Name;
                script.Description = updated.Description;
                script.Steps = updated.Steps;
                script.Enabled = updated.Enabled;
                _store.Save(_scripts);
                RefreshList();
            }
        }

        private void DeleteScript()
        {
            if (_lvScripts.SelectedItems.Count == 0) return;
            var script = _lvScripts.SelectedItems[0].Tag as LogonScript;
            if (script != null && MessageBox.Show("删除 \"" + script.Name + "\"?", "gdterm", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _scripts.Remove(script);
                _store.Save(_scripts);
                RefreshList();
            }
        }

        private LogonScript ShowEditor(LogonScript existing)
        {
            var form = new Form
            {
                Text = existing == null ? "添加登录脚本" : "编辑登录脚本",
                Size = DpiScale.S(this, 550, 450),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false
            };

            var font = Services.FormFontPolicy.UiFont();
            int y = 12;

            var lblName = Lbl("名称:", 12, y, form); var txtName = Txt(100, y, 200, form);
            var chkEnabled = new CheckBox { Text = "启用", Location = DpiScale.P(form, 320, y + 2), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204), Checked = true }; form.Controls.Add(chkEnabled);
            y += 30;

            var lblDesc = Lbl("说明:", 12, y, form); var txtDesc = Txt(100, y, 410, form); y += 30;

            var lblSteps = Lbl("步骤:", 12, y, form); y += 3;
            var lvSteps = new ListView
            {
                Location = DpiScale.P(form, 100, y), Size = DpiScale.S(this, 410, 200),
                View = View.Details, FullRowSelect = true,
                BackColor = Color.FromArgb(37, 37, 38), ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.FixedSingle
            };
            lvSteps.Columns.Add("类型", 60);
            lvSteps.Columns.Add("内容/关键词", 180);
            lvSteps.Columns.Add("超时(ms)", 80);
            lvSteps.Columns.Add("说明", 80);
            form.Controls.Add(lvSteps);
            y += 210;

            // 步骤操作按钮
            var btnAddStep = new Button { Text = "+", Location = DpiScale.P(form, 100, y), Size = DpiScale.S(this, 28, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(78, 201, 176) };
            btnAddStep.FlatAppearance.BorderSize = 0;
            var btnDelStep = new Button { Text = "−", Location = DpiScale.P(form, 132, y), Size = DpiScale.S(this, 28, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(255, 80, 80) };
            btnDelStep.FlatAppearance.BorderSize = 0;
            var btnUp = new Button { Text = "↑", Location = DpiScale.P(form, 170, y), Size = DpiScale.S(this, 28, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204) };
            btnUp.FlatAppearance.BorderSize = 0;
            var btnDown = new Button { Text = "↓", Location = DpiScale.P(form, 202, y), Size = DpiScale.S(this, 28, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204) };
            btnDown.FlatAppearance.BorderSize = 0;
            form.Controls.AddRange(new Control[] { btnAddStep, btnDelStep, btnUp, btnDown });

            var steps = new List<LogonStep>();
            if (existing?.Steps != null) steps.AddRange(existing.Steps);
            Action refreshSteps = () =>
            {
                lvSteps.Items.Clear();
                foreach (var st in steps)
                {
                    var it = new ListViewItem(st.Type.ToString());
                    it.SubItems.Add(st.Value ?? "");
                    it.SubItems.Add(st.TimeoutMs.ToString());
                    it.SubItems.Add(st.Description ?? "");
                    it.Tag = st;
                    lvSteps.Items.Add(it);
                }
            };
            refreshSteps();

            btnAddStep.Click += (s, e) =>
            {
                var step = AddStepDialog();
                if (step != null) { steps.Add(step); refreshSteps(); }
            };
            btnDelStep.Click += (s, e) =>
            {
                if (lvSteps.SelectedItems.Count > 0) { steps.Remove(lvSteps.SelectedItems[0].Tag as LogonStep); refreshSteps(); }
            };
            y += 40;

            var btnOk = new Button { Text = "确定", Size = DpiScale.S(this, 80, 28), Location = DpiScale.P(form, 340, y), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
            var btnCancel = new Button { Text = "取消", Size = DpiScale.S(this, 80, 28), Location = DpiScale.P(form, 430, y), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.FromArgb(204, 204, 204) };
            form.Controls.AddRange(new Control[] { btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;

            if (existing != null)
            {
                txtName.Text = existing.Name;
                txtDesc.Text = existing.Description;
                chkEnabled.Checked = existing.Enabled;
            }

            if (form.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(txtName.Text)) return null;
            return new LogonScript
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = txtName.Text.Trim(), Description = txtDesc.Text.Trim(),
                Steps = steps, Enabled = chkEnabled.Checked,
                AssociatedConnectionId = existing?.AssociatedConnectionId
            };
        }

        private LogonStep AddStepDialog()
        {
            var form = new Form
            {
                Text = "添加步骤", Size = DpiScale.S(this, 350, 220),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(204, 204, 204),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
            };
            var font = Services.FormFontPolicy.UiFont();
            var cmbType = new ComboBox { Location = DpiScale.P(this, 100, 12), Size = DpiScale.S(this, 220, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), FlatStyle = FlatStyle.Flat, Font = font };
            cmbType.Items.AddRange(new object[] { "Send（发送文本）", "Wait（等待关键词）", "Delay（延时）" });
            cmbType.SelectedIndex = 0;
            Lbl("类型:", 12, 15, form); form.Controls.Add(cmbType);
            var txtValue = Txt(100, 48, 220, form); Lbl("内容:", 12, 51, form);
            var numTimeout = new NumericUpDown { Location = DpiScale.P(this, 100, 82), Size = DpiScale.S(this, 100, 25), Maximum = 60000, Value = 10000, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = font }; Lbl("超时:", 12, 85, form); form.Controls.Add(numTimeout);

            var btnOk = new Button { Text = "确定", Size = DpiScale.S(this, 70, 26), Location = DpiScale.P(this, 170, 140), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White };
            var btnCancel = new Button { Text = "取消", Size = DpiScale.S(this, 70, 26), Location = DpiScale.P(this, 250, 140), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.FromArgb(204, 204, 204) };
            form.Controls.AddRange(new Control[] { btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;

            if (form.ShowDialog(this) != DialogResult.OK) return null;
            return new LogonStep
            {
                Type = (LogonStepType)cmbType.SelectedIndex,
                Value = txtValue.Text.Trim(),
                TimeoutMs = (int)numTimeout.Value
            };
        }

        private static Label Lbl(string t, int x, int y, Form f) { var l = new Label { Text = t, Location = DpiScale.P(f, x, y + 3), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = Color.FromArgb(204, 204, 204) }; f.Controls.Add(l); return l; }
        private static TextBox Txt(int x, int y, int w, Form f) { var t = new TextBox { Location = DpiScale.P(f, x, y), Size = DpiScale.S(f, w, 24), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle }; f.Controls.Add(t); return t; }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
