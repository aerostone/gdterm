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
        private static readonly System.Collections.Generic.List<string> Jobs =
            new System.Collections.Generic.List<string>();

        private readonly ListBox _list;
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
            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                IntegralHeight = false
            };
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

            Controls.Add(_list);
            Controls.Add(tip);
            Controls.Add(clear);
            Controls.Add(title);
            _empty = EmptyStateView.Attach(this, "还没有传输记录",
                "从 SFTP 面板上传/下载、Zmodem 收发的文件，完成后都会记在这里",
                "打开传输日志目录", (s2, e2) => OpenLogDir());
            Reload();
        }

        public static void Record(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + (message ?? "");
            lock (Sync)
            {
                Jobs.Insert(0, line);
                while (Jobs.Count > 100) Jobs.RemoveAt(Jobs.Count - 1);
            }
            try { NotificationCenterPanel.Push("XFER", message); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("TransferCenterPanel", exSwallowed); } catch { } }
        }

        public void Reload()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            int count;
            lock (Sync)
            {
                count = Jobs.Count;
                foreach (var j in Jobs) _list.Items.Add(j);
            }
            _list.EndUpdate();
            // 占位项「（暂无传输记录）」退役：它既不能选中也没有意义，改由空态层承担引导
            if (_empty != null) _empty.SetEmpty(count == 0);
        }

        private void CopySelected()
        {
            if (_list.SelectedItem == null) return;
            try
            {
                Clipboard.SetText(_list.SelectedItem.ToString());
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
