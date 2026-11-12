using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core;
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
        private AntdUI.Input _filter;
        // F10 键盘导航的行序（Reload 时按视觉顺序重建：最近在前、收藏在后）
        private List<AntdUI.Button> _navRows = new List<AntdUI.Button>();

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
                RowCount = 5,
                Padding = DpiScale.Pad(this, 40, 24, 40, 20)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var titleFont = new Font("Consolas", 28f, FontStyle.Bold);
            // 行盒高度取真实 GDI 行高 × 1.2（对应原型 line-height:1.15 的“行盒”），
            // 配合 TextAlign=MiddleLeft 让字形在盒内垂直居中——
            // 旧版固定 Height=40 + BottomLeft 把字形压到盒底，视觉中心偏高，是“太靠上”的根源。
            int titleBoxH = Math.Max(DpiScale.V(this, 44), FormFontPolicy.LineBox(titleFont, this, 1.2f));
            var title = new AntdUI.Label {
                Text = "gdterm",
                Dock = DockStyle.Top,
                Height = titleBoxH,
                Font = titleFont,
                ForeColor = GdtermColorTable.Accent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var subtitle = new AntdUI.Label {
                Text = L10n.T("便携运维终端  ·  选择最近会话或新建连接"),
                Dock = DockStyle.Fill,
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = DpiScale.Pad(this, 2, 0, 0, 0)
            };
            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(subtitle);
            header.Controls.Add(title);
            root.Controls.Add(header, 0, 0);

            // 用 AntdUI.StackPanel + Gap 做横向按钮行：Gap 只在按钮之间产生间距，
            // 首尾不贴边也不留多余空白（等价 CSS gap），避免 WinForms FlowLayoutPanel
            // 的 Margin=(0,0,N,0) 造成「首个贴左、末个留尾」的非对称间距。
            var actions = new AntdUI.StackPanel
            {
                Dock = DockStyle.Fill,
                Vertical = false,
                Gap = DpiScale.V(this, 10),
                AutoContainerBgTransparent = true,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, DpiScale.V(this, 8), 0, DpiScale.V(this, 8))
            };
            actions.Controls.Add(MakeAction(L10n.T("新建连接"), () => { if (NewConnectionRequested != null) NewConnectionRequested(); }));
            actions.Controls.Add(MakeAction(L10n.T("本地终端"), () => { if (OpenLocalTerminalRequested != null) OpenLocalTerminalRequested(); }));
            actions.Controls.Add(MakeAction(L10n.T("书签"), () => { if (OpenBookmarksRequested != null) OpenBookmarksRequested(); }));
            actions.Controls.Add(MakeAction(L10n.T("密码库"), () => { if (OpenKeePassRequested != null) OpenKeePassRequested(); }));
            root.Controls.Add(actions, 0, 1);

            // F10 搜索过滤：按标题/副标题过滤下方两个列表（大小写不敏感）
            var filterHost = new Panel { Dock = DockStyle.Fill, Padding = DpiScale.Pad(this, 0, 4, 0, 8) };
            _filter = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                PlaceholderText = L10n.T("过滤最近连接 / 收藏…")
            };
            _filter.TextChanged += (s, e) => Reload();
            filterHost.Controls.Add(_filter);
            root.Controls.Add(filterHost, 0, 2);

            root.Controls.Add(MakeSection(L10n.T("最近连接"), out _recentHost), 0, 3);
            root.Controls.Add(MakeSection(L10n.T("收藏 / 书签"), out _favHost), 0, 4);

            Controls.Add(root);
            // F10 键盘导航：↑/↓/Home/End 走可见行（环绕），Enter 打开连接；
            // 子控件未处理的键会沿父链冒泡到本面板（KeyPreview 仅 Form 有，UserControl 用冒泡）；
            // 焦点在过滤框内时只截 ↓（落入列表），不截输入与光标键
            KeyDown += OnNavKeyDown;
            Reload();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        public void Reload()
        {
            var q = _filter != null ? (_filter.Text ?? "").Trim() : "";
            _recentHost.Controls.Clear();
            _favHost.Controls.Clear();
            _navRows.Clear();

            try
            {
                if (_bookmarks != null)
                {
                    foreach (var r in _bookmarks.GetRecentConnections(8) ?? new List<RecentConnection>())
                    {
                        if (r == null) continue;
                        var title = !string.IsNullOrEmpty(r.Host) ? r.Host : (r.ConnectionId ?? L10n.T("连接"));
                        var sub = (r.Protocol ?? "") + "  " + (r.Success ? L10n.T("成功") : L10n.T("失败"))
                                  + "  " + r.ConnectedAt.ToString("MM-dd HH:mm");
                        if (q.Length > 0 && RowMatches(title, sub, q) < 0) continue;
                        AddRow(_recentHost, title, sub, () => OpenRecent(r));
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("WelcomePanel", exSwallowed); } catch { } }

            if (_recentHost.Controls.Count == 0)
                AddEmpty(_recentHost, q.Length > 0
                    ? L10n.T("没有匹配的最近连接")
                    : L10n.T("暂无最近连接 — 双击左侧树或点「新建连接」开始"));

            try
            {
                if (_bookmarks != null)
                {
                    var all = _bookmarks.LoadAll() ?? new List<SessionBookmark>();
                    int n = 0;
                    foreach (var b in all)
                    {
                        if (b == null || !b.IsFavorite) continue;
                        var title = b.Name ?? b.ConnectionId ?? L10n.T("书签");
                        var sub = b.Tags ?? b.ConnectionId ?? "";
                        if (q.Length > 0 && RowMatches(title, sub, q) < 0) continue;
                        AddRow(_favHost, title, sub, () => OpenBookmark(b));
                        n++;
                        if (n >= 8) break;
                    }
                    if (n == 0)
                    {
                        foreach (var b in all)
                        {
                            if (b == null) continue;
                            var title = b.Name ?? b.ConnectionId ?? L10n.T("书签");
                            var sub = b.Tags ?? "";
                            if (q.Length > 0 && RowMatches(title, sub, q) < 0) continue;
                            AddRow(_favHost, title, sub, () => OpenBookmark(b));
                            n++;
                            if (n >= 6) break;
                        }
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("WelcomePanel", exSwallowed); } catch { } }

            if (_favHost.Controls.Count == 0)
                AddEmpty(_favHost, q.Length > 0
                    ? L10n.T("没有匹配的收藏")
                    : L10n.T("暂无收藏 — 在书签面板中标记收藏后显示在这里"));
        }

        private static int RowMatches(string title, string sub, string q)
        {
            return ((title ?? "") + " " + (sub ?? "")).IndexOf(q, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>F10 键盘导航：焦点在行按钮上时 ↑/↓/Home/End/Enter 全部接管；
        /// 焦点在过滤框时只截 ↓（落入列表首行），输入与光标键不截。</summary>
        private void OnNavKeyDown(object sender, KeyEventArgs e)
        {
            var form = FindForm();
            bool inFilter = form != null && form.ActiveControl is TextBox;
            if (inFilter && e.KeyCode != Keys.Down) return;
            if (_navRows.Count == 0) return;
            int i = -1;
            for (int k = 0; k < _navRows.Count; k++)
                if (_navRows[k].Focused) { i = k; break; }
            int target = -1;
            switch (e.KeyCode)
            {
                case Keys.Down: target = i < 0 ? 0 : (i + 1) % _navRows.Count; break;
                case Keys.Up: target = i < 0 ? _navRows.Count - 1 : (i - 1 + _navRows.Count) % _navRows.Count; break;
                case Keys.Home: target = 0; break;
                case Keys.End: target = _navRows.Count - 1; break;
                case Keys.Enter: target = i; break;
                default: return;
            }
            if (target < 0) return;
            if (e.KeyCode == Keys.Enter) _navRows[target].PerformClick();
            else _navRows[target].Focus();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void OpenRecent(RecentConnection r)
        {
            if (r == null || _store == null) return;
            try
            {
                var cfg = _store.GetById(r.ConnectionId);
                if (cfg != null && OpenConnectionRequested != null) OpenConnectionRequested(cfg);
                else ToastNotifier.Warning(L10n.T("连接配置已不存在: ") + (r.Host ?? r.ConnectionId));
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
                else ToastNotifier.Warning(L10n.T("书签对应连接不存在: ") + (b.Name ?? b.ConnectionId));
            }
            catch (Exception ex) { ToastNotifier.Error(ex.Message); }
        }

        private Control MakeSection(string title, out FlowLayoutPanel host)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = DpiScale.Pad(this, 0, 8, 0, 0)
            };
            var lbl = FormFontPolicy.FitSingleLineLabel(new AntdUI.Label {
                Text = title,
                Dock = DockStyle.Top,
                Font = Services.FormFontPolicy.UiFont(+1f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Foreground
            });
            host = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = DpiScale.Pad(this, 0, 4, 0, 0)
            };
            panel.Controls.Add(host);
            panel.Controls.Add(lbl);
            return panel;
        }

        private void AddRow(FlowLayoutPanel host, string title, string sub, Action onClick)
        {
            int btnHeight = Math.Max(DpiScale.V(this, 40), FormFontPolicy.RowStep(this));
            var btn = new AntdUI.Button {
                Width = Math.Max(DpiScale.V(this, 320), Width - DpiScale.V(this, 100)),
                Height = btnHeight,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 6)),
                Padding = new Padding(DpiScale.V(this, 12), 0, DpiScale.V(this, 12), 0),
                Text = title + (string.IsNullOrEmpty(sub) ? "" : "   ·   " + sub),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => { if (onClick != null) onClick(); };
            host.Controls.Add(btn);
            // F10 键盘导航行序：只收两个数据区的行（空态标签不是行）
            if (host == _recentHost || host == _favHost) _navRows.Add(btn);
        }

        private void AddEmpty(FlowLayoutPanel host, string text)
        {
            host.Controls.Add(FormFontPolicy.FitSingleLineLabel(new AntdUI.Label
            {
                ForeColor = GdtermColorTable.Muted,
                Text = text,
                Margin = DpiScale.Pad(this, 4, 8, 4, 4)
            }));
        }

        private AntdUI.Button MakeAction(string text, Action onClick)
        {
            var b = new AntdUI.Button {
                Text = text,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Cursor = Cursors.Hand
            };
            // AutoSize + 不给 Padding 时 AntdUI 只按库内 sps 算宽，左右内边距是 0
            // （ui-tree-check「按钮左右padding=0(贴边风险)」实证：文字直接贴边框）。
            // SizeActionButton 用 GDI 实测文字宽 + 显式 Padding，DPI/字号两个方向都对。
            FormFontPolicy.SizeActionButton(this, b, 16, 6, Math.Max(32, FormFontPolicy.RowStep(this)));
            b.Click += (s, e) => { if (onClick != null) onClick(); };
            return b;
        }
    }
}
