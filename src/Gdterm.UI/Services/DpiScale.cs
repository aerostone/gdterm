using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// DPI 缩放辅助——手写布局（绝对坐标/固定尺寸）的统一缩放入口。
    ///
    /// 背景：项目内大量窗体未走 Designer，无法依赖 AutoScaleMode 自动缩放；
    /// 在 144dpi(150%) 等环境下出现文字溢出按钮、控件拥挤。
    /// 规范见 docs/UI-SCALING-CONVENTIONS.md。
    /// </summary>
    internal static class DpiScale
    {
        /// <summary>取控件当前 DPI 相对 96 的缩放系数；失败时返回 1。</summary>
        public static float Factor(Control c)
        {
            if (c == null) return 1f;
            try { using (var g = c.CreateGraphics()) return (float)g.DpiX / 96f; }
            catch { return 1f; }
        }

        /// <summary>按控件 DPI 缩放整数值（尺寸、间距等）。</summary>
        public static int V(Control c, int value)
        {
            var f = Factor(c);
            return (int)Math.Round(value * f);
        }

        /// <summary>按给定系数缩放整数值。</summary>
        public static int V(int value, float factor)
        {
            return (int)Math.Round(value * factor);
        }

        /// <summary>按控件 DPI 缩放 Point。</summary>
        public static Point P(Control c, int x, int y)
        {
            var f = Factor(c);
            return new Point((int)Math.Round(x * f), (int)Math.Round(y * f));
        }

        /// <summary>按控件 DPI 缩放 Size。</summary>
        public static Size S(Control c, int w, int h)
        {
            var f = Factor(c);
            return new Size((int)Math.Round(w * f), (int)Math.Round(h * f));
        }
    }
}
