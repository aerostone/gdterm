using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;

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
        private TextBox _searchBox;
        private Label _statusLabel;

        /// <summary>请求打开连接（ConnectionId）</summary>
        public event Action<ConnectionConfig> OpenConnectionRequested;

        public SessionBookmarksPanel(IBookmarkStore bookmarkStore, IConnectionStore connectionStore)
        {
            _bookmarkStore = bookmarkStore ?? throw new ArgumentNullException(nameof(bookmarkStore));
            _connectionStore = connectionStore;
            InitializeComponent();
            Reload();
        }

        private void InitializeComponent()
        {
            BackColor = Color.FromArgb(30, 30, 30);
            Dock = DockStyle.Fill;

            var title = new Label
            {
                Text = "书签 / 最近连接",
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            _searchBox = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 24,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            _searchBox.TextChanged += (s, e) => FilterBookmarks();

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(35, 35, 35),
                Padding = new Padding(4, 2, 4, 2)
            };
            var addBtn = MakeButton("添加书签");
            addBtn.Click += OnAddBookmark;
            var delBtn = MakeButton("删除");
            delBtn.Click += OnDeleteBookmark;
            var favBtn = MakeButton("收藏/取消");
            favBtn.Click += OnToggleFavorite;
            var refreshBtn = MakeButton("刷新");
            refreshBtn.Click += (s, e) => Reload();
            toolbar.Controls.AddRange(new Control[] { addBtn, delBtn, favBtn, refreshBtn });

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = new Font("Microsoft YaHei", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0)
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                BackColor = Color.FromArgb(30, 30, 30),
                SplitterWidth = 4
            };

            _bookmarksList = CreateListView("书签");
            _bookmarksList.Columns.Add("名称", 140);
            _bookmarksList.Columns.Add("连接", 100);
            _bookmarksList.Columns.Add("标签", 80);
            _bookmarksList.Columns.Add("次数", 50);
            _bookmarksList.DoubleClick += (s, e) => OpenSelectedBookmark();
            _bookmarksList.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) OpenSelectedBookmark();
                if (e.KeyCode == Keys.Delete) OnDeleteBookmark(s, e);
            };

            var bmHost = new Panel { Dock = DockStyle.Fill };
            var bmLabel = new Label
            {
                Text = "收藏书签",
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.FromArgb(180, 180, 180),
                Padding = new Padding(6, 2, 0, 0)
            };
            bmHost.Controls.Add(_bookmarksList);
            bmHost.Controls.Add(bmLabel);

            _recentList = CreateListView("最近");
            _recentList.Columns.Add("主机", 140);
            _recentList.Columns.Add("协议", 60);
            _recentList.Columns.Add("时间", 120);
            _recentList.Columns.Add("结果", 50);
            _recentList.DoubleClick += (s, e) => OpenSelectedRecent();

            var recentHost = new Panel { Dock = DockStyle.Fill };
            var recentLabel = new Label
            {
                Text = "最近连接",
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.FromArgb(180, 180, 180),
                Padding = new Padding(6, 2, 0, 0)
            };
            recentHost.Controls.Add(_recentList);
            recentHost.Controls.Add(recentLabel);

            split.Panel1.Controls.Add(bmHost);
            split.Panel2.Controls.Add(recentHost);

            Controls.Add(split);
            Controls.Add(toolbar);
            Controls.Add(_searchBox);
            Controls.Add(title);
            Controls.Add(_statusLabel);

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
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = new Font("Microsoft YaHei", 9f)
            };
        }

        private static Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Margin = new Padding(2),
                Height = 26
            };
        }

        public void Reload()
        {
            FilterBookmarks();
            LoadRecent();
        }

        private void FilterBookmarks()
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
                foreach (var b in ordered)
                {
                    if (!string.IsNullOrEmpty(q))
                    {
                        var hay = ((b.Name ?? "") + " " + (b.Tags ?? "") + " " + (b.ConnectionId ?? "")).ToLowerInvariant();
                        if (hay.IndexOf(q.ToLowerInvariant(), StringComparison.Ordinal) < 0)
                            continue;
                    }
                    var item = new ListViewItem(b.Name ?? "(未命名)")
                    {
                        Tag = b,
                        ForeColor = b.IsFavorite ? Color.FromArgb(255, 200, 80) : Color.White
                    };
                    item.SubItems.Add(b.ConnectionId ?? "");
                    item.SubItems.Add(b.Tags ?? "");
                    item.SubItems.Add(b.ConnectCount.ToString());
                    _bookmarksList.Items.Add(item);
                }
                _statusLabel.Text = "书签 " + _bookmarksList.Items.Count + " 条";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "加载失败: " + ex.Message;
            }
            finally
            {
                _bookmarksList.EndUpdate();
            }
        }

        private void LoadRecent()
        {
            _recentList.BeginUpdate();
            _recentList.Items.Clear();
            try
            {
                var recents = _bookmarkStore.GetRecentConnections(30) ?? new RecentConnection[0];
                foreach (var r in recents)
                {
                    var item = new ListViewItem(r.Host ?? r.ConnectionId ?? "")
                    {
                        Tag = r,
                        ForeColor = r.Success ? Color.FromArgb(120, 200, 120) : Color.FromArgb(220, 120, 120)
                    };
                    item.SubItems.Add(r.Protocol ?? "");
                    item.SubItems.Add(r.ConnectedAt.ToLocalTime().ToString("MM-dd HH:mm"));
                    item.SubItems.Add(r.Success ? "OK" : "失败");
                    _recentList.Items.Add(item);
                }
            }
            catch { }
            finally
            {
                _recentList.EndUpdate();
            }
        }

        private void OnAddBookmark(object sender, EventArgs e)
        {
            if (_connectionStore == null)
            {
                MessageBox.Show("连接存储不可用", "书签", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var connections = _connectionStore.LoadAll();
            if (connections == null || connections.Count == 0)
            {
                MessageBox.Show("没有可添加的连接", "书签", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new Form())
            {
                dlg.Text = "添加书签";
                dlg.Size = new Size(360, 200);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = Color.FromArgb(35, 35, 35);

                var nameBox = new TextBox
                {
                    Location = new Point(15, 20),
                    Size = new Size(310, 24),
                    BackColor = Color.FromArgb(50, 50, 50),
                    ForeColor = Color.White
                };
                var combo = new ComboBox
                {
                    Location = new Point(15, 55),
                    Size = new Size(310, 24),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(50, 50, 50),
                    ForeColor = Color.White
                };
                foreach (var c in connections)
                    combo.Items.Add(new ConnItem(c));
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;

                var ok = new Button
                {
                    Text = "确定",
                    DialogResult = DialogResult.OK,
                    Location = new Point(245, 110),
                    Size = new Size(80, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0, 122, 204),
                    ForeColor = Color.White
                };
                dlg.Controls.Add(new Label { Text = "名称", ForeColor = Color.Silver, Location = new Point(15, 4), AutoSize = true });
                dlg.Controls.Add(nameBox);
                dlg.Controls.Add(new Label { Text = "连接", ForeColor = Color.Silver, Location = new Point(15, 40), AutoSize = true });
                dlg.Controls.Add(combo);
                dlg.Controls.Add(ok);
                dlg.AcceptButton = ok;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var selected = combo.SelectedItem as ConnItem;
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
                    MessageBox.Show("添加失败: " + ex.Message, "书签", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnDeleteBookmark(object sender, EventArgs e)
        {
            if (_bookmarksList.SelectedItems.Count == 0) return;
            var bm = _bookmarksList.SelectedItems[0].Tag as SessionBookmark;
            if (bm == null) return;
            if (MessageBox.Show("删除书签「" + bm.Name + "」？", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                _bookmarkStore.Delete(bm.Id);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除失败: " + ex.Message, "书签", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show("更新失败: " + ex.Message, "书签", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show("找不到连接: " + bm.ConnectionId, "书签",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bm.ConnectCount++;
            bm.LastConnectedAt = DateTime.UtcNow;
            try { _bookmarkStore.Update(bm); } catch { }
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
                MessageBox.Show("找不到连接: " + r.ConnectionId, "最近",
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
