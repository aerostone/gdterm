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
            // 动作行 AutoSize：改成可换行的 FlowLayoutPanel 后它自己报得出优选高
            //（旧的 AntdUI.StackPanel 优选高恒 0 → AutoSize 行压成 1px → 四个按钮画成 0 高）
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
            // 副标题用原生 Label + Dock=Top + AutoSize：窄侧栏（实测 234–274px）里
            // 长副标题必须折行，AntdUI.Label 的 AutoSize 只按单行量宽 → 尾部被裁
            var subtitle = new Label {
                Text = L10n.T("便携运维终端  ·  选择最近会话或新建连接"),
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = GdtermColorTable.Muted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = DpiScale.Pad(this, 2, 0, 0, 0)
            };
            // 头两行用 TLP 排：标题固定行高（字形行盒），副标题 AutoSize 行 + 锚定左右，
            // 折行后行高自动长出来 —— 旧版 Panel(AutoSize) 靠默认高 100 兜着，14pt 下副标题被裁。
            var header = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, titleBoxH));
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            title.Dock = DockStyle.Fill;
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(subtitle, 0, 1);
            root.Controls.Add(header, 0, 0);

            // 横向按钮行：侧栏只有 234–274px，四个按钮（约 430px）必然放不下一行，
            // 所以用可换行的 FlowLayoutPanel（AntdUI.StackPanel 不换行，宽了就画到面板外）。
            // 间距按 Margin=(0,0,10,6) 给：末列留 10px 透明尾隙，视觉上不可见，换来窄栏不溢出。
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0),
                Padding = new Padding(0, DpiScale.V(this, 8), 0, DpiScale.V(this, 8)),
                BackColor = GdtermColorTable.Background
            };
            actions.Controls.Add(MakeAction(L10n.T("新建连接"), () => { if (NewConnectionRequested != null) NewConnectionRequested(); }));
            actions.Controls.Add(MakeAction(L10n.T("本地终端"), () => { if (OpenLocalTerminalRequested != null) OpenLocalTerminalRequested(); }));
            actions.Controls.Add(MakeAction(L10n.T("书签"), () => { if (OpenBookmarksRequested != null) OpenBookmarksRequested(); }));
            actions.Controls.Add(MakeAction(L10n.T("密码库"), () => { if (OpenKeePassRequested != null) OpenKeePassRequested(); }));
            root.Controls.Add(actions, 0, 1);

            // F10 搜索过滤：按标题/副标题过滤下方两个列表（大小写不敏感）
            // 最低高 = 字段高 + 上下内边距：不给下限时行高取 AntdUI.Input 的优选高，
            // DPI 150% 下内边距翻倍、优选高不涨 → 输入框反而从 88 缩到 82（矩阵门反空转实证）
            var filterHost = new Panel { Dock = DockStyle.Fill, Padding = DpiScale.Pad(this, 0, 4, 0, 8) };
            filterHost.MinimumSize = new Size(0,
                FormFontPolicy.FieldHeight(this) + DpiScale.V(this, 12));
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

            FitEmptyLabels();
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
            // 侧栏宽度变化（拖分隔条 / 换字号）时，行宽跟着走，不会留半截
            var hostRef = host; // out 参数不能进 lambda
            host.SizeChanged += delegate { FitRowWidths(hostRef); };
            panel.Controls.Add(host);
            panel.Controls.Add(lbl);
            return panel;
        }

        private void AddRow(FlowLayoutPanel host, string title, string sub, Action onClick)
        {
            int btnHeight = Math.Max(DpiScale.V(this, 40), FormFontPolicy.RowStep(this));
            // 行宽跟随宿主列宽（原来写死 ≥320：侧栏只有 234–274px，整行会画到面板外被裁）；
            // 构造期宿主还没布局（宽 0），先用下限兜底，MakeSection 里 SizeChanged 再校正
            var btn = new AntdUI.Button {
                Width = Math.Max(DpiScale.V(this, 160), RowWidth(host)),
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
            // 原生 Label + AutoSize：空态文案在窄侧栏里要折行（AntdUI.Label 单行量宽会画到面板外）
            host.Controls.Add(new Label
            {
                ForeColor = GdtermColorTable.Muted,
                BackColor = Color.Transparent,
                Font = Services.FormFontPolicy.UiFont(),
                AutoSize = true,
                Text = text,
                Margin = DpiScale.Pad(this, 4, 8, 4, 4)
            });
        }

        /// <summary>行按钮的可用宽：宿主内宽减去滚动条（宿主未布局时按 320 设计 px 兜底）。</summary>
        private int RowWidth(FlowLayoutPanel host)
        {
            if (host == null || host.ClientSize.Width <= 0) return DpiScale.V(this, 320);
            return Math.Max(DpiScale.V(this, 160),
                host.ClientSize.Width - host.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        }

        /// <summary>宿主变宽/变窄时把行按钮跟着改宽（只动数据行按钮，不动空态文本）。</summary>
        private void FitRowWidths(FlowLayoutPanel host)
        {
            if (host == null || host.IsDisposed || host.ClientSize.Width <= 0) return;
            int w = RowWidth(host);
            foreach (Control c in host.Controls)
            {
                var b = c as AntdUI.Button;
                if (b != null && b.Width != w) b.Width = w;
            }
        }

        /// <summary>空态文案按宿主实际宽收口（受限于侧栏宽度，不能超出一行画到面板外）。</summary>
        private void FitEmptyLabels()
        {
            foreach (var host in new Control[] { _recentHost, _favHost })
            {
                if (host == null || host.IsDisposed) continue;
                int avail = host.ClientSize.Width - host.Padding.Horizontal - DpiScale.V(this, 12);
                if (avail <= 0) continue;
                foreach (Control c in host.Controls)
                {
                    var lbl = c as Label;
                    if (lbl != null && lbl.AutoSize) lbl.MaximumSize = new Size(avail, 0);
                }
            }
        }

        private AntdUI.Button MakeAction(string text, Action onClick)
        {
            var b = new AntdUI.Button {
                Text = text,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, DpiScale.V(this, 10), DpiScale.V(this, 6))
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
