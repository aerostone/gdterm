using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 传输任务摘要面板（SFTP/Zmodem 完成记录）。
    /// </summary>
    public sealed class TransferCenterPanel : UserControl
    {
        private static readonly object Sync = new object();

        /// <summary>一条传输记录（原来是拼好的字符串，无法按时间/说明分列，也导不出 CSV）。</summary>
        private sealed class Job
        {
            public DateTime At { get; set; }
            public string Message { get; set; }
            public string TimeText { get { return At.ToString("HH:mm:ss"); } }
        }

        private static readonly System.Collections.Generic.List<Job> Jobs =
            new System.Collections.Generic.List<Job>();

        private readonly ListView _list;
        private ListShell _shell;
        private EmptyStateView _empty;

        public TransferCenterPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            var title = new AntdUI.Label {
                Text = "传输中心",
                Dock = DockStyle.Top,
                Height = Math.Max(DpiScale.V(this, 28), FormFontPolicy.LineBox(FormFontPolicy.UiFont(+1f), this)),
                Font = Services.FormFontPolicy.UiFont(+1f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 8), 0, 0, 0)
            };
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont()
            };
            _list.Columns.Add("时间", DpiScale.V(this, 70));
            _list.Columns.Add("说明", DpiScale.V(this, 360));
            var tip = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                Height = Math.Max(DpiScale.V(this, 22), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this)),
                Text = "上传/下载完成后会出现在此列表",
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 8), 0, 0, 0)
            };
            // 清空按钮（此前只有 Record/Reload，无清空入口，用户报 Record-only）
            var clear = new AntdUI.Button {
                Text = "清空",
                Dock = DockStyle.Bottom,
                Height = Math.Max(DpiScale.V(this, 28), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this)),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            clear.Click += (s, e) =>
            {
                int n;
                lock (Sync) n = Jobs.Count;
                if (n == 0)
                {
                    ToastNotifier.Info("传输记录已经是空的");
                    return;
                }
                if (MessageBox.Show(FindForm(), "清空 " + n + " 条传输记录？", "传输中心",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    lock (Sync) Jobs.Clear();
                    Reload();
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("TransferCenter.Clear", ex); } catch (Exception) { }
                    ToastNotifier.Error("清空失败：" + ex.Message);
                }
            };

            // 双击/回车 = 复制这条记录（要贴给同事或提 issue 时原来没有任何出路）
            _list.DoubleClick += (s, e) => CopySelected();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { CopySelected(); e.Handled = true; }
            };

            _shell = new ListShell(_list, "过滤传输记录…");
            _shell.AddAction("导出", (s2, e2) => _shell.ExportCsv(FindForm(), "transfers")).Name = "TransferExportButton";

            Controls.Add(_shell);
            Controls.Add(tip);
            Controls.Add(clear);
            Controls.Add(title);
            _empty = EmptyStateView.Attach(this, "还没有传输记录",
                "从 SFTP 面板上传/下载、Zmodem 收发的文件，完成后都会记在这里",
                "打开传输日志目录", (s2, e2) => OpenLogDir());
            _list.AccessibleName = "传输记录列表";
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }

            Reload();
        }

        public static void Record(string message)
        {
            var job = new Job { At = DateTime.Now, Message = message ?? "" };
            lock (Sync)
            {
                Jobs.Insert(0, job);
                while (Jobs.Count > 100) Jobs.RemoveAt(Jobs.Count - 1);
            }
            try { NotificationCenterPanel.Push("XFER", message); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TransferCenterPanel", exSwallowed); } catch { } }
        }

        public void Reload()
        {
            var rows = new System.Collections.Generic.List<ListViewItem>();
            int count;
            lock (Sync)
            {
                count = Jobs.Count;
                foreach (var j in Jobs)
                {
                    var item = new ListViewItem(j.TimeText);
                    item.SubItems.Add(j.Message ?? "");
                    item.Tag = j;
                    rows.Add(item);
                }
            }
            _shell.SetItems(rows);
            if (_empty != null) _empty.SetEmpty(count == 0);
        }

        private void CopySelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var it = _list.SelectedItems[0];
            try
            {
                Clipboard.SetText(it.Text + "  " + (it.SubItems.Count > 1 ? it.SubItems[1].Text : ""));
                ToastNotifier.Success("已复制传输记录", 2000);
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("TransferCenter.Copy", ex); } catch (Exception) { }
                ToastNotifier.Error("复制失败：" + ex.Message);
            }
        }

        private void OpenLogDir()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gdterm", "logs");
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("TransferCenter.OpenLogDir", ex); } catch (Exception) { }
                ToastNotifier.Error("打开日志目录失败：" + ex.Message);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Reload();
        }
    }
}
