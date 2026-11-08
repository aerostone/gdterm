using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// AntdUI.Table 多选层。
    ///
    /// 为什么需要它：AntdUI.Table 本身只有单选（SelectedIndex + CellClick，
    /// 无 MultiSelect 属性、无勾选列、无行自绘钩子——二进制 API 实证），
    /// 列表页的「删选中/批量」需求（F7 先例在 ListView 上用 MultiSelect=true）
    /// 在表页没有控件层支持。本层在 CellClick 上用 Ctrl/Shift 组合键自持选中集：
    /// - 普通点击 = 清空后单选该行为锚点（与原生单选手感一致）；
    /// - Ctrl+点击 = 切换该行；
    /// - Shift+点击 = 锚点→该行范围全选。
    /// 行号以数据行为 0 基（AntdUI 的 RowIndex 含表头，已减 1）。
    /// 过滤/排序会重排行序使索引失效，宿主须在换视图后调 <see cref="Reset"/>。
    /// </summary>
    public sealed class TableMultiSelect
    {
        private readonly AntdUI.Table _table;
        private readonly HashSet<int> _set = new HashSet<int>();
        private int _anchor = -1;

        /// <summary>选中集变化（宿主刷新按钮可用性/计数行）。Reset 清空非空集时同样触发。</summary>
        public event Action Changed;

        public int Count { get { return _set.Count; } }

        public bool Contains(int dataRow) { return _set.Contains(dataRow); }

        /// <summary>选中行（数据行 0 基索引，升序）。</summary>
        public int[] SelectedIndices
        {
            get
            {
                var a = new int[_set.Count];
                _set.CopyTo(a);
                Array.Sort(a);
                return a;
            }
        }

        public TableMultiSelect(AntdUI.Table table)
        {
            if (table == null) throw new ArgumentNullException("table");
            _table = table;
            _table.CellClick += OnCellClick;
        }

        private void OnCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            // AntdUI.Table 行号 0 = 表头，数据行从 1 起（与 SelectedIndex-1 同约定）
            int idx = (e != null ? e.RowIndex : 0) - 1;
            if (idx < 0) return;

            var mods = Control.ModifierKeys;
            if (mods == Keys.Control)
            {
                if (!_set.Remove(idx)) _set.Add(idx);
                _anchor = idx;
            }
            else if (mods == Keys.Shift && _anchor >= 0)
            {
                int lo = Math.Min(_anchor, idx), hi = Math.Max(_anchor, idx);
                for (int i = lo; i <= hi; i++) _set.Add(i);
            }
            else
            {
                _set.Clear();
                _set.Add(idx);
                _anchor = idx;
            }
            Raise();
        }

        /// <summary>过滤/排序换视图或数据重建后清掉（索引引用已失效，清空优于错指）。</summary>
        public void Reset()
        {
            if (_set.Count == 0) { _anchor = -1; return; }
            _set.Clear();
            _anchor = -1;
            Raise();
        }

        private void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }
    }
}
