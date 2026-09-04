using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 快捷命令编辑对话框（AntdUI 版）——添加/编辑 QuickCommand。
    /// </summary>
    public class QuickCommandEditorForm : AntdUI.Window
    {
        private AntdUI.Input _txtName;
        private AntdUI.Input _txtCommand;
        private AntdUI.Select _cmbGroup;
        private AntdUI.Input _txtDescription;
        private AntdUI.Checkbox _chkRequiresRoot;
        private AntdUI.Input _txtPreCommand;
        private AntdUI.Input _txtPostCommand;
        private AntdUI.Input _txtShortcut;
        private AntdUI.InputNumber _numSortOrder;

        public QuickCommand Result { get; private set; }

        public QuickCommandEditorForm(QuickCommand existing = null, string defaultGroup = null)
        {
            BuildUI(existing, defaultGroup);
            if (existing != null) FillFrom(existing);
        }

        private void BuildUI(QuickCommand existing, string defaultGroup)
        {
            Text = existing == null ? "添加快捷命令" : "编辑快捷命令";
            Size = new Size(520, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int y = 20;
            int lblX = 20, inputX = 115, inputW = 365;
            int rowH = 46;

            // 名称
            AddLabel("名称", lblX, y);
            _txtName = AddInput(inputX, y, inputW, false);
            y += rowH;

            // 命令（多行）
            AddLabel("命令", lblX, y);
            _txtCommand = AddInput(inputX, y, inputW, false);
            _txtCommand.Multiline = true;
            _txtCommand.Size = new Size(inputW, 76);
            y += 84;

            // 占位符提示
            var lblHint = new AntdUI.Label
            {
                Text = "占位符: {host} {user} {date} {time} {datetime} {env:VAR_NAME}",
                Location = new Point(inputX, y),
                AutoSize = true
            };
            Controls.Add(lblHint);
            y += 32;

            // 分组
            AddLabel("分组", lblX, y);
            _cmbGroup = new AntdUI.Select
            {
                Location = new Point(inputX, y),
                Size = new Size(200, 38)
            };
            foreach (var g in new[] { "网络", "磁盘", "进程", "系统", "安全", "Docker", "自定义" })
                _cmbGroup.Items.Add(g);
            _cmbGroup.Text = !string.IsNullOrEmpty(defaultGroup) ? defaultGroup : "自定义";
            Controls.Add(_cmbGroup);
            y += rowH;

            // 执行前命令
            AddLabel("前置命令", lblX, y);
            _txtPreCommand = AddInput(inputX, y, inputW, false);
            _txtPreCommand.PlaceholderText = "如: sudo -i";
            y += rowH;

            // 执行后命令
            AddLabel("后置命令", lblX, y);
            _txtPostCommand = AddInput(inputX, y, inputW, false);
            _txtPostCommand.PlaceholderText = "如: cleanup (可选)";
            y += rowH;

            // 需要 root + 排序
            _chkRequiresRoot = new AntdUI.Checkbox
            {
                Text = "需要 root 权限",
                Location = new Point(inputX, y + 8),
                AutoSize = true
            };
            Controls.Add(_chkRequiresRoot);

            AddLabel("排序", 300, y);
            _numSortOrder = new AntdUI.InputNumber
            {
                Location = new Point(345, y),
                Size = new Size(80, 38),
                Maximum = 999,
                Value = 0,
                Increment = 1
            };
            Controls.Add(_numSortOrder);
            y += rowH;

            // 快捷键
            AddLabel("快捷键", lblX, y);
            _txtShortcut = AddInput(inputX, y, 160, false);
            _txtShortcut.PlaceholderText = "如: Ctrl+Shift+1";
            y += rowH;

            // 描述
            AddLabel("描述", lblX, y);
            _txtDescription = AddInput(inputX, y, inputW, false);
            y += rowH + 4;

            // 按钮（主按钮最右）
            var btnOk = new AntdUI.Button
            {
                Text = "确定",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(84, 38),
                Location = new Point(520 - 20 - 84 - 8 - 84, y)
            };
            btnOk.Click += (s, e) => TryCloseOk();
            Controls.Add(btnOk);

            var btnCancel = new AntdUI.Button
            {
                Text = "取消",
                Size = new Size(84, 38),
                Location = new Point(520 - 20 - 84, y)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + 10) });
        }

        private AntdUI.Input AddInput(int x, int y, int w, bool password)
        {
            var txt = new AntdUI.Input
            {
                Location = new Point(x, y),
                Size = new Size(w, 38)
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

        private void TryCloseOk()
        {
            if (string.IsNullOrWhiteSpace(_txtName.Text) || string.IsNullOrWhiteSpace(_txtCommand.Text))
            {
                AntdUI.Message.warn(this, "请填写名称和命令");
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
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { }
            base.Dispose(disposing);
        }
    }
}
