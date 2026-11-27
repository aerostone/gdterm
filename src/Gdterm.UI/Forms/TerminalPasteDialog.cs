using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.Core;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Forms
{
    public sealed class TerminalPasteDialog : AntdUI.Window
    {
        private readonly AntdUI.Input _text;
        private readonly AntdUI.Checkbox _expandTabs;
        private readonly AntdUI.Checkbox _sendLines;
        private readonly AntdUI.Select _newLine;
        private readonly AntdUI.InputNumber _interval;

        public string PreparedText { get; private set; }
        public bool SendLineByLine { get { return _sendLines.Checked; } }
        public int SendIntervalMs { get { return (int)_interval.Value; } }

        public TerminalPasteDialog(string text)
        {
            Text = L10n.T("粘贴预览");
            FormBorderStyle = FormBorderStyle.Sizable;
            AutoHandDpi = false;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = DpiScale.S(this, 560, 360);
            Size = DpiScale.S(this, 720, 500);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont();
            ShowInTaskbar = false;

            _text = new AntdUI.Input
            {
                Multiline = true,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = FormFontPolicy.MonoFont(),
                Text = text ?? string.Empty
            };
            _text.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode != Keys.Tab || e.Control || e.Alt) return;
                int start = _text.SelectionStart;
                int length = _text.SelectionLength;
                _text.Text = _text.Text.Remove(start, length).Insert(start, "\t");
                _text.SelectionStart = start + 1;
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            _expandTabs = new AntdUI.Checkbox { Text = L10n.T("将 Tab 展开为 4 个空格"), AutoSize = true };
            _sendLines = new AntdUI.Checkbox { Text = L10n.T("逐行发送并附加回车"), AutoSize = true };
            _newLine = new AntdUI.Select { Height = FormFontPolicy.FieldHeight(this), Width = DpiScale.V(this, 130) };
            _newLine.Items.AddRange(new object[] { L10n.T("保留换行"), "CR", "LF", "CRLF" });
            _newLine.SelectedIndex = 0;
            _interval = new AntdUI.InputNumber { Height = FormFontPolicy.FieldHeight(this), Minimum = 0, Maximum = 2000, Increment = 25, Value = 100, Width = DpiScale.V(this, 80) };
            _interval.Enabled = false;
            _sendLines.CheckedChanged += (s, e) => _interval.Enabled = _sendLines.Checked;

            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = DpiScale.Pad(this, 8, 8, 8, 8) };
            options.Controls.Add(_expandTabs);
            options.Controls.Add(CreateOptionLabel(L10n.T("换行:"), 50, 12));
            options.Controls.Add(_newLine);
            options.Controls.Add(_sendLines);
            options.Controls.Add(CreateOptionLabel(L10n.T("间隔(ms):"), 80, 12));
            options.Controls.Add(_interval);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = DpiScale.Pad(this, 8, 8, 8, 8), AutoSize = true };
            var send = new AntdUI.Button { Text = L10n.T("发送"), Type = AntdUI.TTypeMini.Primary, DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new AntdUI.Button { Text = L10n.T("取消"), Type = AntdUI.TTypeMini.Default, DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(send);
            buttons.Controls.Add(cancel);
            Gdterm.UI.Services.FormFontPolicy.StyleDialogActions(this, buttons);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(_text, 0, 0);
            layout.Controls.Add(options, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout);
            CancelButton = cancel;
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK)
                    PreparedText = TerminalPasteContent.Prepare(_text.Text, _expandTabs.Checked, (PasteNewLineMode)_newLine.SelectedIndex);
            };
            UiA11y.AutoName(this);
        }

        private AntdUI.Label CreateOptionLabel(string text, int width, int leftMargin)
        {
            return FormFontPolicy.FitSingleLineLabel(new AntdUI.Label
            {
                Text = text,
                Width = DpiScale.V(this, width),
                Height = FormFontPolicy.RowStep(this),
                Margin = DpiScale.Pad(this, leftMargin, 5, 2, 0)
            });
        }
    }
}
