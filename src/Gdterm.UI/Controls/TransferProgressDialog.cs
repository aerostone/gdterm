using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SFTP/Zmodem 传输进度对话框。支持取消与百分比更新。
    /// </summary>
    public sealed class TransferProgressDialog : AntdUI.Window
    {
        private readonly AntdUI.Label _titleLabel;
        private readonly AntdUI.Label _detailLabel;
        private readonly ProgressBar _bar;
        private readonly AntdUI.Label _percentLabel;
        private readonly AntdUI.Button _cancelButton;
        private bool _completed;
        private bool _cancelled;

        public bool IsCancelled { get { return _cancelled; } }

        public TransferProgressDialog(string title)
        {
            Text = string.IsNullOrEmpty(title) ? "传输" : title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            // 规范规则②：布局改用 Dock，禁绝对坐标；尺寸经 DpiScale（规范见 docs/UI-SCALING-CONVENTIONS.md）

            _titleLabel = new AntdUI.Label
            {
                Text = title ?? "传输中…",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 14), DpiScale.V(this, 16), 0),
                Font = new Font(Font.FontFamily, Font.Size + 1f, FontStyle.Bold)
            };
            _detailLabel = new AntdUI.Label
            {
                Text = "准备中…",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 8), DpiScale.V(this, 16), 0)
            };
            var barHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, 30),
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 10), DpiScale.V(this, 16), 0)
            };
            _bar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };
            barHost.Controls.Add(_bar);
            var bottomFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = DpiScale.V(this, 44),
                WrapContents = false,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 8), DpiScale.V(this, 16), DpiScale.V(this, 8))
            };
            _percentLabel = new AntdUI.Label
            {
                Text = "0%",
                AutoSize = true,
                Margin = new Padding(3, 0, DpiScale.V(this, 12), 0),
                Anchor = AnchorStyles.Left
            };
            _cancelButton = new AntdUI.Button
            {
                Text = "取消",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Default
            };
            _cancelButton.Click += (s, e) =>
            {
                _cancelled = true;
                _detailLabel.Text = "正在取消…";
                _cancelButton.Enabled = false;
            };

            // 底部行：RightToLeft 流序使先加入的靠右 —— 取消按钮在右、百分比在其左
            bottomFlow.Controls.Add(_percentLabel);
            bottomFlow.Controls.Add(_cancelButton);

            // WinForms Dock z-order：先加 Fill，后加边缘（Top/Bottom）
            Controls.Add(barHost);
            Controls.Add(bottomFlow);
            Controls.Add(_detailLabel);
            Controls.Add(_titleLabel);

            ClientSize = new Size(DpiScale.V(this, 420), DpiScale.V(this, 150));
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        /// <summary>更新进度（可从任意线程调用）。</summary>
        public void Report(long transferred, long total, string detail = null)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => Report(transferred, total, detail))); }
                catch { }
                return;
            }

            if (!string.IsNullOrEmpty(detail))
                _detailLabel.Text = detail;

            int pct;
            if (total > 0)
                pct = (int)Math.Max(0, Math.Min(100, (transferred * 100L) / total));
            else
                pct = 0;

            try
            {
                _bar.Value = pct;
                _percentLabel.Text = total > 0
                    ? string.Format("{0}%  ({1} / {2})", pct, FormatBytes(transferred), FormatBytes(total))
                    : FormatBytes(transferred);
            }
            catch { }
        }

        public void Complete(bool success, string message = null)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => Complete(success, message))); }
                catch { }
                return;
            }

            _completed = true;
            _cancelButton.Text = "关闭";
            _cancelButton.Enabled = true;
            _cancelButton.Click -= null;
            // 重新绑定为关闭
            foreach (EventHandler h in new EventHandler[] { }) { }
            _cancelButton.Click += (s, e) => { try { DialogResult = success ? DialogResult.OK : DialogResult.Cancel; Close(); } catch { } };
            if (success)
            {
                _bar.Value = 100;
                _detailLabel.Text = message ?? "完成";
                _detailLabel.ForeColor = Color.FromArgb(0, 255, 65);
                _percentLabel.Text = "100%";
            }
            else
            {
                _detailLabel.Text = message ?? "失败";
                _detailLabel.ForeColor = Color.FromArgb(248, 81, 73);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_completed && !_cancelled)
                _cancelled = true;
            base.OnFormClosing(e);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.00") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
        }
    }

    /// <summary>
    /// IProgress 适配：把 Sftp 进度回调到 TransferProgressDialog。
    /// </summary>
    public sealed class TransferProgressAdapter : IProgress<Gdterm.Sftp.Models.FileTransferProgress>
    {
        private readonly TransferProgressDialog _dialog;
        private readonly string _fileName;

        public TransferProgressAdapter(TransferProgressDialog dialog, string fileName)
        {
            _dialog = dialog;
            _fileName = fileName ?? "";
        }

        public void Report(Gdterm.Sftp.Models.FileTransferProgress value)
        {
            if (_dialog == null || value == null) return;
            _dialog.Report(
                value.BytesTransferred,
                value.TotalBytes,
                string.IsNullOrEmpty(_fileName) ? "传输中…" : ("传输 " + _fileName));
        }
    }
}
