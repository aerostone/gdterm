using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Models;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 快捷命令编辑对话框——添加/编辑 QuickCommand
    /// </summary>
    public class QuickCommandEditorForm : Form
    {
        private TextBox _txtName;
        private TextBox _txtCommand;
        private ComboBox _cmbGroup;
        private TextBox _txtDescription;
        private CheckBox _chkRequiresRoot;
        private TextBox _txtPreCommand;
        private TextBox _txtPostCommand;
        private TextBox _txtShortcut;
        private NumericUpDown _numSortOrder;

        public QuickCommand Result { get; private set; }

        public QuickCommandEditorForm(QuickCommand existing = null, string defaultGroup = null)
        {
            // 高/低 DPI 自适应
            BuildUI(existing, defaultGroup);
            if (existing != null) FillFrom(existing);
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        private void BuildUI(QuickCommand existing, string defaultGroup)
        {
            Text = existing == null ? "添加快捷命令" : "编辑快捷命令";
            Size = DpiScale.S(500, 480);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.FromArgb(204, 204, 204);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var font = Services.FormFontPolicy.UiFont();
            var smallFont = new Font("Consolas", 8.5f);
            int y = 15;
            int lblX = 15, inputX = 110, inputW = 350;

            // 名称
            AddLabel("名称:", lblX, y, font);
            _txtName = AddTextBox(inputX, y, inputW, font);
            _txtName.Text = "";
            y += 35;

            // 命令
            AddLabel("命令:", lblX, y, font);
            _txtCommand = AddTextBox(inputX, y, inputW, smallFont);
            _txtCommand.Height = 50;
            _txtCommand.Multiline = true;
            _txtCommand.ScrollBars = ScrollBars.Vertical;
            y += 58;

            // 占位符提示
            var lblHint = new Label
            {
                Text = "占位符: {host} {user} {date} {time} {datetime} {env:VAR_NAME}",
                Location = new Point(inputX, y),
                Size = new Size(inputW, 16),
                Font = Services.FormFontPolicy.UiFont(-1.5f),
                ForeColor = Color.FromArgb(100, 100, 100)
            };
            Controls.Add(lblHint);
            y += 22;

            // 分组
            AddLabel("分组:", lblX, y, font);
            _cmbGroup = new ComboBox
            {
                Location = new Point(inputX, y - 3),
                Size = DpiScale.S(200, 25),
                Font = font,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            // 内置分组
            _cmbGroup.Items.AddRange(new object[] { "网络", "磁盘", "进程", "系统", "安全", "Docker", "自定义" });
            if (!string.IsNullOrEmpty(defaultGroup)) _cmbGroup.Text = defaultGroup;
            else _cmbGroup.Text = "自定义";
            Controls.Add(_cmbGroup);
            y += 35;

            // 执行前命令
            AddLabel("前置命令:", lblX, y, font);
            _txtPreCommand = AddTextBox(inputX, y, inputW, smallFont);
            WinFormsCompat.SetCueBanner(_txtPreCommand, "如: sudo -i");
            y += 35;

            // 执行后命令
            AddLabel("后置命令:", lblX, y, font);
            _txtPostCommand = AddTextBox(inputX, y, inputW, smallFont);
            WinFormsCompat.SetCueBanner(_txtPostCommand, "如: cleanup (可选)");
            y += 35;

            // 需要 root + 排序
            _chkRequiresRoot = new CheckBox
            {
                Text = "需要 root 权限",
                Location = new Point(inputX, y),
                AutoSize = true,
                Font = font,
                ForeColor = Color.FromArgb(204, 204, 204)
            };
            Controls.Add(_chkRequiresRoot);

            AddLabel("排序:", 300, y, font);
            _numSortOrder = new NumericUpDown
            {
                Location = new Point(345, y - 3),
                Size = DpiScale.S(60, 25),
                Font = font,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                Maximum = 999
            };
            Controls.Add(_numSortOrder);
            y += 35;

            // 快捷键
            AddLabel("快捷键:", lblX, y, font);
            _txtShortcut = AddTextBox(inputX, y, 150, font);
            WinFormsCompat.SetCueBanner(_txtShortcut, "如: Ctrl+Shift+1");
            y += 35;

            // 描述
            AddLabel("描述:", lblX, y, font);
            _txtDescription = AddTextBox(inputX, y, inputW, font);
            y += 50;

            // 按钮
            var btnOk = new Button
            {
                Text = "确定",
                Size = DpiScale.S(80, 30),
                Location = new Point(290, y),
                DialogResult = DialogResult.OK,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Font = font
            };
            btnOk.FlatAppearance.BorderSize = 0;

            var btnCancel = new Button
            {
                Text = "取消",
                Size = DpiScale.S(80, 30),
                Location = new Point(380, y),
                DialogResult = DialogResult.Cancel,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = font
            };
            btnCancel.FlatAppearance.BorderSize = 0;

            Controls.AddRange(new Control[] { btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private Label AddLabel(string text, int x, int y, Font font)
        {
            var lbl = new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = font,
                ForeColor = Color.FromArgb(204, 204, 204)
            };
            Controls.Add(lbl);
            return lbl;
        }

        private TextBox AddTextBox(int x, int y, int w, Font font)
        {
            var txt = new TextBox
            {
                Location = new Point(x, y - 3),
                Size = new Size(w, 25),
                Font = font,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(txt);
            return txt;
        }

        private void FillFrom(QuickCommand cmd)
        {
            _txtName.Text = cmd.Name ?? "";
            _txtCommand.Text = cmd.Command ?? "";
            _cmbGroup.Text = cmd.Group ?? "自定义";
            _txtDescription.Text = cmd.Description ?? "";
            _chkRequiresRoot.Checked = cmd.RequiresRoot;
            _txtPreCommand.Text = cmd.PreCommand ?? "";
            _txtPostCommand.Text = cmd.PostCommand ?? "";
            _txtShortcut.Text = cmd.Shortcut ?? "";
            _numSortOrder.Value = cmd.SortOrder;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                if (string.IsNullOrWhiteSpace(_txtName.Text) || string.IsNullOrWhiteSpace(_txtCommand.Text))
                {
                    MessageBox.Show("请填写名称和命令", "gdterm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }

                Result = new QuickCommand
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = _txtName.Text.Trim(),
                    Command = _txtCommand.Text.Trim(),
                    Group = string.IsNullOrWhiteSpace(_cmbGroup.Text) ? "自定义" : _cmbGroup.Text.Trim(),
                    Description = _txtDescription.Text.Trim(),
                    RequiresRoot = _chkRequiresRoot.Checked,
                    PreCommand = string.IsNullOrWhiteSpace(_txtPreCommand.Text) ? null : _txtPreCommand.Text.Trim(),
                    PostCommand = string.IsNullOrWhiteSpace(_txtPostCommand.Text) ? null : _txtPostCommand.Text.Trim(),
                    Shortcut = string.IsNullOrWhiteSpace(_txtShortcut.Text) ? null : _txtShortcut.Text.Trim(),
                    SortOrder = (int)_numSortOrder.Value
                };
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { }
            base.Dispose(disposing);
        }
    }
}
