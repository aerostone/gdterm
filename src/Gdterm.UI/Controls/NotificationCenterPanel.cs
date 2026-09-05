using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 简易通知中心：汇总 Toast 级别事件（内存环形缓冲）。
    /// </summary>
    public sealed class NotificationCenterPanel : UserControl
    {
        private static readonly object Sync = new object();
        private static readonly System.Collections.Generic.List<string> Buffer =
            new System.Collections.Generic.List<string>();
        private const int Max = 200;

        private readonly ListBox _list;
        private readonly AntdUI.Button _clear;

        public NotificationCenterPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            _clear = new AntdUI.Button {
                Text = "清空",
                Dock = DockStyle.Bottom,
                Height = 28,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _clear.Click += (s, e) =>
            {
                lock (Sync) Buffer.Clear();
                Reload();
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

            var title = new AntdUI.Label {
                Text = "通知中心",
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = Services.FormFontPolicy.UiFont(+1f, FontStyle.Bold)
            };

            Controls.Add(_list);
            Controls.Add(_clear);
            Controls.Add(title);
            Reload();
        }

        public static void Push(string level, string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + " [" + level + "] " + (message ?? "");
            lock (Sync)
            {
                Buffer.Insert(0, line);
                while (Buffer.Count > Max) Buffer.RemoveAt(Buffer.Count - 1);
            }
        }

        public void Reload()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            lock (Sync)
            {
                foreach (var line in Buffer) _list.Items.Add(line);
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
