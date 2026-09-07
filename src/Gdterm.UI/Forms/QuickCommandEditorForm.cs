using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.UI.Services;

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
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导
        }

        private void BuildUI(QuickCommand existing, string defaultGroup)
        {
            Text = existing == null ? "添加快捷命令" : "编辑快捷命令";
            Size = DpiScale.S(this, 520, 560); // 初始基准，构造末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
            ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定像素步进在高 DPI/大字号下控件重叠）
            int clientW = DpiScale.V(this, 520);
            int y = DpiScale.V(this, 20);
            int lblX = DpiScale.V(this, 20), inputX = DpiScale.V(this, 115);
            int inputW = clientW - inputX - DpiScale.V(this, 20);
            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            int rowH = fieldH + DpiScale.V(this, 8);

            // 名称
            AddLabel("名称", lblX, y, fieldH);
            _txtName = AddInput(inputX, y, inputW, fieldH);
            y += rowH;

            // 命令（多行）
            AddLabel("命令", lblX, y, fieldH);
            _txtCommand = AddInput(inputX, y, inputW, fieldH);
            _txtCommand.Multiline = true;
            _txtCommand.Size = new Size(inputW, fieldH + DpiScale.V(this, 38));
            y += fieldH + DpiScale.V(this, 46);

            // 占位符提示
            var lblHint = new AntdUI.Label {
                Text = "占位符: {host} {user} {date} {time} {datetime} {env:VAR_NAME}",
                Location = new Point(inputX, y),
                AutoSize = true
            };
            Controls.Add(lblHint);
            y += Math.Max(DpiScale.V(this, 32), FormFontPolicy.RowStep(this));

            // 分组
            AddLabel("分组", lblX, y, fieldH);
            _cmbGroup = new AntdUI.Select {
                Location = new Point(inputX, y),
                Size = new Size(DpiScale.V(this, 200), fieldH)
            };
            foreach (var g in new[] { "网络", "磁盘", "进程", "系统", "安全", "Docker", "自定义" })
                _cmbGroup.Items.Add(g);
            _cmbGroup.Text = !string.IsNullOrEmpty(defaultGroup) ? defaultGroup : "自定义";
            Controls.Add(_cmbGroup);
            y += rowH;

            // 执行前命令
            AddLabel("前置命令", lblX, y, fieldH);
            _txtPreCommand = AddInput(inputX, y, inputW, fieldH);
            _txtPreCommand.PlaceholderText = "如: sudo -i";
            y += rowH;

            // 执行后命令
            AddLabel("后置命令", lblX, y, fieldH);
            _txtPostCommand = AddInput(inputX, y, inputW, fieldH);
            _txtPostCommand.PlaceholderText = "如: cleanup (可选)";
            y += rowH;

            // 需要 root + 排序（同一行两个控件，间距按 DPI 缩放）
            _chkRequiresRoot = new AntdUI.Checkbox {
                Text = "需要 root 权限",
                Location = new Point(inputX, y + DpiScale.V(this, 8)),
                AutoSize = true
            };
            Controls.Add(_chkRequiresRoot);

            int sortX = inputX + DpiScale.V(this, 185);
            AddLabel("排序", sortX, y, fieldH);
            _numSortOrder = new AntdUI.InputNumber {
                Location = new Point(sortX + DpiScale.V(this, 45), y),
                Size = new Size(DpiScale.V(this, 80), fieldH),
                Maximum = 999,
                Value = 0,
                Increment = 1
            };
            Controls.Add(_numSortOrder);
            y += rowH;

            // 快捷键
            AddLabel("快捷键", lblX, y, fieldH);
            _txtShortcut = AddInput(inputX, y, DpiScale.V(this, 160), fieldH);
            _txtShortcut.PlaceholderText = "如: Ctrl+Shift+1";
            y += rowH;

            // 描述
            AddLabel("描述", lblX, y, fieldH);
            _txtDescription = AddInput(inputX, y, inputW, fieldH);
            y += rowH + DpiScale.V(this, 6);

            int btnW = DpiScale.V(this, 84);
            int btnGap = DpiScale.V(this, 8);
            // 按钮：Windows 惯例主按钮最右（修复：原先确定在左侧）
            var btnCancel = new AntdUI.Button {
                Text = "取消",
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - DpiScale.V(this, 20) - btnW, y)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);

            var btnOk = new AntdUI.Button {
                Text = "确定",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - DpiScale.V(this, 20) - btnW * 2 - btnGap, y)
            };
            btnOk.Click += (s, e) => TryCloseOk();
            Controls.Add(btnOk);

            // 客户区高度随内容自适应（修复：固定 560 在大字号/DPI 下裁剪底部按钮）
            ClientSize = new Size(clientW, y + fieldH + DpiScale.V(this, 20));

            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void AddLabel(string text, int x, int y, int fieldH)
        {
            int offset = Math.Max(4, (fieldH - FontHeight) / 2);
            Controls.Add(new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + offset) });
        }

        private AntdUI.Input AddInput(int x, int y, int w, int fieldH)
        {
            var txt = new AntdUI.Input {
                Location = new Point(x, y),
                Size = new Size(w, fieldH)
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
