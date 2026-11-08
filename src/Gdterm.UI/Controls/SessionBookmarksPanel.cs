using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 书签 + 最近连接侧栏。双击打开关联连接。
    /// </summary>
    public class SessionBookmarksPanel : UserControl
    {
        private readonly IBookmarkStore _bookmarkStore;
        private readonly IConnectionStore _connectionStore;
        private ListView _bookmarksList;
        private ListView _recentList;
        private AntdUI.Input _searchBox;
        private AntdUI.Label _statusLabel;
        private ListShell _shellBookmarks;
        private ListShell _shellRecent;
        private EmptyStateView _emptyBookmarks;
        private EmptyStateView _emptyRecent;

        /// <summary>请求打开连接（ConnectionId）</summary>
        public event Action<ConnectionConfig> OpenConnectionRequested;

        public SessionBookmarksPanel(IBookmarkStore bookmarkStore, IConnectionStore connectionStore)
        {
            _bookmarkStore = bookmarkStore ?? throw new ArgumentNullException(nameof(bookmarkStore));
            _connectionStore = connectionStore;
            InitializeComponent();
            // F16 回收站：删除的书签可经「工具 → 回收站」恢复
            TrashStore.Default.RegisterRestorer("bookmark", entry =>
            {
                var bm = entry.Payload as SessionBookmark;
                if (bm != null)
                {
                    try { _bookmarkStore.Add(bm); } catch (Exception ex) { throw new InvalidOperationException(L10n.T("恢复书签失败：") + ex.Message, ex); }
                    if (!IsDisposed) Reload();
                }
            });
            Reload();
        }

        private void InitializeComponent()
        {
            BackColor = GdtermColorTable.Background;
            Dock = DockStyle.Fill;

            var title = new AntdUI.Label {
                Text = L10n.T("书签 / 最近连接"),
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(+1f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 8), 0, 0, 0)
            };

            _searchBox = new AntdUI.Input {
                Dock = DockStyle.Top,
                AutoSize = true,
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this)),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
            };
            _searchBox.TextChanged += (s, e) => FilterBookmarks();

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 4), DpiScale.V(this, 2), DpiScale.V(this, 4), DpiScale.V(this, 2))
            };
            var addBtn = MakeButton(L10n.T("添加书签"));
            addBtn.Click += OnAddBookmark;
            var delBtn = MakeButton(L10n.T("删除"));
            delBtn.Click += OnDeleteBookmark;
            var favBtn = MakeButton(L10n.T("收藏/取消"));
            favBtn.Click += OnToggleFavorite;
            var refreshBtn = MakeButton(L10n.T("刷新"));
            refreshBtn.Click += (s, e) => Reload();
            toolbar.Controls.AddRange(new Control[] { addBtn, delBtn, favBtn, refreshBtn });

            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 8), 0, 0, 0)
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = GdtermColorTable.Background,
                SplitterWidth = 4
            };

            _bookmarksList = CreateListView(L10n.T("书签"));
            _bookmarksList.CheckBoxes = true;   // 批量删除/收藏
            _bookmarksList.Columns.Add(L10n.T("名称"), 140);
            _bookmarksList.Columns.Add(L10n.T("连接"), 100);
            _bookmarksList.Columns.Add(L10n.T("标签"), 80);
            _bookmarksList.Columns.Add(L10n.T("次数"), 50);
            _bookmarksList.DoubleClick += (s, e) => OpenSelectedBookmark();
            _bookmarksList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) OpenSelectedBookmark();
                if (e.KeyCode == Keys.Delete) OnDeleteBookmark(s, e);
            };

            // 两个列表各配一个壳：排序 + 导出（书签页自己有搜索框，壳不再出第二个过滤）
            _shellBookmarks = new ListShell(_bookmarksList, showFilter: false);
            _shellBookmarks.AddAction(L10n.T("导出"), (s2, e2) => _shellBookmarks.ExportCsv(FindForm(), "bookmarks")).Name = "BookmarkExportButton";
            _bookmarksList.KeyDown += (s2, e2) =>
            {
                if (e2.Control && e2.KeyCode == Keys.F) { _searchBox.Focus(); _searchBox.SelectAll(); e2.SuppressKeyPress = true; }
            };

            var bmHost = new Panel { Dock = DockStyle.Fill };
            var bmLabel = new AntdUI.Label {
                Text = L10n.T("收藏书签"),
                Dock = DockStyle.Top,
                Height = Math.Max(DpiScale.V(this, 20), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this)),
                ForeColor = GdtermColorTable.Muted,
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 2), 0, 0)
            };
            bmHost.Controls.Add(_shellBookmarks);
            bmHost.Controls.Add(bmLabel);

            _recentList = CreateListView(L10n.T("最近"));
            _shellRecent = new ListShell(_recentList);
            _shellRecent.AddAction(L10n.T("导出"), (s2, e2) => _shellRecent.ExportCsv(FindForm(), "recent-connections")).Name = "RecentExportButton";
            _recentList.Columns.Add(L10n.T("主机"), 140);
            _recentList.Columns.Add(L10n.T("协议"), 60);
            _recentList.Columns.Add(L10n.T("时间"), 120);
            _recentList.Columns.Add(L10n.T("结果"), 50);
            _recentList.DoubleClick += (s, e) => OpenSelectedRecent();

            var recentHost = new Panel { Dock = DockStyle.Fill };
            var recentLabel = new AntdUI.Label {
                Text = L10n.T("最近连接"),
                Dock = DockStyle.Top,
                Height = Math.Max(DpiScale.V(this, 20), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this)),
                ForeColor = GdtermColorTable.Muted,
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 2), 0, 0)
            };
            recentHost.Controls.Add(_shellRecent);
            recentHost.Controls.Add(recentLabel);

            split.Panel1.Controls.Add(bmHost);
            split.Panel2.Controls.Add(recentHost);

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(_searchBox);
            Controls.Add(title);
            Controls.Add(_statusLabel);
            _bookmarksList.AccessibleName = L10n.T("书签列表");
            _recentList.AccessibleName = L10n.T("最近连接列表");
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }


            // 审计 C1：两个列表各自要有空态 —— 只有书签没记录、只有记录没书签都是常态
            _emptyBookmarks = EmptyStateView.Attach(bmHost, L10n.T("还没有书签"),
                L10n.T("把常用会话收进书签，下次按名称/标签就能搜到"),
                L10n.T("添加书签"), (s2, e2) => OnAddBookmark(s2, e2));
            _emptyRecent = EmptyStateView.Attach(recentHost, L10n.T("还没有连接记录"),
                L10n.T("连过的会话会按时间倒序留在这里，成功/失败一目了然"));

            // 布局顺序：Bottom 先加会被挤；用 Resume 后设 splitter
            split.SplitterDistance = 220;
        }

        private static ListView CreateListView(string name)
        {
            return new ListView
            {
                Name = name,
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = Services.FormFontPolicy.UiFont()
            };
        }

        private AntdUI.Button MakeButton(string text)
        {
            return new AntdUI.Button
            {
                Text = text,
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(2),
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3))
            };
        }

        public void Reload()
        {
            FilterBookmarks();
            LoadRecent();
        }

        private void FilterBookmarks()
        {
            using (BusyScope.Begin(this, L10n.T("正在读取书签…"), false))
            {
            _bookmarksList.BeginUpdate();
            _bookmarksList.Items.Clear();
            try
            {
                var q = (_searchBox.Text ?? "").Trim();
                var all = _bookmarkStore.LoadAll() ?? new SessionBookmark[0];
                var ordered = all
                    .OrderByDescending(b => b.IsFavorite)
                    .ThenByDescending(b => b.ConnectCount)
                    .ThenBy(b => b.Name ?? "");
                var rows = new List<ListViewItem>();
                foreach (var b in ordered)
                {
                    if (!string.IsNullOrEmpty(q))
                    {
                        var hay = ((b.Name ?? "") + " " + (b.Tags ?? "") + " " + (b.ConnectionId ?? "")).ToLowerInvariant();
                        if (hay.IndexOf(q.ToLowerInvariant(), StringComparison.Ordinal) < 0)
                            continue;
                    }
                    var item = new ListViewItem(b.Name ?? L10n.T("(未命名)"))
                    {
                        Tag = b,
                        ForeColor = b.IsFavorite ? GdtermColorTable.Warning : GdtermColorTable.Foreground
                    };
                    item.SubItems.Add(b.ConnectionId ?? "");
                    item.SubItems.Add(b.Tags ?? "");
                    item.SubItems.Add(b.ConnectCount.ToString());
                    rows.Add(item);
                }
                _shellBookmarks.SetItems(rows);
                _statusLabel.Text = L10n.T("书签 ") + _shellBookmarks.AllItems.Count + L10n.T(" 条");
            }
            catch (Exception ex)
            {
                _statusLabel.Text = L10n.T("加载失败: ") + ex.Message;
                try { DiagLog.Error("SessionBookmarksPanel.Filter", ex); } catch (Exception) { }
                ToastNotifier.Error(L10n.T("书签加载失败：") + ex.Message);
            }
            finally
            {
                _bookmarksList.EndUpdate();
                if (_emptyBookmarks != null)
                {
                    // 「筛选后为空」与「一条书签都没有」是两件事：前者该提示换关键字，后者该引导添加
                    bool hasAny = false;
                    try
                    {
                        var raw = _bookmarkStore.LoadAll();
                        hasAny = raw != null && raw.Count > 0;
                    }
                    catch (Exception) { }
                    _emptyBookmarks.SetEmpty(_shellBookmarks.AllItems.Count == 0,
                        hasAny ? L10n.T("没有匹配的书签") : null,
                        hasAny ? L10n.T("换个名称/标签再搜，或清空搜索框") : null);
                }
            }
            }
        }

        private void LoadRecent()
        {
            _recentList.BeginUpdate();
            _recentList.Items.Clear();
            try
            {
                var recents = _bookmarkStore.GetRecentConnections(30) ?? new RecentConnection[0];
                var rows = new List<ListViewItem>();
                foreach (var r in recents)
                {
                    var item = new ListViewItem(r.Host ?? r.ConnectionId ?? "")
                    {
                        Tag = r,
                        ForeColor = r.Success ? GdtermColorTable.Success : GdtermColorTable.Danger
                    };
                    item.SubItems.Add(r.Protocol ?? "");
                    item.SubItems.Add(r.ConnectedAt.ToLocalTime().ToString("MM-dd HH:mm"));
                    item.SubItems.Add(r.Success ? "OK" : L10n.T("失败"));
                    rows.Add(item);
                }
                _shellRecent.SetItems(rows);
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("SessionBookmarksPanel.Recent", ex); } catch (Exception) { }
                ToastNotifier.Error(L10n.T("最近连接加载失败：") + ex.Message);
            }
            finally
            {
                _recentList.EndUpdate();
                if (_emptyRecent != null) _emptyRecent.SetEmpty(_shellRecent.AllItems.Count == 0);
            }
        }

        private void OnAddBookmark(object sender, EventArgs e)
        {
            if (_connectionStore == null)
            {
                MessageBox.Show(L10n.T("连接存储不可用"), L10n.T("书签"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var connections = _connectionStore.LoadAll();
            if (connections == null || connections.Count == 0)
            {
                MessageBox.Show(L10n.T("没有可添加的连接"), L10n.T("书签"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = L10n.T("添加书签");
                // ClientSize 在按钮行后按行高动态计算（见下方）
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = GdtermColorTable.Surface;
                dlg.ForeColor = GdtermColorTable.Foreground;
                dlg.Font = Services.FormFontPolicy.UiFont();

                // 字体驱动行高（原先输入框高 24、按钮高 28，在大字号下文字被裁）
                int fieldH = FormFontPolicy.FieldHeight(this);
                int rowH = fieldH + DpiScale.V(this, 12);
                int pad = DpiScale.V(this, 15);
                int y = DpiScale.V(this, 18);
                var lblName = new AntdUI.Label { Text = L10n.T("名称"), ForeColor = GdtermColorTable.Muted, Location = new Point(pad, y), AutoSize = true };
                y += Services.FormFontPolicy.RowStep(this);
                var nameBox = new AntdUI.Input {
                    Location = new Point(pad, y),
                    Size = new Size(DpiScale.V(this, 310), fieldH),
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground
                };
                y += rowH;
                var lblConn = new AntdUI.Label { Text = L10n.T("连接"), ForeColor = GdtermColorTable.Muted, Location = new Point(pad, y), AutoSize = true };
                y += Services.FormFontPolicy.RowStep(this);
                var combo = new AntdUI.Select {
                    Location = new Point(pad, y),
                    Size = new Size(DpiScale.V(this, 310), fieldH),
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground
                };
                foreach (var c in connections)
                    combo.Items.Add(new ConnItem(c));
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                y += rowH + DpiScale.V(this, 6);

                int btnH = Math.Max(DpiScale.V(this, 30), fieldH);
                var ok = new AntdUI.Button {
                    Text = L10n.T("保存"),
                    DialogResult = DialogResult.OK,
                    Location = new Point(pad + DpiScale.V(this, 310) - DpiScale.V(this, 80), y),
                    Size = new Size(DpiScale.V(this, 80), btnH),
                    Type = AntdUI.TTypeMini.Primary,
                };
                dlg.Controls.Add(lblName);
                dlg.Controls.Add(nameBox);
                dlg.Controls.Add(lblConn);
                dlg.Controls.Add(combo);
                dlg.Controls.Add(ok);
                dlg.AcceptButton = ok;
                dlg.ClientSize = new Size(pad * 2 + DpiScale.V(this, 310), y + btnH + DpiScale.V(this, 16));

                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var selected = combo.SelectedValue as ConnItem;
                if (selected == null) return;
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? selected.Config.Name : nameBox.Text.Trim();
                var bm = new SessionBookmark
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    ConnectionId = selected.Config.Id,
                    CreatedAt = DateTime.UtcNow,
                    ConnectCount = 0,
                    IsFavorite = false
                };
                try
                {
                    _bookmarkStore.Add(bm);
                    Reload();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L10n.T("添加失败: ") + ex.Message, L10n.T("书签"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnDeleteBookmark(object sender, EventArgs e)
        {
            var targets = new List<SessionBookmark>();
            foreach (var it in _shellBookmarks.SelectedOrChecked())
            {
                var bm0 = it.Tag as SessionBookmark;
                if (bm0 != null) targets.Add(bm0);
            }
            if (targets.Count == 0) return;
            string ask = targets.Count == 1
                ? L10n.Tf("删除书签「{0}」？", targets[0].Name)
                : L10n.Tf("删除选中的 {0} 个书签？", targets.Count);
            if (MessageBox.Show(ask, L10n.T("确认"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                int ok = 0, fail = 0;
                foreach (var bm1 in targets)
                {
                    try { _bookmarkStore.Delete(bm1.Id); TrashStore.Default.Add("bookmark", L10n.T("书签"), bm1.Name, bm1); ok++; }
                    catch (Exception) { fail++; }
                }
                Reload();
                if (fail == 0) ToastNotifier.Success(L10n.T("已删除 ") + ok + L10n.T(" 个书签"));
                else ToastNotifier.Warning(L10n.T("删除完成：") + ok + L10n.T(" 成功，") + fail + L10n.T(" 失败"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(L10n.T("删除失败: ") + ex.Message, L10n.T("书签"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnToggleFavorite(object sender, EventArgs e)
        {
            if (_bookmarksList.SelectedItems.Count == 0) return;
            var bm = _bookmarksList.SelectedItems[0].Tag as SessionBookmark;
            if (bm == null) return;
            bm.IsFavorite = !bm.IsFavorite;
            try
            {
                _bookmarkStore.Update(bm);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(L10n.T("更新失败: ") + ex.Message, L10n.T("书签"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenSelectedBookmark()
        {
            if (_bookmarksList.SelectedItems.Count == 0) return;
            var bm = _bookmarksList.SelectedItems[0].Tag as SessionBookmark;
            if (bm == null || string.IsNullOrEmpty(bm.ConnectionId)) return;
            var cfg = ResolveConnection(bm.ConnectionId);
            if (cfg == null)
            {
                MessageBox.Show(L10n.T("找不到连接: ") + bm.ConnectionId, L10n.T("书签"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bm.ConnectCount++;
            bm.LastConnectedAt = DateTime.UtcNow;
            try { _bookmarkStore.Update(bm); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SessionBookmarksPanel", exSwallowed); } catch { } }
            OpenConnectionRequested?.Invoke(cfg);
        }

        private void OpenSelectedRecent()
        {
            if (_recentList.SelectedItems.Count == 0) return;
            var r = _recentList.SelectedItems[0].Tag as RecentConnection;
            if (r == null || string.IsNullOrEmpty(r.ConnectionId)) return;
            var cfg = ResolveConnection(r.ConnectionId);
            if (cfg == null)
            {
                MessageBox.Show(L10n.T("找不到连接: ") + r.ConnectionId, L10n.T("最近"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            OpenConnectionRequested?.Invoke(cfg);
        }

        private ConnectionConfig ResolveConnection(string connectionId)
        {
            if (_connectionStore == null || string.IsNullOrEmpty(connectionId)) return null;
            try { return _connectionStore.GetById(connectionId); }
            catch { return null; }
        }

        private sealed class ConnItem
        {
            public ConnectionConfig Config { get; private set; }
            public ConnItem(ConnectionConfig c) { Config = c; }
            public override string ToString()
            {
                return (Config.Name ?? Config.Host ?? Config.Id) + " (" + Config.Protocol + ")";
            }
        }
    }
}
