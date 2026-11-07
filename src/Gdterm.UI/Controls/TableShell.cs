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
    /// AntdUI.Table 版列表壳：过滤 + 排序 + 导出。
    ///
    /// 为什么和 <see cref="ListShell"/> 分开：ListView 是「子项集合」，表是「DataSource + 行号映射」。
    /// 表页（快捷键绑定/传输中心/危险命令规则）都靠 SelectedIndex-1 反查自己的行数组，
    /// 一旦过滤/排序改了行的顺序，映射必须同步——所以壳在每次换视图后回调 ViewChanged，
    /// 由页面把「可见顺序」写回自己的映射数组（壳不碰页面的业务集合）。
    /// </summary>
    public sealed class TableShell<T> : Panel where T : class
    {
        private const string DefaultOrder = "默认顺序";

        private readonly AntdUI.Table _table;
        private readonly List<T> _all = new List<T>();
        private readonly List<T> _shown = new List<T>();
        private readonly Func<T, string> _filterText;
        private readonly Func<T, int, string> _cellText;
        private readonly string[] _columns;
        private readonly FlowLayoutPanel _bar;
        private readonly AntdUI.Label _countLabel;

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
            // 壳是透明包裹层：关掉 UserControl 默认 Fixed3D 2px 边框，否则被包表格相对列 Panel
            // 会多缩进 2px（左列无壳、右列有壳 → 两列左缘错位，ScannerCenter 左缘对齐断言即崩）
            BorderStyle = BorderStyle.None;
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
                PlaceholderText = "过滤当前表格…",
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

            bool filtered = q.Length > 0;
            _countLabel.Text = !filtered && _shown.Count == _all.Count
                ? (_all.Count == 0 ? string.Empty : "共 " + _all.Count + " 条")
                : _shown.Count + " / " + _all.Count + " 条";

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
                ToastNotifier.Warning("没有可导出的行");
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
                    ToastNotifier.Success("已导出 " + _shown.Count + " 行到 " + Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    try { DiagLog.Error("TableShell.ExportCsv", ex); } catch (Exception) { }
                    ToastNotifier.Error("导出失败：" + ex.Message);
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
