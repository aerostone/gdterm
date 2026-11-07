using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

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
        private EmptyStateView _empty;
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
                int n;
                lock (Sync) n = Buffer.Count;
                if (n == 0)
                {
                    // 空列表点「清空」不该什么也不发生：这是最容易让用户怀疑按钮坏了的地方
                    ToastNotifier.Info("通知中心已经是空的");
                    return;
                }
                if (MessageBox.Show(this.FindForm(), "清空 " + n + " 条通知？清空后无法恢复。", "通知中心",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    lock (Sync) Buffer.Clear();
                    Reload();
                    ToastNotifier.Success("已清空 " + n + " 条通知", 2000);
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("NotificationCenter.Clear", ex); } catch (Exception) { }
                    ToastNotifier.Error("清空失败：" + ex.Message);
                }
            };

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                IntegralHeight = false
            };

            var title = new AntdUI.Label {
                Text = "通知中心",
                Dock = DockStyle.Top,
                Height = Math.Max(DpiScale.V(this, 28), FormFontPolicy.LineBox(FormFontPolicy.UiFont(+1f), this)),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 8), 0, 0, 0),
                Font = Services.FormFontPolicy.UiFont(+1f, FontStyle.Bold)
            };

            // 双击/回车 = 复制这一条（通知往往要贴给别人或提 issue，选中后无处可去是原来的空洞）
            _list.DoubleClick += (s, e) => CopySelected();
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { CopySelected(); e.Handled = true; }
                if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
            };

            Controls.Add(_list);
            Controls.Add(_clear);
            Controls.Add(title);

            // 审计 C1：通知中心空态（“没有通知”和“没加载出来”此前长得一模一样）
            _empty = EmptyStateView.Attach(this, "暂无通知",
                "连接状态变化、扫描结果、导入导出等操作会在这里留痕");
            // 首帧必须反映缓冲区现状：此前只在 Push 时刷新，侧栏一打开是空白表 + 隐藏空态，
            // 看着像"通知全丢了"（CI 0.1.381 侧栏 dump 实证空态未显示）。
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

        /// <summary>只读快照（测试与程序化读取用，A4）。</summary>
        public static string[] Snapshot()
        {
            lock (Sync) return Buffer.ToArray();
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
            if (_empty != null) _empty.SetEmpty(_list.Items.Count == 0);
        }

        private void CopySelected()
        {
            if (_list.SelectedItem == null) return;
            var text = _list.SelectedItem.ToString();
            try
            {
                Clipboard.SetText(text);
                ToastNotifier.Success("已复制通知", 2000);
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程占用是常见失败：不能静默
                try { DiagLog.Error("NotificationCenter.Copy", ex); } catch (Exception) { }
                ToastNotifier.Error("复制失败：" + ex.Message);
            }
        }

        private void RemoveSelected()
        {
            if (_list.SelectedItem == null) return;
            var text = _list.SelectedItem.ToString();
            try
            {
                lock (Sync) Buffer.Remove(text);
                Reload();
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("NotificationCenter.Remove", ex); } catch (Exception) { }
                ToastNotifier.Error("删除通知失败：" + ex.Message);
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Reload();
        }
    }
}
