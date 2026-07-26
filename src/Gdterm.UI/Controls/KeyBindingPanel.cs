using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Connections;
using Gdterm.Terminal;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 快捷键管理面板——预设切换 + 绑定列表 + 编辑/添加/删除
    /// </summary>
    public class KeyBindingPanel : UserControl
    {
        private readonly TerminalKeyBindingStore _store;
        private TerminalKeyBindingConfig _config;

        private ComboBox _cmbPreset;
        private ListView _lvBindings;
        private Label _lblDescription;
        private Button _btnAdd;
        private Button _btnEdit;
        private Button _btnDelete;
        private Button _btnReset;
        private CheckBox _chkIntercept;

        /// <summary>当绑定变更时触发（通知外部刷新 TerminalControl 的 resolver）</summary>
        public event Action BindingsChanged;

        public KeyBindingPanel(TerminalKeyBindingStore store)
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
            // ── 顶部：预设选择 ──
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 45, BackColor = Color.FromArgb(37, 37, 38), Padding = new Padding(10) };

            var lblPreset = new Label
            {
                Text = "预设:",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                AutoSize = true,
                Location = new Point(10, 13)
            };

            _cmbPreset = new ComboBox
            {
                Location = new Point(55, 10),
                Size = new Size(200, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Microsoft YaHei", 9f),
                FlatStyle = FlatStyle.Flat
            };
            foreach (var preset in _config.Presets)
                _cmbPreset.Items.Add(string.Format("{0} — {1}", preset.Name, preset.Description));
            _cmbPreset.SelectedIndexChanged += OnPresetChanged;

            _lblDescription = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei", 8f),
                ForeColor = Color.FromArgb(130, 130, 130),
                AutoSize = true,
                Location = new Point(270, 13)
            };

            _chkIntercept = new CheckBox
            {
                Text = "拦截模式（匹配的按键不发送到终端）",
                Font = new Font("Microsoft YaHei", 8f),
                ForeColor = Color.FromArgb(204, 204, 204),
                AutoSize = true,
                Location = new Point(480, 12),
                Checked = _config.InterceptMode
            };
            _chkIntercept.CheckedChanged += (s, e) => { _config.InterceptMode = _chkIntercept.Checked; _store.Save(); };

            topPanel.Controls.AddRange(new Control[] { lblPreset, _cmbPreset, _lblDescription, _chkIntercept });

            // ── 底部：操作按钮 ──
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(37, 37, 38) };

            _btnAdd = CreateButton("添加", 10);
            _btnEdit = CreateButton("编辑", 90);
            _btnDelete = CreateButton("删除", 170);
            _btnReset = CreateButton("重置预设", 300);

            _btnAdd.Click += OnAdd;
            _btnEdit.Click += OnEdit;
            _btnDelete.Click += OnDelete;
            _btnReset.Click += OnReset;

            bottomPanel.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete, _btnReset });

            // ── 中间：绑定列表 ──
            _lvBindings = new ListView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 9f),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None
            };
            _lvBindings.Columns.Add("名称", 150);
            _lvBindings.Columns.Add("按键组合", 150);
            _lvBindings.Columns.Add("类型", 60);
            _lvBindings.Columns.Add("发送内容", 200);
            _lvBindings.Columns.Add("分组", 60);
            _lvBindings.Columns.Add("状态", 50);
            _lvBindings.Columns.Add("描述", 200);
            _lvBindings.DoubleClick += (s, e) => OnEdit(s, e);

            Controls.Add(_lvBindings);
            Controls.Add(bottomPanel);
            Controls.Add(topPanel);

            // 选中当前活动预设
            for (int i = 0; i < _config.Presets.Count; i++)
            {
                if (string.Equals(_config.Presets[i].Name, _config.ActivePreset, StringComparison.OrdinalIgnoreCase))
                {
                    _cmbPreset.SelectedIndex = i;
                    break;
                }
            }
        }

        private Button CreateButton(string text, int x)
        {
            return new Button
            {
                Text = text,
                Size = new Size(70, 30),
                Location = new Point(x, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Microsoft YaHei", 9f),
                Cursor = Cursors.Hand
            };
        }

        private void OnPresetChanged(object sender, EventArgs e)
        {
            int idx = _cmbPreset.SelectedIndex;
            if (idx < 0 || idx >= _config.Presets.Count) return;
            _config.ActivePreset = _config.Presets[idx].Name;
            _lblDescription.Text = _config.Presets[idx].Description;
            _store.Save();
            RefreshList();
            BindingsChanged?.Invoke();
        }

        private void RefreshList()
        {
            _lvBindings.Items.Clear();
            var bindings = _store.GetActiveBindings();

            foreach (var b in bindings)
            {
                var item = new ListViewItem(b.Name);
                item.SubItems.Add(b.GetKeyCombo());
                item.SubItems.Add(b.Type.ToString());
                item.SubItems.Add(FormatValue(b));
                item.SubItems.Add(b.Group);
                item.SubItems.Add(b.Enabled ? "✓" : "✗");
                item.SubItems.Add(b.Description ?? "");
                item.Tag = b;

                // 自定义绑定用不同颜色
                if (b.Group == "custom")
                    item.ForeColor = Color.FromArgb(78, 201, 176);

                _lvBindings.Items.Add(item);
            }
        }

        private static string FormatValue(TerminalKeyBinding b)
        {
            switch (b.Type)
            {
                case SendType.Sequence:
                    return FormatSequence(b.Value);
                case SendType.Action:
                    return string.Format("[{0}]", b.Value);
                default:
                    return b.Value ?? "";
            }
        }

        private static string FormatSequence(string seq)
        {
            if (string.IsNullOrEmpty(seq)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in seq)
            {
                if (c == '\x1b') sb.Append("ESC");
                else if (c == '\x02') sb.Append("C-b");
                else if (c == '\x01') sb.Append("C-a");
                else if (c == '\x03') sb.Append("C-c");
                else if (c < 32) sb.AppendFormat("C-{0}", (char)(c + 64));
                else if (c == '[') sb.Append("[");
                else if (c == ']') sb.Append("]");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private void OnAdd(object sender, EventArgs e)
        {
            var binding = ShowBindingEditor(null);
            if (binding != null)
            {
                _config.CustomBindings.Add(binding);
                _store.Save();
                RefreshList();
                BindingsChanged?.Invoke();
            }
        }

        private void OnEdit(object sender, EventArgs e)
        {
            if (_lvBindings.SelectedItems.Count == 0) return;
            var binding = _lvBindings.SelectedItems[0].Tag as TerminalKeyBinding;
            if (binding == null) return;

            var updated = ShowBindingEditor(binding);
            if (updated != null)
            {
                // 更新绑定
                binding.Name = updated.Name;
                binding.Ctrl = updated.Ctrl;
                binding.Alt = updated.Alt;
                binding.Shift = updated.Shift;
                binding.Key = updated.Key;
                binding.Type = updated.Type;
                binding.Value = updated.Value;
                binding.Description = updated.Description;
                _store.Save();
                RefreshList();
                BindingsChanged?.Invoke();
            }
        }

        private void OnDelete(object sender, EventArgs e)
        {
            if (_lvBindings.SelectedItems.Count == 0) return;
            var binding = _lvBindings.SelectedItems[0].Tag as TerminalKeyBinding;
            if (binding == null) return;

            if (binding.Group != "custom")
            {
                MessageBox.Show("只能删除自定义绑定", "gdterm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _config.CustomBindings.RemoveAll(b => b.GetKeyCombo() == binding.GetKeyCombo());
            _store.Save();
            RefreshList();
            BindingsChanged?.Invoke();
        }

        private void OnReset(object sender, EventArgs e)
        {
            if (MessageBox.Show("确定重置当前预设？自定义绑定将保留。", "gdterm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _store.Reload();
                _config = _store.Load();
                RefreshList();
                BindingsChanged?.Invoke();
            }
        }

        /// <summary>快捷键编辑对话框</summary>
        private TerminalKeyBinding ShowBindingEditor(TerminalKeyBinding existing)
        {
            var form = new Form
            {
                Text = existing == null ? "添加快捷键" : "编辑快捷键",
                Size = new Size(450, 380),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            int y = 15;
            var font = new Font("Microsoft YaHei", 9f);

            var lblName = new Label { Text = "名称:", Location = new Point(15, y), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var txtName = new TextBox { Location = new Point(100, y - 3), Size = new Size(310, 25), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = font };
            y += 35;

            var lblCombo = new Label { Text = "按键组合:", Location = new Point(15, y), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var chkCtrl = new CheckBox { Text = "Ctrl", Location = new Point(100, y - 2), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var chkAlt = new CheckBox { Text = "Alt", Location = new Point(160, y - 2), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var chkShift = new CheckBox { Text = "Shift", Location = new Point(210, y - 2), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var cmbKey = new ComboBox { Location = new Point(270, y - 3), Size = new Size(140, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = font, FlatStyle = FlatStyle.Flat };
            FillKeyCombo(cmbKey);
            y += 35;

            var lblType = new Label { Text = "类型:", Location = new Point(15, y), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var cmbType = new ComboBox { Location = new Point(100, y - 3), Size = new Size(140, 25), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = font, FlatStyle = FlatStyle.Flat };
            cmbType.Items.AddRange(new object[] { "Sequence (转义序列)", "Text (字面文本)", "Action (内置动作)" });
            y += 35;

            var lblValue = new Label { Text = "发送内容:", Location = new Point(15, y), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var txtValue = new TextBox { Location = new Point(100, y - 3), Size = new Size(310, 25), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 9f) };
            y += 35;

            var lblDesc = new Label { Text = "描述:", Location = new Point(15, y), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            var txtDesc = new TextBox { Location = new Point(100, y - 3), Size = new Size(310, 25), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = font };
            y += 10;

            // 提示
            var lblHint = new Label
            {
                Text = "Sequence 示例: \\x1b[1;5A (Ctrl+Up)\nText 示例: ls -la\\r\nAction: copy/paste/clear/scroll_up/scroll_down/find",
                Location = new Point(15, y),
                Size = new Size(400, 40),
                Font = new Font("Microsoft YaHei", 8f),
                ForeColor = Color.FromArgb(100, 100, 100)
            };
            y += 50;

            var btnOk = new Button
            {
                Text = "确定",
                Size = new Size(80, 30),
                Location = new Point(250, y),
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };

            var btnCancel = new Button
            {
                Text = "取消",
                Size = new Size(80, 30),
                Location = new Point(340, y),
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204)
            };

            // 填充现有值
            if (existing != null)
            {
                txtName.Text = existing.Name;
                chkCtrl.Checked = existing.Ctrl;
                chkAlt.Checked = existing.Alt;
                chkShift.Checked = existing.Shift;
                cmbType.SelectedIndex = (int)existing.Type;
                txtValue.Text = existing.Value;
                txtDesc.Text = existing.Description;
                for (int i = 0; i < cmbKey.Items.Count; i++)
                {
                    if (string.Equals(cmbKey.Items[i].ToString(), existing.Key, StringComparison.OrdinalIgnoreCase))
                    { cmbKey.SelectedIndex = i; break; }
                }
            }
            else
            {
                cmbType.SelectedIndex = 0;
            }

            form.Controls.AddRange(new Control[] { lblName, txtName, lblCombo, chkCtrl, chkAlt, chkShift, cmbKey, lblType, cmbType, lblValue, txtValue, lblDesc, txtDesc, lblHint, btnOk, btnCancel });
            form.AcceptButton = btnOk;
            form.CancelButton = btnCancel;

            if (form.ShowDialog(this) != DialogResult.OK) return null;

            if (string.IsNullOrWhiteSpace(txtName.Text) || cmbKey.SelectedItem == null)
            {
                MessageBox.Show("请填写名称和选择按键", "gdterm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return new TerminalKeyBinding
            {
                Name = txtName.Text.Trim(),
                Ctrl = chkCtrl.Checked,
                Alt = chkAlt.Checked,
                Shift = chkShift.Checked,
                Key = cmbKey.SelectedItem.ToString(),
                Type = (SendType)cmbType.SelectedIndex,
                Value = txtValue.Text ?? "",
                Description = txtDesc.Text ?? "",
                Group = "custom",
                Enabled = true
            };
        }

        private static void FillKeyCombo(ComboBox cmb)
        {
            // 字母
            for (char c = 'A'; c <= 'Z'; c++) cmb.Items.Add(c.ToString());
            // 数字
            for (char c = '0'; c <= '9'; c++) cmb.Items.Add("D" + c);
            // 功能键
            for (int i = 1; i <= 12; i++) cmb.Items.Add("F" + i);
            // 方向键
            cmb.Items.AddRange(new object[] { "Up", "Down", "Left", "Right" });
            // 特殊键
            cmb.Items.AddRange(new object[]
            {
                "Enter", "Escape", "Back", "Delete", "Insert",
                "Home", "End", "PageUp", "PageDown", "Tab", "Space",
                // 符号键
                "OemOpenBrackets",    // [
                "OemCloseBrackets",   // ]
                "OemSemicolon",       // ;
                "OemQuotes",          // '
                "Oemcomma",           // ,
                "OemPeriod",          // .
                "OemQuestion",        // /
                "Oemtilde",           // `
                "OemMinus",           // -
                "Oemplus",            // =
                "Oem5",               // \
                "Oem7",               // "
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lvBindings?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
