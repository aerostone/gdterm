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
        /// <summary>
        /// 测试钩子（L2 矩阵门，docs/UI-UX-VERIFICATION.md §4.2）：强制 Factor 返回给定系数，
        /// 让 CI 在没有高 DPI 显示器（GHA windows-latest 恒 96dpi）时也能跑 125%/150% 的手写布局。
        /// 传 &lt;=0 清除。产品代码从不调用。
        ///
        /// 口径限制（诚实记录，不要当成“真实高 DPI 已验证”）：本钩子只覆盖<b>我们自己的 DpiScale 数学</b>
        /// （Padding/尺寸/间距）。WinForms 的 AutoScaleMode 与 PerMonitorV2 系统缩放仍由真实
        /// WM_DPICHANGED 驱动，本钩子注入不了 —— 所以矩阵门同时跑字号轴（真实字体度量，会真的
        /// 触发 TextRenderer 行走），字体轴那一半是真实环境，DPI 轴那一半是受控近似。
        /// </summary>
        private static float s_overrideFactor;

        /// <summary>测试专用：固定 DPI 系数（&lt;=0 清除）。见字段注释的口径限制。</summary>
        public static void SetOverrideForTests(float factor)
        {
            s_overrideFactor = factor > 0f ? factor : 0f;
        }

        /// <summary>当前是否处于测试覆盖态（诊断/dump 用）。</summary>
        public static bool HasOverride { get { return s_overrideFactor > 0f; } }

        /// <summary>取控件当前 DPI 相对 96 的缩放系数；失败时返回 1。</summary>
        public static float Factor(Control c)
        {
            if (s_overrideFactor > 0f) return s_overrideFactor;
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

        /// <summary>按控件 DPI 缩放四边 Padding/Margin（缩放规则 4d：禁止裸像素 Padding）。</summary>
        public static Padding Pad(Control c, int left, int top, int right, int bottom)
        {
            var f = Factor(c);
            return new Padding(
                (int)Math.Round(left * f), (int)Math.Round(top * f),
                (int)Math.Round(right * f), (int)Math.Round(bottom * f));
        }
    }
}
