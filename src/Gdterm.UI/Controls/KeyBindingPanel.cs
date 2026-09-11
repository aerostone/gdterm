using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Connections;
using Gdterm.Terminal;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 快捷键管理面板——预设切换 + 绑定列表 + 编辑/添加/删除
    /// </summary>
    public class KeyBindingPanel : UserControl
    {
        private readonly TerminalKeyBindingStore _store;
        private TerminalKeyBindingConfig _config;

        private AntdUI.Select _cmbPreset;
        private AntdUI.Table _table;
        private System.Collections.Generic.List<TerminalKeyBinding> _rows = new System.Collections.Generic.List<TerminalKeyBinding>();
        private AntdUI.Label _lblDescription;
        private AntdUI.Button _btnAdd;
        private AntdUI.Button _btnEdit;
        private AntdUI.Button _btnDelete;
        private AntdUI.Button _btnReset;
        private AntdUI.Checkbox _chkIntercept;

        /// <summary>当绑定变更时触发（通知外部刷新 TerminalControl 的 resolver）</summary>
        public event Action BindingsChanged;

        public KeyBindingPanel(TerminalKeyBindingStore store)
        {
            _store = store;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            _config = _store.Load();
            BuildUI();
            RefreshList();
        }

        private void BuildUI()
        {
            // ── 顶部：预设选择 ──
            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 10))
            };

            var lblPreset = new AntdUI.Label {
                Text = "预设:",
                AutoSize = true,
                Margin = new Padding(0, DpiScale.V(this, 8), DpiScale.V(this, 10), 0)
            };

            _cmbPreset = new AntdUI.Select {
                Size = DpiScale.S(this, 200, 34),
                Margin = new Padding(0, DpiScale.V(this, 2), DpiScale.V(this, 14), 0)
            };
            foreach (var preset in _config.Presets)
                _cmbPreset.Items.Add(string.Format("{0} — {1}", preset.Name, preset.Description));
            _cmbPreset.SelectedIndexChanged += OnPresetChanged;

            _lblDescription = new AntdUI.Label {
                Text = "",
                AutoSize = true,
                Margin = new Padding(0, DpiScale.V(this, 8), DpiScale.V(this, 14), 0)
            };

            _chkIntercept = new AntdUI.Checkbox {
                Text = "拦截模式（匹配的按键不发送到终端）",
                AutoSize = true,
                Margin = new Padding(0, DpiScale.V(this, 7), 0, 0),
                Checked = _config.InterceptMode
            };
            _chkIntercept.CheckedChanged += (s, e) => { _config.InterceptMode = _chkIntercept.Checked; _store.Save(); };

            topPanel.Controls.AddRange(new Control[] { lblPreset, _cmbPreset, _lblDescription, _chkIntercept });

            // ── 底部：操作按钮 ──
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 5), DpiScale.V(this, 10), DpiScale.V(this, 5))
            };

            _btnAdd = CreateButton("添加");
            _btnEdit = CreateButton("编辑");
            _btnDelete = CreateButton("删除");
            _btnReset = CreateButton("重置预设");

            _btnAdd.Click += OnAdd;
            _btnEdit.Click += OnEdit;
            _btnDelete.Click += OnDelete;
            _btnReset.Click += OnReset;

            bottomPanel.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete, _btnReset });

            // ── 中间：绑定列表 ──
            // P1-7：RowHeight 跟字（28 硬编码→ Max(28, fieldH 地板）），Consolas 字号跟随全局 UI 字号
            _table = new AntdUI.Table
            {
                Name = "KeyBindingTable",
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9f),
                BorderWidth = 0,
                RowHeight = Math.Max(DpiScale.V(this, 24), Gdterm.UI.Services.FormFontPolicy.RowStep(this)) // 表格阅读行：24 地板
            };
            _table.Columns.Add(new AntdUI.Column("Name", "名称", AntdUI.ColumnAlign.Left) { Width = "14%" });
            _table.Columns.Add(new AntdUI.Column("Combo", "按键组合", AntdUI.ColumnAlign.Left) { Width = "16%" });
            _table.Columns.Add(new AntdUI.Column("Type", "类型", AntdUI.ColumnAlign.Left) { Width = "10%" });
            _table.Columns.Add(new AntdUI.Column("Value", "发送内容", AntdUI.ColumnAlign.Left) { Width = "24%" });
            _table.Columns.Add(new AntdUI.Column("Group", "分组", AntdUI.ColumnAlign.Left) { Width = "12%" });
            _table.Columns.Add(new AntdUI.Column("State", "状态", AntdUI.ColumnAlign.Left) { Width = "8%" });
            _table.Columns.Add(new AntdUI.Column("Desc", "描述", AntdUI.ColumnAlign.Left) { Width = "16%" });
            _table.CellDoubleClick += (s, e) => OnEdit(s, e);

            Controls.Add(_table);
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

        private AntdUI.Button CreateButton(string text)
        {
            return new AntdUI.Button {
                Text = text,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 4), DpiScale.V(this, 10), DpiScale.V(this, 4)),
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0),
                Type = AntdUI.TTypeMini.Default,
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

        private sealed class BindingRow
        {
            public string Name { get; set; }
            public string Combo { get; set; }
            public string Type { get; set; }
            public string Value { get; set; }
            public string Group { get; set; }
            public string State { get; set; }
            public string Desc { get; set; }
        }

        private void RefreshList()
        {
            _rows.Clear();
            var rows = new List<BindingRow>();
            foreach (var b in _store.GetActiveBindings())
            {
                _rows.Add(b);
                rows.Add(new BindingRow
                {
                    Name = b.Name,
                    Combo = b.GetKeyCombo(),
                    Type = b.Type.ToString(),
                    Value = FormatValue(b),
                    Group = b.Group,
                    State = b.Enabled ? "✓" : "✗",
                    Desc = b.Description ?? ""
                });
            }
            _table.DataSource = rows;
        }

        private TerminalKeyBinding SelectedBinding()
        {
            // AntdUI Table 行号 1 开始（表头占 0），映射 _rows 须减 1
            var idx = _table.SelectedIndex - 1;
            return idx >= 0 && idx < _rows.Count ? _rows[idx] : null;
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
            var binding = SelectedBinding();
            if (binding == null) return;
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
            var binding = SelectedBinding();
            if (binding == null) return;
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

        /// <summary>快捷键编辑对话框（AntdUI 版）</summary>
        private TerminalKeyBinding ShowBindingEditor(TerminalKeyBinding existing)
        {
            using (var form = new AntdUI.Window())
            {
                form.Text = existing == null ? "添加快捷键" : "编辑快捷键";
                // ClientSize 在按钮行后按行高动态计算（见下方）
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                // P0-1：固定 y 步进→字体驱动行高（原先 50/52/62 在大字号下重叠）
                int fieldH = Math.Max(DpiScale.V(this, 36), Gdterm.UI.Services.FormFontPolicy.RowStep(this));
                int rowH = fieldH + DpiScale.V(this, 10);
                int lblOff = DpiScale.V(this, 8);
                int y = 20;
                var lblName = new AntdUI.Label { Text = "名称", Location = DpiScale.P(this, 15, y + lblOff), AutoSize = true };
                var txtName = new AntdUI.Input { Location = DpiScale.P(this, 100, y), Size = new Size(DpiScale.V(this, 330), fieldH) };
                y += rowH;

                var lblCombo = new AntdUI.Label { Text = "按键组合", Location = DpiScale.P(this, 15, y + lblOff), AutoSize = true };
                var chkCtrl = new AntdUI.Checkbox { Text = "Ctrl", Location = DpiScale.P(this, 100, y + lblOff), AutoSize = true };
                var chkAlt = new AntdUI.Checkbox { Text = "Alt", Location = DpiScale.P(this, 160, y + lblOff), AutoSize = true };
                var chkShift = new AntdUI.Checkbox { Text = "Shift", Location = DpiScale.P(this, 218, y + lblOff), AutoSize = true };
                var cmbKey = new AntdUI.Select { Location = DpiScale.P(this, 275, y), Size = new Size(DpiScale.V(this, 155), fieldH) };
                FillKeyCombo(cmbKey);
                y += rowH;

                var lblType = new AntdUI.Label { Text = "类型", Location = DpiScale.P(this, 15, y + lblOff), AutoSize = true };
                var cmbType = new AntdUI.Select { Location = DpiScale.P(this, 100, y), Size = new Size(DpiScale.V(this, 155), fieldH) };
                cmbType.Items.AddRange(new object[] { "Sequence (转义序列)", "Text (字面文本)", "Action (内置动作)" });
                y += rowH;

                var lblValue = new AntdUI.Label { Text = "发送内容", Location = DpiScale.P(this, 15, y + lblOff), AutoSize = true };
                var txtValue = new AntdUI.Input { Location = DpiScale.P(this, 100, y), Size = new Size(DpiScale.V(this, 330), fieldH), Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9f) };
                y += rowH;

                var lblDesc = new AntdUI.Label { Text = "描述", Location = DpiScale.P(this, 15, y + lblOff), AutoSize = true };
                var txtDesc = new AntdUI.Input { Location = DpiScale.P(this, 100, y), Size = new Size(DpiScale.V(this, 330), fieldH) };
                y += rowH + DpiScale.V(this, 2);

                var lblHint = new AntdUI.Label {
                    Text = "Sequence: \\x1b[1;5A (Ctrl+Up)\nText: ls -la\\r\nAction: copy/paste/clear/scroll_up/scroll_down/find",
                    Location = DpiScale.P(this, 15, y),
                    Size = new Size(DpiScale.V(this, 420), Gdterm.UI.Services.FormFontPolicy.RowStep(this) * 2 + DpiScale.V(this, 8)),
                    AutoSize = false
                };
                y += Gdterm.UI.Services.FormFontPolicy.RowStep(this) * 2 + DpiScale.V(this, 14);

                var btnOk = new AntdUI.Button {
                    Text = "确定",
                    Type = AntdUI.TTypeMini.Primary,
                    Size = new Size(DpiScale.V(this, 84), fieldH),
                    Location = DpiScale.P(this, 250, y)
                };
                btnOk.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(txtName.Text) || cmbKey.SelectedValue == null)
                    {
                        AntdUI.Message.warn(form, "请填写名称和选择按键");
                        return;
                    }
                    form.DialogResult = DialogResult.OK;
                    form.Close();
                };

                var btnCancel = new AntdUI.Button {
                    Text = "取消",
                    Type = AntdUI.TTypeMini.Default,
                    Size = new Size(DpiScale.V(this, 84), fieldH),
                    Location = DpiScale.P(this, 346, y)
                };
                // 客户区高度随行高自适应（原先固定 500 在大字号下裁剪）
                form.ClientSize = new Size(DpiScale.V(this, 470), y + fieldH + DpiScale.V(this, 16));
                btnCancel.Click += (s, e) => { form.DialogResult = DialogResult.Cancel; form.Close(); };

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

                if (form.ShowDialog(this) != DialogResult.OK) return null;

                return new TerminalKeyBinding
                {
                    Name = txtName.Text.Trim(),
                    Ctrl = chkCtrl.Checked,
                    Alt = chkAlt.Checked,
                    Shift = chkShift.Checked,
                    Key = cmbKey.SelectedValue.ToString(),
                    Type = (SendType)cmbType.SelectedIndex,
                    Value = txtValue.Text ?? "",
                    Description = txtDesc.Text ?? "",
                    Enabled = existing == null || existing.Enabled,
                    Group = existing != null ? existing.Group : "custom"
                };
            }
        }

        private static void FillKeyCombo(AntdUI.Select cmb)
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
                "Oemcomma",           //, "OemPeriod",          // .
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
                _table?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
