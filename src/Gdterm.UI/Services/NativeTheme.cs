using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 原生 WinForms 控件统一暗色外观扩展（AntdUI 过渡层）。
    /// 背景：侧边板（ToolboxPanel/KeyBindingPanel 等十余个）以 ListView 为核心交互，
    /// AntdUI.Table 的数据绑定模型差异大、重写风险高；按 DESIGN-LANGUAGE「改到哪迁到哪」
    /// 债务策略，本类为这些原生控件提供单点暗色主题入口，替代各文件散落的裸 FromArgb。
    /// 色值一律取 GdtermColorTable 运行时 token，主题切换即时生效（重刷时调用方需重新应用）。
    /// </summary>
    public static class NativeTheme
    {
        /// <summary>应用暗色主题到 ListView（Details 视图列表的标准外观）。</summary>
        public static ListView Dark(this ListView lv)
        {
            if (lv == null) return lv;
            lv.BackColor = GdtermColorTable.Surface;
            lv.ForeColor = GdtermColorTable.Foreground;
            lv.;
            if (lv.View == View.Details)
            {
                lv.OwnerDraw = false;
                // 表头色通过 OwnerDraw 才可控；默认保持系统表头，避免复杂绘制破坏排序交互
            }
            return lv;
        }

        /// <summary>应用暗色主题到 ListBox。</summary>
        public static ListBox Dark(this ListBox lb)
        {
            if (lb == null) return lb;
            lb.BackColor = GdtermColorTable.Surface;
            lb.ForeColor = GdtermColorTable.Foreground;
            lb.;
            return lb;
        }

        /// <summary>应用暗色主题到 TreeView。</summary>
        public static TreeView Dark(this TreeView tv)
        {
            if (tv == null) return tv;
            tv.BackColor = GdtermColorTable.Surface;
            tv.ForeColor = GdtermColorTable.Foreground;
            tv.;
            return tv;
        }

        /// <summary>应用暗色主题到单行 TextBox（多行编辑框同样适用）。</summary>
        public static TextBox Dark(this TextBox tb)
        {
            if (tb == null) return tb;
            tb.BackColor = GdtermColorTable.Surface;
            tb.ForeColor = GdtermColorTable.Foreground;
            tb.;
            return tb;
        }

        /// <summary>应用暗色主题到 ComboBox（DropDownList 与可编辑均适用）。</summary>
        public static ComboBox Dark(this ComboBox cb)
        {
            if (cb == null) return cb;
            cb.BackColor = GdtermColorTable.Surface;
            cb.ForeColor = GdtermColorTable.Foreground;
            cb.;
            return cb;
        }

        /// <summary>应用暗色主题到按钮（Surface 底 + Border 描边；主按钮请用 AntdUI）。</summary>
        public static Button Dark(this Button btn)
        {
            if (btn == null) return btn;
            btn.;
            btn.BackColor = GdtermColorTable.Surface;
            btn.ForeColor = GdtermColorTable.Foreground;
            return btn;
        }

        /// <summary>主操作按钮（Accent 实底 + 深色文字，替代裸 0,122,204 品牌蓝）。</summary>
        public static Button DarkPrimary(this Button btn)
        {
            if (btn == null) return btn;
            btn.;
            btn.BackColor = GdtermColorTable.Accent;
            btn.ForeColor = Color.FromArgb(0x0D, 0x11, 0x17);
            return btn;
        }

        /// <summary>危险操作按钮（Danger 实底 + 白字，删除/断开场景）。</summary>
        public static Button DarkDanger(this Button btn)
        {
            if (btn == null) return btn;
            btn.;
            btn.BackColor = GdtermColorTable.Danger;
            btn.ForeColor = Color.White;
            return btn;
        }

        /// <summary>批量应用暗色主题到一组控件（递归容器内全部匹配类型）。</summary>
        public static void DarkRecursive(Control root)
        {
            if (root == null) return;
            var stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                var lv = c as ListView; if (lv != null) { lv.Dark(); }
                else { var lb = c as ListBox; if (lb != null) { lb.Dark(); }
                else { var tv = c as TreeView; if (tv != null) { tv.Dark(); }
                else { var tb = c as TextBox; if (tb != null) { tb.Dark(); }
                else { var cb = c as ComboBox; if (cb != null) { cb.Dark(); } } } } }
                foreach (Control child in c.Controls) stack.Push(child);
            }
        }
    }
}
