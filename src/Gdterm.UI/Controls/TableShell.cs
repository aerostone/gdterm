using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// AntdUI.Table 版列表壳：过滤 + 排序 + 导出。
    ///
    /// 为什么和 <see cref="ListShell"/> 分开：ListView 是「子项集合」，表是「DataSource + 行号映射」。
    /// 表页（快捷键绑定/传输中心/危险命令规则）都靠 SelectedIndex-1 反查自己的行数组，
    /// 一旦过滤/排序改了行的顺序，映射必须同步——所以壳在每次换视图后回调 ViewChanged，
    /// 由页面把「可见顺序」写回自己的映射数组（壳不碰页面的业务集合）。
    /// </summary>
    public sealed class TableShell<T> : Panel where T : class
    {
        private static string DefaultOrder => L10n.T("默认顺序");

        private readonly AntdUI.Table _table;
        private readonly List<T> _all = new List<T>();
        private readonly List<T> _shown = new List<T>();
        private readonly Func<T, string> _filterText;
        private readonly Func<T, int, string> _cellText;
        private readonly string[] _columns;
        private readonly FlowLayoutPanel _bar;
        private readonly AntdUI.Label _countLabel;
        private TableMultiSelect _multi;

        public AntdUI.Input FilterBox { get; private set; }
        public AntdUI.Select SortSelect { get; private set; }
        public AntdUI.Button SortDirection { get; private set; }

        /// <summary>可见顺序变化（页面据此同步 SelectedIndex → 行对象的映射）。</summary>
        public event Action<IList<T>> ViewChanged;

        public TableShell(AntdUI.Table table, string[] columns, Func<T, string> filterText,
            Func<T, int, string> cellText, bool showFilter = true)
        {
            if (table == null) throw new ArgumentNullException("table");
            _table = table;
            _columns = columns ?? new string[0];
            _filterText = filterText;
            _cellText = cellText;

            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            int fieldH = FormFontPolicy.FieldHeight(this);
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
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };

            FilterBox = new AntdUI.Input
            {
                Name = "TableShellFilter",
                PlaceholderText = L10n.T("过滤当前表格…"),
                AllowClear = true,
                MinimumSize = new Size(DpiScale.V(this, 120), fieldH),
                Width = DpiScale.V(this, 168),
                Margin = new Padding(0, 0, DpiScale.V(this, 6), 0)
            };
            FilterBox.TextChanged += (s, e) => Apply();
            FilterBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                FilterBox.Text = string.Empty;
                e.SuppressKeyPress = true;
            };
            if (showFilter) _bar.Controls.Add(FilterBox);

            SortSelect = new AntdUI.Select
            {
                Name = "TableShellSort",
                Width = DpiScale.V(this, 128),
                Height = fieldH,
                Margin = new Padding(0, 0, DpiScale.V(this, 4), 0)
            };
            SortSelect.Items.Add(DefaultOrder);
            foreach (var c in _columns) SortSelect.Items.Add(c);
            SortSelect.SelectedIndex = 0;
            SortSelect.SelectedIndexChanged += (s, e) => Apply();
            _bar.Controls.Add(SortSelect);

            SortDirection = new AntdUI.Button
            {
                Name = "TableShellSortDirection",
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
                Name = "TableShellCount",
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Font = FormFontPolicy.UiFont(-0.5f),
                Margin = new Padding(0, DpiScale.V(this, 6), 0, 0)
            };
            _bar.Controls.Add(_countLabel);

            _table.Dock = DockStyle.Fill;
            Controls.Add(_table);
            Controls.Add(_bar);
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            // AntdUI.Tabs 隐藏页机制（CI 0.1.389 dump 实证）：未激活页被置 Bounds(0,0,0,0)，
            // 壳随之 0×0；AutoSize 工具条保持 38 高、其子件按宽 0 折行叠高，而 WinForms 子窗
            // 不裁剪父边界——工具条整条画到宿主左上角（叠在 tab 头上），tree-check 的越界
            // 规则正是抓到了这个真实渲染泄漏。零面积时隐藏工具条，页面激活获得真实尺寸后恢复。
            bool hasArea = ClientSize.Width > 0 && ClientSize.Height > 0;
            if (_bar.Visible != hasArea) _bar.Visible = hasArea;
            base.OnClientSizeChanged(e);
        }

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

        public IList<T> AllItems { get { return _all; } }
        public IList<T> VisibleItems { get { return _shown; } }

        /// <summary>
        /// 启用多选（AntdUI.Table 本身单选，见 <see cref="TableMultiSelect"/>）。
        /// 选中计数并入计数行；过滤/排序自动清空选中；批量操作用 <see cref="GetSelectedRows"/>。
        /// 返回实例以便页面挂 <see cref="TableMultiSelect.Changed"/>（如批量按钮可用性）。
        /// </summary>
        public TableMultiSelect EnableMultiSelect()
        {
            if (_multi != null) return _multi;
            _multi = new TableMultiSelect(_table);
            _multi.Changed += OnMultiChanged;
            return _multi;
        }

        private void OnMultiChanged() { if (_countLabel != null) UpdateCountLabel(); }

        private void UpdateCountLabel()
        {
            if (_countLabel == null) return;
            bool filtered = (FilterBox != null && (FilterBox.Text ?? string.Empty).Trim().Length > 0);
            _countLabel.Text = !filtered && _shown.Count == _all.Count
                ? (_all.Count == 0 ? string.Empty : L10n.T("共 ") + _all.Count + L10n.T(" 条"))
                : _shown.Count + " / " + _all.Count + L10n.T(" 条");
            if (_multi != null && _multi.Count > 0)
                _countLabel.Text = (string.IsNullOrEmpty(_countLabel.Text) ? string.Empty : _countLabel.Text + "  ")
                    + L10n.Tf("已选中 {0}", _multi.Count);
        }

        /// <summary>当前选中的行（可见顺序，未选中返回空）。过滤生效时即选中的可见子集。</summary>
        public IList<T> GetSelectedRows()
        {
            var result = new List<T>();
            if (_multi == null) return result;
            foreach (int i in _multi.SelectedIndices)
            {
                if (i >= 0 && i < _shown.Count) result.Add(_shown[i]);
            }
            return result;
        }

        public void SetItems(IEnumerable<T> items)
        {
            _all.Clear();
            if (items != null)
                foreach (var it in items)
                    if (it != null) _all.Add(it);
            Apply();
        }

        public void Refresh() { Apply(); }

        public string FilterText
        {
            get { return FilterBox.Text ?? string.Empty; }
            set { FilterBox.Text = value ?? string.Empty; }
        }

        private void Apply()
        {
            string q = (FilterBox.Text ?? string.Empty).Trim();
            _shown.Clear();
            for (int i = 0; i < _all.Count; i++)
            {
                if (q.Length == 0 || Matches(_all[i], q)) _shown.Add(_all[i]);
            }

            int col = SortSelect.SelectedIndex - 1;
            if (col >= 0 && col < _columns.Length && _cellText != null)
            {
                bool desc = SortDirection.Text == "↓";
                _shown.Sort((a, b) =>
                {
                    int r = ListShell.NaturalCompare(_cellText(a, col) ?? string.Empty, _cellText(b, col) ?? string.Empty);
                    return desc ? -r : r;
                });
            }

            _table.DataSource = _shown.ToArray();
            SortDirection.Visible = col >= 0;

            // 过滤/排序重排行序，多选的索引引用失效——清空优于错指
            if (_multi != null) _multi.Reset();
            UpdateCountLabel();

            var h = ViewChanged;
            if (h != null) h(_shown);
        }

        private bool Matches(T item, string q)
        {
            if (_filterText != null)
                return (_filterText(item) ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            if (_cellText == null) return true;
            for (int i = 0; i < _columns.Length; i++)
            {
                var s = _cellText(item, i) ?? string.Empty;
                if (s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>导出可见行（表没有勾选模型，导出即"当前这屏"）。</summary>
        public void ExportCsv(IWin32Window owner, string baseName)
        {
            if (_shown.Count == 0)
            {
                ToastNotifier.Warning(L10n.T("没有可导出的行"));
                return;
            }
            using (var dlg = new SaveFileDialog
            {
                Title = L10n.T("导出为 CSV"),
                Filter = "CSV (*.csv)|*.csv",
                FileName = string.IsNullOrEmpty(baseName) ? "export" : baseName,
                DefaultExt = "csv"
            })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                try
                {
                    var sb = new StringBuilder();
                    sb.Append(string.Join(",", _columns));
                    sb.Append("\r\n");
                    foreach (var row in _shown)
                    {
                        var cells = new string[_columns.Length];
                        for (int i = 0; i < _columns.Length; i++)
                            cells[i] = Escape(_cellText != null ? _cellText(row, i) : string.Empty);
                        sb.Append(string.Join(",", cells));
                        sb.Append("\r\n");
                    }
                    using (var sw = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true)))
                        sw.Write(sb.ToString());
                    ToastNotifier.Success(L10n.T("已导出 ") + _shown.Count + L10n.T(" 行到 ") + Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("TableShell.ExportCsv", ex); } catch (Exception) { }
                    ToastNotifier.Error(L10n.T("导出失败：") + ex.Message);
                }
            }
        }

        private static string Escape(string s)
        {
            s = s ?? string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
