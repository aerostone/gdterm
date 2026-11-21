using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    public sealed class TerminalPasteProgressDialog : AntdUI.Window
    {
        private readonly AntdUI.Label _status;
        private readonly AntdUI.Button _cancel;
        private readonly Action _cancelSending;
        private bool _completed;

        public TerminalPasteProgressDialog(int total, Action cancelSending)
        {
            _cancelSending = cancelSending;
            Text = L10n.T("正在粘贴到终端");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false;
            AutoHandDpi = false;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 420, 138);

            _status = new AntdUI.Label
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 70),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 18), DpiScale.V(this, 12), DpiScale.V(this, 18), DpiScale.V(this, 8)),
                Text = L10n.Tf("正在发送第 {0}/{1} 行", 0, total)
            };
            _cancel = new AntdUI.Button { Text = L10n.T("取消发送"), Type = AntdUI.TTypeMini.Default };
            FormFontPolicy.SizeActionButton(this, _cancel, 16, 5);
            _cancel.Click += (s, e) =>
            {
                if (_completed) Close();
                else RequestCancel();
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 4), DpiScale.V(this, 16), DpiScale.V(this, 12))
            };
            buttons.Controls.Add(_cancel);
            Controls.Add(buttons);
            Controls.Add(_status);
            CancelButton = _cancel;
            FormClosing += (s, e) =>
            {
                if (!_completed)
                {
                    e.Cancel = true;
                    RequestCancel();
                }
            };
            UiA11y.AutoName(this);
        }

        public void UpdateProgress(int current, int total)
        {
            if (IsDisposed || !IsHandleCreated) return;
            _status.Text = L10n.Tf("正在发送第 {0}/{1} 行", current, total);
        }

        public void ShowResult(int sent, int total, bool completed)
        {
            _completed = true;
            if (IsDisposed) return;
            _status.Text = completed
                ? L10n.Tf("发送完成：共发送 {0} 行", sent)
                : L10n.Tf("发送已停止：已发送 {0}/{1} 行", sent, total);
            _cancel.Text = L10n.T("关闭");
            _cancel.Enabled = true;
        }

        public void ShowFailure(string message, int sent, int total)
        {
            if (IsDisposed) return;
            _completed = true;
            _status.Text = L10n.Tf("发送中断：已发送 {0}/{1} 行；{2}", sent, total, message ?? string.Empty);
            _cancel.Text = L10n.T("关闭");
            _cancel.Enabled = true;
        }

        private void RequestCancel()
        {
            _cancel.Enabled = false;
            _status.Text = L10n.T("正在取消发送…");
            try { _cancelSending(); } catch { }
        }
    }
}
