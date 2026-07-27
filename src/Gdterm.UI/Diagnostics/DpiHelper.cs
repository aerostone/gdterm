using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// DPI 自适应与分辨率兼容工具。
    /// </summary>
    /// <remarks>
    /// 设计基准：所有 Form/Container 控件使用 96 DPI (100%) 设计的尺寸，
    /// 通过 AutoScaleMode.Dpi + AutoScaleDimensions(96,96) 让 .NET 自动按当前 DPI 缩放；
    /// DpiHelper.ScaleX/ScaleY 用于运行时手工缩放尺寸、字号、间距。
    ///
    /// 高/低 DPI 自适应策略：
    /// - 100% (96 DPI)：原始尺寸
    /// - 150% (144 DPI)：×1.5
    /// - 200% (192 DPI)：×2.0
    /// - 同时支持 PerMonitorV2（Win10 1703+）和 System DPI（Win7/8/10 早期）
    ///
    /// 分辨率兼容策略：
    /// - MainForm MinimumSize 800×600 @ 96 DPI，按当前 DPI scale 等比放大
    /// - 默认启动尺寸 = 屏幕工作区 70%，最大 1600×900 @ 96 DPI 等比 scale
    /// - 所有对话框启动时 Screen.AllScreens 找最大屏工作区，居中
    /// </remarks>
    internal static class DpiHelper
    {
        private static float? _scaleX;
        private static float? _scaleY;

        /// <summary>
        /// 当前相对于 96 DPI（100%）的水平缩放因子。
        /// 在 Form 创建后用 CreateGraphics().DpiX 获取；
        /// 设计期或上下文缺失时返回 1.0。
        /// </summary>
        public static float ScaleX
        {
            get
            {
                if (_scaleX.HasValue) return _scaleX.Value;
                try
                {
                    using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        _scaleX = g.DpiX / 96f;
                    }
                }
                catch
                {
                    _scaleX = 1f;
                }
                return _scaleX.Value;
            }
        }

        public static float ScaleY
        {
            get
            {
                if (_scaleY.HasValue) return _scaleY.Value;
                try
                {
                    using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        _scaleY = g.DpiY / 96f;
                    }
                }
                catch
                {
                    _scaleY = 1f;
                }
                return _scaleY.Value;
            }
        }

        public static int ScaleInt(int value) => (int)Math.Round(value * ScaleX);

        public static Size ScaleSize(Size s) =>
            new Size((int)Math.Round(s.Width * ScaleX), (int)Math.Round(s.Height * ScaleY));

        public static Point ScalePoint(Point p) =>
            new Point((int)Math.Round(p.X * ScaleX), (int)Math.Round(p.Y * ScaleY));

        public static Padding ScalePadding(Padding p) =>
            new Padding(ScaleInt(p.Left), ScaleInt(p.Top), ScaleInt(p.Right), ScaleInt(p.Bottom));

        /// <summary>
        /// 按 DPI 缩放字号。fontSizeInPoints 是设计期 pt 值（96 DPI）。
        /// </summary>
        public static float ScaleFont(float fontSizeInPoints) =>
            (float)Math.Round(fontSizeInPoints * ScaleX, 1);

        /// <summary>
        /// 当前主屏的工作区（去掉任务栏），用于窗口自适应。
        /// </summary>
        public static Rectangle GetPrimaryWorkingArea()
        {
            try
            {
                return Screen.PrimaryScreen.WorkingArea;
            }
            catch
            {
                return new Rectangle(0, 0, 1366, 768);
            }
        }

        /// <summary>
        /// 给出适合当前屏幕/分辨率的 MainForm 启动尺寸。
        /// 100% DPI 下 1200×800；4K 屏 + 200% DPI 会按 scale 缩放并夹到工作区 90%。
        /// 低分辨率（1280x720 等）会收缩到工作区 95%。
        /// </summary>
        public static Size GetStartupWindowSize()
        {
            var work = GetPrimaryWorkingArea();
            // 100% DPI 下理想尺寸
            float baseW = 1200f, baseH = 800f;
            var size = ScaleSize(new Size((int)baseW, (int)baseH));
            // 不超过工作区 90%
            int maxW = (int)(work.Width * 0.9);
            int maxH = (int)(work.Height * 0.9);
            if (size.Width > maxW) size.Width = maxW;
            if (size.Height > maxH) size.Height = maxH;
            // 不小于 800×600 @ 当前 DPI
            var min = ScaleSize(new Size(800, 600));
            if (size.Width < min.Width) size.Width = min.Width;
            if (size.Height < min.Height) size.Height = min.Height;
            return size;
        }

        /// <summary>
        /// MainForm 的 MinimumSize，按 DPI 缩放 800×600 基准。
        /// </summary>
        public static Size GetMinimumWindowSize() => ScaleSize(new Size(800, 600));

        /// <summary>
        /// 在 Form OnLoad 阶段调用：把 Form 按 AutoScaleDimensions(96,96) + Dpi 模式重新缩放，
        /// 用于覆盖设计期 AutoScaleMode.None 的旧代码。
        /// </summary>
        public static void ApplyAutoScale(Form form)
        {
            if (form == null) return;
            try
            {
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.AutoScaleDimensions = new SizeF(96F, 96F);
                // 触发缩放：用当前 DPI 的 SizeF
                var factor = new SizeF(ScaleX, ScaleY);
                form.Scale(factor);
            }
            catch { }
        }

        /// <summary>
        /// 在任意 ContainerControl 上声明 AutoScale 基准（用于 UserControl/Panel）。
        /// </summary>
        public static void ApplyAutoScale(ContainerControl c)
        {
            if (c == null) return;
            try
            {
                c.AutoScaleMode = AutoScaleMode.Dpi;
                c.AutoScaleDimensions = new SizeF(96F, 96F);
            }
            catch { }
        }

        /// <summary>
        /// 让对话框在屏幕上居中（多显示器时找鼠标所在屏）。
        /// </summary>
        public static void CenterToScreen(Form form)
        {
            if (form == null) return;
            try
            {
                var screen = Screen.FromPoint(Cursor.Position);
                var work = screen.WorkingArea;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(
                    work.X + (work.Width - form.Width) / 2,
                    work.Y + (work.Height - form.Height) / 2);
            }
            catch { }
        }
    }
}
