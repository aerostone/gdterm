using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SFTP/Zmodem 传输进度对话框。支持取消与百分比更新。
    /// </summary>
    public sealed class TransferProgressDialog : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _detailLabel;
        private readonly ProgressBar _bar;
        private readonly Label _percentLabel;
        private readonly Button _cancelButton;
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
            Size = new Size(420, 180);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = new Font("Microsoft YaHei UI", 9f);

            _titleLabel = new Label
            {
                Text = title ?? "传输中…",
                Location = new Point(16, 16),
                Size = new Size(380, 22),
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
            };
            _detailLabel = new Label
            {
                Text = "准备中…",
                Location = new Point(16, 44),
                Size = new Size(380, 20),
                ForeColor = GdtermColorTable.Muted
            };
            _bar = new ProgressBar
            {
                Location = new Point(16, 72),
                Size = new Size(380, 18),
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };
            _percentLabel = new Label
            {
                Text = "0%",
                Location = new Point(16, 98),
                Size = new Size(200, 18),
                ForeColor = GdtermColorTable.Muted
            };
            _cancelButton = new Button
            {
                Text = "取消",
                Location = new Point(310, 110),
                Size = new Size(86, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _cancelButton.FlatAppearance.BorderColor = GdtermColorTable.Border;
            _cancelButton.Click += (s, e) =>
            {
                _cancelled = true;
                _detailLabel.Text = "正在取消…";
                _cancelButton.Enabled = false;
            };

            Controls.Add(_titleLabel);
            Controls.Add(_detailLabel);
            Controls.Add(_bar);
            Controls.Add(_percentLabel);
            Controls.Add(_cancelButton);
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
