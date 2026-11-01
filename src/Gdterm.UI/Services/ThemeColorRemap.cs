using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 运行期主题切换的颜色重贴（UI/UX 审计 2026-10-07 C6）。
    ///
    /// 问题：<c>GdtermColorTable</c> 的值是 <see cref="Color"/> 结构体，构造控件时
    /// <c>BackColor = GdtermColorTable.Background</c> 拷贝的是**当时的快照**；
    /// 切换主题后调用 <c>Refresh()/Invalidate(true)</c> 只会用旧色重画，
    /// 于是出现“切主题只变一半”（菜单/状态栏变了，侧栏面板还停在旧色）。
    ///
    /// 方案：由 <see cref="GdtermColorTable.ThemeChanged"/> 提供「旧色 → 新色」映射，
    /// 本类递归走控件树把命中旧色的 BackColor/ForeColor 换成新色。
    /// 相比“重建整个外壳”，它不丢运行期状态（打开的标签、终端会话、滚动位置）；
    /// 相比“每个控件自己订阅事件”，它只需要接一处，新面板不用改代码。
    ///
    /// 边界（明确不管的部分）：
    ///   - 自绘控件（TerminalControl、ConnectionTreeControl 的画笔）在 OnPaint 里实时读
    ///     token，天然跟随，不需要重贴；
    ///   - 画笔/画刷缓存持有关键色的控件，应自己在 <see cref="RefreshAll"/> 之后
    ///     Invalidate 并重建缓存（本类只改属性，不碰 GDI 对象）；
    ///   - AntdUI 的语义色（Primary/Success…）由 <c>AntdUI.Style</c> 决定，这里额外
    ///     重放一次 SetPrimary。
    /// </summary>
    public static class ThemeColorRemap
    {
        /// <summary>把子树里命中旧色的属性换成新色，返回改写过的控件数。</summary>
        public static int Apply(Control root, IDictionary<Color, Color> map)
        {
            if (root == null || map == null || map.Count == 0) return 0;
            int changed = 0;
            var stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control c;
                try { c = stack.Pop(); } catch { break; }
                if (c == null) continue;
                try
                {
                    if (!c.BackColor.IsEmpty)
                    {
                        var mapped = Map(map, c.BackColor);
                        if (mapped.HasValue && c.BackColor.ToArgb() != mapped.Value.ToArgb())
                        {
                            c.BackColor = mapped.Value;
                            changed++;
                        }
                    }
                    if (!c.ForeColor.IsEmpty)
                    {
                        var mapped = Map(map, c.ForeColor);
                        if (mapped.HasValue && c.ForeColor.ToArgb() != mapped.Value.ToArgb())
                        {
                            c.ForeColor = mapped.Value;
                            changed++;
                        }
                    }
                    // ToolStrip/MenuStrip 的项不是 Control 子级，需要单独走
                    var strip = c as ToolStrip;
                    if (strip != null)
                    {
                        foreach (ToolStripItem item in strip.Items)
                            changed += ApplyItem(map, item);
                    }
                }
                catch { }
                try
                {
                    foreach (Control child in c.Controls) stack.Push(child);
                }
                catch { }
            }
            try { root.Invalidate(true); } catch { }
            return changed;
        }

        private static int ApplyItem(IDictionary<Color, Color> map, ToolStripItem item)
        {
            if (item == null) return 0;
            int changed = 0;
            try
            {
                var b = Map(map, item.BackColor);
                if (b.HasValue && item.BackColor.ToArgb() != b.Value.ToArgb()) { item.BackColor = b.Value; changed++; }
                var f = Map(map, item.ForeColor);
                if (f.HasValue && item.ForeColor.ToArgb() != f.Value.ToArgb()) { item.ForeColor = f.Value; changed++; }
                var d = item as ToolStripDropDownItem;
                if (d != null)
                {
                    foreach (ToolStripItem sub in d.DropDownItems) changed += ApplyItem(map, sub);
                }
            }
            catch { }
            return changed;
        }

        /// <summary>
        /// 精确匹配（含 alpha）优先；未命中再按 RGB 忽略 alpha 匹配一次
        /// （同一 token 在不同控件上可能被写成 alpha=255 或原样赋值）。
        /// </summary>
        private static Color? Map(IDictionary<Color, Color> map, Color value)
        {
            Color hit;
            if (map.TryGetValue(value, out hit)) return hit;
            int rgb = value.ToArgb() & 0x00FFFFFF;
            foreach (var kv in map)
            {
                if ((kv.Key.ToArgb() & 0x00FFFFFF) == rgb) return kv.Value;
            }
            return null;
        }

        /// <summary>
        /// 主题切换的统一收尾：重贴颜色 + 重放 AntdUI 主色 + 全量重绘。
        /// 外壳与独立对话框都可调用。
        /// </summary>
        public static int RefreshAll(Form form, IDictionary<Color, Color> map)
        {
            int changed = 0;
            if (form != null)
            {
                changed = Apply(form, map);
                try { form.Refresh(); } catch { }
            }
            try { AntdUI.Style.SetPrimary(GdtermColorTable.Accent); } catch { }
            return changed;
        }
    }
}
