using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 列表页共用壳：过滤 + 排序 + 导出（FEATURE-MAP §4「加分项」的收口点）。批量动作由宿主
    /// 用 <see cref="AddAction"/> 挂自己的按钮，壳只负责给它 "当前选中/勾选/可见" 三档取数口。
    ///
    /// 背景：7 个 ListView 列表页（宏录制/命令历史/命令模板/端口转发/敏感扫描/书签/OTP）
    /// 各自都是「一个 ListView + 两个按钮」，同行都有的过滤/排序/批量/导出全靠用户手翻。
    /// 逐页手搓会得到 7 份互不相同的实现，所以这里做成一个壳：
    ///
    ///   var shell = new ListShell(_list);            // _list 是页面原来的 ListView（保持不动）
    ///   Controls.Add(shell);                          // 页面加 shell，不加 _list
    ///   shell.AddAction("导出", (s, e) => shell.ExportCsv(this, "macros"));
    ///   ...
    ///   shell.SetItems(allItems);                     // 原来的 Items.Add 循环 → 收集后一次交给壳
    ///
    /// 壳持有「全量条目」(_all)，过滤/排序只在视图层做，不改宿主数据；条目对象本身被复用，
    /// 所以勾选状态、子项文本在过滤前后都保留。
    /// 排序比较用自然序（"第2步" < "第10步"，纯字典序会反）。
    /// </summary>
    public sealed class ListShell : Panel
    {
        private const string DefaultOrder = "默认顺序";

        private readonly List<ListViewItem> _all = new List<ListViewItem>();
        private readonly FlowLayoutPanel _bar;
        private readonly Func<ListViewItem, string> _textOf;
        private readonly string _filterHint;
        private readonly List<string> _sortKeys = new List<string>();
        private readonly AntdUI.Label _countLabel;

        public ListView List { get; private set; }
        public AntdUI.Input FilterBox { get; private set; }
        public AntdUI.Select SortSelect { get; private set; }
        public AntdUI.Button SortDirection { get; private set; }
        /// <summary>过滤后为空时的引导层（宿主自己的空态只管「一条都没有」）。</summary>
        public EmptyStateView FilterEmpty { get; private set; }

        /// <summary>双击 / 回车激活某条（宿主接管"打开这条"的语义）。</summary>
        public event EventHandler<ListViewItem> ItemActivated;

        /// <param name="showFilter">
        /// false = 页面自己已有搜索框（且搜索走后端查询，如命令历史），壳只提供排序/批量/导出，
        /// 不再塞第二个过滤框（两套过滤同时生效是负数体验）。
        /// </param>
        public ListShell(ListView list, string filterHint = "过滤当前列表…", Func<ListViewItem, string> textOf = null,
            bool showFilter = true)
        {
            if (list == null) throw new ArgumentNullException("list");
            List = list;
            _textOf = textOf;
            _filterHint = filterHint;

            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            Padding = Padding.Empty;

            _bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                // 换行而非溢出：窄侧栏（360px）里过滤框+排序+按钮排不下时，
                // 宁可工具条变两行（Dock=Top + AutoSize 会自然长高），也不能让按钮伸出父边界
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = GdtermColorTable.Background,
                // 行间距交给子控件 Margin，容器不留白（否则窄侧栏里工具条吃掉两行）
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };

            int fieldH = FormFontPolicy.FieldHeight(this);
            FilterBox = new AntdUI.Input
            {
                Name = "ListShellFilter",
                PlaceholderText = _filterHint,
                AllowClear = true,
                MinimumSize = new Size(DpiScale.V(this, 120), fieldH),
                Width = DpiScale.V(this, 168),
                Margin = new Padding(0, 0, DpiScale.V(this, 6), 0)
            };
            FilterBox.TextChanged += (s, e) => Apply();
            FilterBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                FilterBox.Text = string.Empty;   // Esc 只清过滤，不关面板
                e.SuppressKeyPress = true;
            };
            if (showFilter) _bar.Controls.Add(FilterBox);
            else FilterBox.Visible = false;

            SortSelect = new AntdUI.Select
            {
                Name = "ListShellSort",
                Width = DpiScale.V(this, 128),
                Height = fieldH,
                Margin = new Padding(0, 0, DpiScale.V(this, 4), 0)
            };
            SortSelect.Items.Add(DefaultOrder);
            SortSelect.SelectedIndex = 0;
            SortSelect.SelectedIndexChanged += (s, e) => Apply();
            _bar.Controls.Add(SortSelect);

            SortDirection = new AntdUI.Button
            {
                Name = "ListShellSortDirection",
                Text = "↓",
                Type = AntdUI.TTypeMini.Default,
                Width = DpiScale.V(this, 34),
                Height = fieldH,
                Margin = new Padding(0, 0, DpiScale.V(this, 6), 0),
                Visible = false
            };
            SortDirection.Click += (s, e) => { SortDirection.Text = SortDirection.Text == "↓" ? "↑" : "↓"; Apply(); };
            _bar.Controls.Add(SortDirection);

            _countLabel = new AntdUI.Label
            {
                Name = "ListShellCount",
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Font = FormFontPolicy.UiFont(-0.5f),
                Margin = new Padding(0, DpiScale.V(this, 6), DpiScale.V(this, 6), 0)
            };
            _bar.Controls.Add(_countLabel);

            List.Dock = DockStyle.Fill;

            // 过滤后为空：与宿主自己的空态是两件事（"库里没有" vs "这一屏没有"）
            FilterEmpty = EmptyStateView.Attach(this, "没有匹配的条目", "换个关键词，或点右侧按钮清空过滤", "清空过滤",
                (s, e) => FilterBox.Text = string.Empty);
            FilterEmpty.Name = "ListShellFilterEmpty";

            Controls.Add(List);
            Controls.Add(_bar);
            try { FilterEmpty.BringToFront(); } catch (Exception) { }

            // 表头点击排序（ListView 默认 HeaderStyle=Clickable 但没人实现 ColumnClick，
            // 点了毫无反应；这里统一走 SortSelect，保持"一个排序状态"）
            List.ColumnClick += (s, e) =>
            {
                int target = e.Column + 1;
                if (SortSelect.SelectedIndex == target)
                {
                    SortDirection.Text = SortDirection.Text == "↓" ? "↑" : "↓";
                    Apply();
                }
                else
                {
                    SortSelect.SelectedIndex = target;
                }
            };

            List.DoubleClick += (s, e) => RaiseActivated();
            List.KeyDown += OnListKeyDown;
        }

        /// <summary>宿主追加自己的动作按钮（导出 / 批量删除 / 启用…），进入同一行工具条。</summary>
        public AntdUI.Button AddAction(string text, EventHandler onClick, string name = null)
        {
            int fieldH = FormFontPolicy.FieldHeight(this);
            var b = new AntdUI.Button
            {
                Name = name,
                Text = text,
                Type = AntdUI.TTypeMini.Default,
                AutoSize = true,
                Height = fieldH,
                // AutoSize 钮不吃库内默认 sps，Padding 必须显式给，否则文字贴边（ui-tree-check L0 会红）
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3)),
                Margin = new Padding(0, 0, DpiScale.V(this, 4), 0)
            };
            if (onClick != null) b.Click += onClick;
            _bar.Controls.Add(b);
            return b;
        }

        /// <summary>全量条目（宿主重建列表后一次交进来；壳负责过滤/排序后显示）。</summary>
        public IList<ListViewItem> AllItems { get { return _all; } }

        /// <summary>当前可见条目（已过过滤/排序）。</summary>
        public IList<ListViewItem> VisibleItems
        {
            get
            {
                var v = new List<ListViewItem>();
                foreach (ListViewItem it in List.Items) v.Add(it);
                return v;
            }
        }

        /// <summary>选中或勾选的条目；都没有则返回当前可见条目（导出/批量默认作用于"看得见的"）。</summary>
        public IList<ListViewItem> SelectedOrChecked()
        {
            var picked = new List<ListViewItem>();
            foreach (ListViewItem it in List.Items)
            {
                if (it.Selected || it.Checked) picked.Add(it);
            }
            return picked.Count > 0 ? picked : VisibleItems;
        }

        public void SetItems(IEnumerable<ListViewItem> items)
        {
            _all.Clear();
            if (items != null)
            {
                foreach (var it in items)
                    if (it != null) _all.Add(it);
            }
            SyncSortKeys();
            Apply();
        }

        /// <summary>
        /// 追加一条（扫描类页面逐条出结果时用）。
        /// 无排序、无过滤时走 O(1) 尾插；否则退回 Apply（过滤/排序下本来就得重排）。
        /// </summary>
        public void AddItem(ListViewItem item)
        {
            if (item == null) return;
            _all.Add(item);
            if (SortSelect.SelectedIndex <= 0 && (FilterBox.Text ?? string.Empty).Trim().Length == 0)
            {
                List.Items.Add(item);
                UpdateCount();
            }
            else
            {
                Apply();
            }
        }

        /// <summary>宿主就地改过条目文本/勾选后重新应用（不清 _all）。</summary>
        public void Refresh() { Apply(); }

        public string FilterText
        {
            get { return FilterBox.Text ?? string.Empty; }
            set { FilterBox.Text = value ?? string.Empty; }
        }

        private void SyncSortKeys()
        {
            // 列标题由宿主在建壳之后才 Add，所以每次 SetItems 同步一次；
            // 只在列有变化时才重建下拉，避免打断用户已选中的排序键。
            if (List.Columns.Count == 0) return;
            var keys = new List<string>();
            foreach (ColumnHeader c in List.Columns) keys.Add(c.Text ?? string.Empty);
            if (keys.Count == _sortKeys.Count)
            {
                bool same = true;
                for (int i = 0; i < keys.Count; i++)
                    if (keys[i] != _sortKeys[i]) { same = false; break; }
                if (same) return;
            }
            int keep = SortSelect.SelectedIndex;
            _sortKeys.Clear();
            _sortKeys.AddRange(keys);
            SortSelect.Items.Clear();
            SortSelect.Items.Add(DefaultOrder);
            foreach (var k in _sortKeys) SortSelect.Items.Add(k);
            SortSelect.SelectedIndex = keep <= _sortKeys.Count ? Math.Max(0, keep) : 0;
            SortDirection.Visible = SortSelect.SelectedIndex > 0;
        }

        private void Apply()
        {
            if (List.Columns.Count == 0) SyncSortKeys();

            string q = (FilterBox.Text ?? string.Empty).Trim();
            var shown = new List<ListViewItem>(_all.Count);
            for (int i = 0; i < _all.Count; i++)
            {
                if (q.Length == 0 || Matches(_all[i], q)) shown.Add(_all[i]);
            }

            int col = SortSelect.SelectedIndex - 1;   // 0 = 默认顺序
            if (col >= 0 && col < List.Columns.Count)
            {
                bool desc = SortDirection.Text == "↓";
                shown.Sort((a, b) =>
                {
                    int r = NaturalCompare(CellText(a, col), CellText(b, col));
                    return desc ? -r : r;
                });
            }

            // 重排同一批条目对象：勾选/子项/图标都跟着走，只换顺序
            var selected = new HashSet<ListViewItem>();
            foreach (ListViewItem it in List.Items) if (it.Selected) selected.Add(it);
            ListViewItem focused = List.FocusedItem;

            List.BeginUpdate();
            try
            {
                List.Items.Clear();
                List.Items.AddRange(shown.ToArray());
                foreach (ListViewItem it in List.Items) if (selected.Contains(it)) it.Selected = true;
                if (focused != null) { try { focused.Focused = true; focused.EnsureVisible(); } catch (Exception) { } }
            }
            finally
            {
                List.EndUpdate();
            }

            if (SortDirection != null) SortDirection.Visible = col >= 0;
            bool filteredOut = q.Length > 0 && shown.Count == 0 && _all.Count > 0;
            if (FilterEmpty != null) FilterEmpty.SetEmpty(filteredOut);
            UpdateCount();
        }

        private void UpdateCount()
        {
            if (_countLabel == null) return;
            int shown = List.Items.Count;
            bool filtered = (FilterBox.Text ?? string.Empty).Trim().Length > 0;
            _countLabel.Text = !filtered && shown == _all.Count
                ? (_all.Count == 0 ? string.Empty : "共 " + _all.Count + " 条")
                : shown + " / " + _all.Count + " 条";
        }

        private bool Matches(ListViewItem item, string q)
        {
            if (_textOf != null) return IndexOf(_textOf(item), q) >= 0;
            if (IndexOf(item.Text, q) >= 0) return true;
            for (int i = 0; i < item.SubItems.Count; i++)
                if (IndexOf(item.SubItems[i].Text, q) >= 0) return true;
            return false;
        }

        private static int IndexOf(string haystack, string needle)
        {
            return string.IsNullOrEmpty(haystack) ? -1 : haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }

        private static string CellText(ListViewItem item, int col)
        {
            if (item == null) return string.Empty;
            if (col == 0) return item.Text ?? string.Empty;
            return col < item.SubItems.Count ? (item.SubItems[col].Text ?? string.Empty) : string.Empty;
        }

        /// <summary>
        /// 自然序比较：纯数字段按数值比（"mac2" &lt; "mac10"），其余按不区分大小写的字串比。
        /// 时间列（2026-11-06 14:33）因为定宽，字典序恰好等于时间序。
        /// </summary>
        public static int NaturalCompare(string a, string b)
        {
            a = a ?? string.Empty; b = b ?? string.Empty;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                    continue;
                }
                char ca = char.ToUpperInvariant(a[i]), cb = char.ToUpperInvariant(b[j]);
                if (ca != cb) return ca - cb;
                i++; j++;
            }
            return (a.Length - i) - (b.Length - j);
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                RaiseActivated();
                e.SuppressKeyPress = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.F)
            {
                FilterBox.Focus();
                FilterBox.SelectAll();
                e.SuppressKeyPress = true;
                return;
            }
            if (e.Control && e.KeyCode == Keys.E)
            {
                ExportCsv(FindForm(), SuggestedFileName());
                e.SuppressKeyPress = true;
            }
            // 空格在带勾选框的列表里默认只切当前行：补一个"全部可见项勾选/取消"
            if (e.Control && e.KeyCode == Keys.A)
            {
                bool any = false;
                foreach (ListViewItem it in List.Items) if (!it.Checked) { any = true; break; }
                foreach (ListViewItem it in List.Items) it.Checked = any;
                e.SuppressKeyPress = true;
            }
        }

        private string SuggestedFileName()
        {
            return "export";
        }

        private void RaiseActivated()
        {
            var h = ItemActivated;
            if (h == null) return;
            var it = List.FocusedItem;
            if (it == null && List.SelectedItems.Count > 0) it = List.SelectedItems[0];
            if (it != null) h(this, it);
        }

        /// <summary>
        /// 导出当前列表为 CSV（Excel 友好：UTF-8 BOM，逗号/引号/换行都转义）。
        /// 有勾选/选中就导这些，否则导当前可见的整屏。
        /// </summary>
        public void ExportCsv(IWin32Window owner, string baseName)
        {
            var rows = SelectedOrChecked();
            if (rows.Count == 0)
            {
                ToastNotifier.Warning("没有可导出的条目");
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = "导出为 CSV",
                Filter = "CSV (*.csv)|*.csv",
                FileName = string.IsNullOrEmpty(baseName) ? "export" : baseName,
                DefaultExt = "csv"
            })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < List.Columns.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(Escape(List.Columns[i].Text));
                    }
                    sb.Append("\r\n");
                    foreach (var it in rows)
                    {
                        for (int i = 0; i < List.Columns.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append(Escape(CellText(it, i)));
                        }
                        sb.Append("\r\n");
                    }
                    // BOM 不能省：没有它 Excel 会把中文读成乱码（而记事本/VS Code 都正常，最容易误判"没问题"）
                    using (var sw = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                        sw.Write(sb.ToString());
                    ToastNotifier.Success("已导出 " + rows.Count + " 条到 " + Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("ListShell.ExportCsv", ex); } catch (Exception) { }
                    ToastNotifier.Error("导出失败：" + ex.Message);
                }
            }
        }

        private static string Escape(string s)
        {
            s = s ?? string.Empty;
            bool need = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!need) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
