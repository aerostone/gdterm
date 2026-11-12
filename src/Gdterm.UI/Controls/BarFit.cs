using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 工具条（过滤框 + 排序 + 动作按钮的 FlowLayoutPanel）的自适应收口。
    ///
    /// 为什么需要它（L2 矩阵第二批目标实证，2026-11-07）：
    /// d150 + 14pt 下，工具条里的 Select 是 192px、按钮是 79×57，而窄侧栏/左分栏只有 168px ——
    /// 单个子件就比容器宽，`WrapContents` 也换不了行，于是子件直接画到父边界外。
    /// 再叠上 `Dock=Top + AutoSize` 按旧宽度算 PreferredSize 的老毛病，工具条高度也不会跟着换行长高，
    /// 第二行整排压在表格上。
    ///
    /// 这里做三件事，顺序不能变：
    ///   1. **收宽**：每个子件宽度收到「可用宽 − 外边距」，但记住它原本想要的宽度（窗口变宽要还回去）；
    ///   2. **量高**：按**当前**宽度问 PreferredSize，得到真实行高；
    ///   3. **让位**：宿主太矮时先隐藏可省子件（过滤/排序/计数），还放不下就整条收起 ——
    ///      宁可不显示工具条，也不能把它画到表格上。
    /// </summary>
    internal sealed class BarFit
    {
        private readonly FlowLayoutPanel _bar;
        private readonly Dictionary<Control, int> _wantWidth = new Dictionary<Control, int>();
        private readonly List<Control> _optional = new List<Control>();

        public BarFit(FlowLayoutPanel bar)
        {
            _bar = bar;
        }

        /// <summary>登记子件的期望宽度；可选子件在拥挤时会被隐藏。</summary>
        public void Record(Control c, bool optional = false)
        {
            if (c == null) return;
            _wantWidth[c] = c.Width;
            if (optional && !_optional.Contains(c)) _optional.Add(c);
        }

        public int Fit(int availWidth)
        {
            if (_bar == null || availWidth <= 0) return 0;
            foreach (var kv in _wantWidth)
            {
                Control c = kv.Key;
                if (c == null || c.Parent == null) continue;
                int room = Math.Max(1, availWidth - c.Margin.Horizontal);
                int want = kv.Value > 0 ? kv.Value : c.Width;
                int w = Math.Min(want, room);
                if (c.Width != w) c.Width = w;
            }
            _bar.PerformLayout();
            return PreferredHeight(availWidth);
        }

        public int PreferredHeight(int availWidth)
        {
            if (_bar == null || availWidth <= 0) return 0;
            try { return _bar.GetPreferredSize(new Size(availWidth, 0)).Height; }
            catch (Exception) { return 0; }
        }

        /// <summary>隐藏/恢复可省子件，返回隐藏后是否还有可省子件未收起（用于逐级尝试）。</summary>
        public bool HideOneOptional()
        {
            for (int i = 0; i < _optional.Count; i++)
            {
                Control c = _optional[i];
                if (c != null && c.Parent != null && c.Visible)
                {
                    c.Visible = false;
                    _bar.PerformLayout();
                    return true;
                }
            }
            return false;
        }

        /// <summary>恢复全部可省子件（宿主变宽/变高后回到完整工具条）。</summary>
        public void RestoreOptional()
        {
            bool changed = false;
            foreach (Control c in _optional)
            {
                if (c != null && c.Parent != null && !c.Visible) { c.Visible = true; changed = true; }
            }
            if (changed) _bar.PerformLayout();
        }
    }
}
