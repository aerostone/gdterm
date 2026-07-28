using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

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

        public TransferCenterPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            var title = new Label
            {
                Text = "传输中心",
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };
            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                IntegralHeight = false
            };
            var tip = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Text = "上传/下载完成后会出现在此列表",
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };
            Controls.Add(_list);
            Controls.Add(tip);
            Controls.Add(title);
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
            try { NotificationCenterPanel.Push("XFER", message); } catch { }
        }

        public void Reload()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            lock (Sync)
            {
                if (Jobs.Count == 0)
                    _list.Items.Add("（暂无传输记录）");
                else
                    foreach (var j in Jobs) _list.Items.Add(j);
            }
            _list.EndUpdate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Reload();
        }
    }
}
