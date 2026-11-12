using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 通用单行文本输入对话框（重命名标签、给连接起别名…）。
    ///
    /// 为什么要独立一个：WinForms 没有内置 InputBox（VB 的 Interaction.InputBox 要拖
    /// Microsoft.VisualBasic，且外观/DPI/主题全不跟主程序一致）。全程序原先靠"就地改
    /// 文本"绕开输入框，代价是用户没法给标签/连接起自己的名字。
    ///
    /// 约定：确定返回 OK + Text；Esc/取消返回 Cancel（调用方据此不改任何东西）。
    /// 空字符串是**合法输入**（调用方自行决定"清空即恢复默认"），这里只做去首尾空白。
    /// </summary>
    public class TextPromptDialog : AntdUI.Window
    {
        private AntdUI.Input _input;

        /// <summary>用户输入的文本（去首尾空白）。</summary>
        public string Value { get; private set; }

        public TextPromptDialog(string title, string label, string initialValue, string hint = null)
        {
            Value = initialValue ?? "";
            InitializeComponent(title, label, Value, hint);
            UiA11y.AutoName(this);
        }

        private void InitializeComponent(string title, string label, string initial, string hint)
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false;
            AutoHandDpi = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            Size = new Size(DpiScale.V(this, 460), DpiScale.V(this, hint != null ? 210 : 180));
            Text = title ?? L10n.T("输入");

            var lbl = new AntdUI.Label
            {
                Text = label ?? "",
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Padding = DpiScale.Pad(this, 12, 12, 12, 4)
            };

            _input = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                Text = initial ?? "",
                Margin = DpiScale.Pad(this, 12, 0, 12, 0)
            };
            _input.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
            };

            var hintLabel = new AntdUI.Label
            {
                Text = hint ?? "",
                Dock = DockStyle.Top,
                AutoSize = true,
                Visible = !string.IsNullOrEmpty(hint),
                ForeColor = GdtermColorTable.Muted,
                Padding = DpiScale.Pad(this, 12, 6, 12, 0)
            };

            // 按钮一律 FlowLayoutPanel 流式（SCALING 规则 4e：页脚不用绝对坐标）
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = DpiScale.Pad(this, 12, 10, 12, 12)
            };
            var ok = new AntdUI.Button
            {
                Text = L10n.T("确定"),
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                DialogResult = DialogResult.OK
            };
            ok.Click += (s, e) => Accept();
            var cancel = new AntdUI.Button
            {
                Text = L10n.T("取消"),
                AutoSize = true,
                DialogResult = DialogResult.Cancel
            };
            footer.Controls.Add(ok);
            footer.Controls.Add(cancel);

            Controls.Add(_input);
            Controls.Add(hintLabel);
            Controls.Add(lbl);
            Controls.Add(footer);
            AcceptButton = null; // Enter 由输入框 KeyDown 接管，避免重复触发
            CancelButton = null; // Esc 同理（按钮 DialogResult 仍生效）
            Shown += (s, e) =>
            {
                try { _input.Focus(); _input.SelectAll(); } catch { }
            };
        }

        private void Accept()
        {
            Value = (_input.Text ?? "").Trim();
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>一行调用：取消返回 null，确定返回去空白后的文本（可能是空串）。</summary>
        public static string Ask(IWin32Window owner, string title, string label, string initialValue, string hint = null)
        {
            try
            {
                using (var dlg = new TextPromptDialog(title, label, initialValue, hint))
                {
                    var result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                    return result == DialogResult.OK ? dlg.Value : null;
                }
            }
            catch (Exception ex)
            {
                try { Diagnostics.DiagLog.Swallowed("TextPromptDialog.Ask", ex); } catch { }
                return null;
            }
        }
    }
}
