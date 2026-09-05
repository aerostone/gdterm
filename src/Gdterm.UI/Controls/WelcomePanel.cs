using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 无标签时的启动落地页：最近连接 / 收藏 / 快捷操作。
    /// </summary>
    public sealed class WelcomePanel : UserControl
    {
        private readonly IConnectionStore _store;
        private readonly IBookmarkStore _bookmarks;
        private readonly FlowLayoutPanel _recentHost;
        private readonly FlowLayoutPanel _favHost;

        public event Action NewConnectionRequested;
        public event Action OpenLocalTerminalRequested;
        public event Action OpenBookmarksRequested;
        public event Action OpenKeePassRequested;
        public event Action<ConnectionConfig> OpenConnectionRequested;

        public WelcomePanel(IConnectionStore store, IBookmarkStore bookmarks)
        {
            _store = store;
            _bookmarks = bookmarks;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = Services.FormFontPolicy.UiFont(+0.5f);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(40, 28, 40, 28)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var title = new AntdUI.Label {
                Text = "gdterm",
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 28f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Accent,
                TextAlign = ContentAlignment.BottomLeft
            };
            var subtitle = new AntdUI.Label {
                Text = "便携运维终端  ·  选择最近会话或新建连接",
                Dock = DockStyle.Fill,
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(2, 4, 0, 0)
            };
            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(subtitle);
            header.Controls.Add(title);
            title.Height = 40;
            title.Dock = DockStyle.Top;
            root.Controls.Add(header, 0, 0);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 8)
            };
            actions.Controls.Add(MakeAction("新建连接", () => { if (NewConnectionRequested != null) NewConnectionRequested(); }));
            actions.Controls.Add(MakeAction("本地终端", () => { if (OpenLocalTerminalRequested != null) OpenLocalTerminalRequested(); }));
            actions.Controls.Add(MakeAction("书签", () => { if (OpenBookmarksRequested != null) OpenBookmarksRequested(); }));
            actions.Controls.Add(MakeAction("密码库", () => { if (OpenKeePassRequested != null) OpenKeePassRequested(); }));
            root.Controls.Add(actions, 0, 1);

            root.Controls.Add(MakeSection("最近连接", out _recentHost), 0, 2);
            root.Controls.Add(MakeSection("收藏 / 书签", out _favHost), 0, 3);

            Controls.Add(root);
            Reload();
        }

        public void Reload()
        {
            _recentHost.Controls.Clear();
            _favHost.Controls.Clear();

            try
            {
                if (_bookmarks != null)
                {
                    foreach (var r in _bookmarks.GetRecentConnections(8) ?? new List<RecentConnection>())
                    {
                        if (r == null) continue;
                        var title = !string.IsNullOrEmpty(r.Host) ? r.Host : (r.ConnectionId ?? "连接");
                        var sub = (r.Protocol ?? "") + "  " + (r.Success ? "成功" : "失败")
                                  + "  " + r.ConnectedAt.ToString("MM-dd HH:mm");
                        AddRow(_recentHost, title, sub, () => OpenRecent(r));
                    }
                }
            }
            catch { }

            if (_recentHost.Controls.Count == 0)
                AddEmpty(_recentHost, "暂无最近连接 — 双击左侧树或点「新建连接」开始");

            try
            {
                if (_bookmarks != null)
                {
                    var all = _bookmarks.LoadAll() ?? new List<SessionBookmark>();
                    int n = 0;
                    foreach (var b in all)
                    {
                        if (b == null || !b.IsFavorite) continue;
                        AddRow(_favHost, b.Name ?? b.ConnectionId ?? "书签",
                            b.Tags ?? b.ConnectionId ?? "",
                            () => OpenBookmark(b));
                        n++;
                        if (n >= 8) break;
                    }
                    if (n == 0)
                    {
                        foreach (var b in all)
                        {
                            if (b == null) continue;
                            AddRow(_favHost, b.Name ?? b.ConnectionId ?? "书签",
                                b.Tags ?? "",
                                () => OpenBookmark(b));
                            n++;
                            if (n >= 6) break;
                        }
                    }
                }
            }
            catch { }

            if (_favHost.Controls.Count == 0)
                AddEmpty(_favHost, "暂无收藏 — 在书签面板中标记收藏后显示在这里");
        }

        private void OpenRecent(RecentConnection r)
        {
            if (r == null || _store == null) return;
            try
            {
                var cfg = _store.GetById(r.ConnectionId);
                if (cfg != null && OpenConnectionRequested != null) OpenConnectionRequested(cfg);
                else ToastNotifier.Warning("连接配置已不存在: " + (r.Host ?? r.ConnectionId));
            }
            catch (Exception ex) { ToastNotifier.Error(ex.Message); }
        }

        private void OpenBookmark(SessionBookmark b)
        {
            if (b == null || _store == null) return;
            try
            {
                var cfg = _store.GetById(b.ConnectionId);
                if (cfg != null && OpenConnectionRequested != null) OpenConnectionRequested(cfg);
                else ToastNotifier.Warning("书签对应连接不存在: " + (b.Name ?? b.ConnectionId));
            }
            catch (Exception ex) { ToastNotifier.Error(ex.Message); }
        }

        private Control MakeSection(string title, out FlowLayoutPanel host)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 8, 0, 0)
            };
            var lbl = new AntdUI.Label {
                Text = title,
                Dock = DockStyle.Top,
                Height = 24,
                Font = Services.FormFontPolicy.UiFont(+1f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Foreground
            };
            host = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            panel.Controls.Add(host);
            panel.Controls.Add(lbl);
            return panel;
        }

        private void AddRow(FlowLayoutPanel host, string title, string sub, Action onClick)
        {
            var btn = new AntdUI.Button {
                Width = Math.Max(320, Width - 100),
                Height = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, 0, 0, 6),
                Padding = new Padding(12, 0, 12, 0),
                Text = title + (string.IsNullOrEmpty(sub) ? "" : "   ·   " + sub),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = GdtermColorTable.Border;
            btn.FlatAppearance.MouseOverBackColor = GdtermColorTable.Hover;
            btn.Click += (s, e) => { if (onClick != null) onClick(); };
            host.Controls.Add(btn);
        }

        private void AddEmpty(FlowLayoutPanel host, string text)
        {
            host.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Text = text,
                Margin = new Padding(4, 8, 4, 4)
            });
        }

        private Button MakeAction(string text, Action onClick)
        {
            var b = new AntdUI.Button {
                Text = text,
                AutoSize = true,
                MinimumSize = DpiScale.S(this, 110, 32),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = GdtermColorTable.Border;
            b.FlatAppearance.MouseOverBackColor = GdtermColorTable.Hover;
            b.Click += (s, e) => { if (onClick != null) onClick(); };
            return b;
        }
    }
}
