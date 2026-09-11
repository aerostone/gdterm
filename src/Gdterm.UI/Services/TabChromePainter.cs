using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签页绘制与关闭按钮命中测试（finding-10）。
    /// v2 优化（原型验收通过）：
    ///   L1 弹性宽：固定 120px 会在长主机名下硬截断 → 文本测量自适应 90~180px + 省略号；
    ///   L2 关闭钮常驻占位挤占文字 → 仅悬停/选中时绘制，文字可用满整宽；
    ///   选中态 = Surface2 底 + 底部 2px 强调线（对齐 v2 标签页规范）。
    /// OwnerDrawFixed 的 ItemSize 只能给固定值，弹性宽度需要 MeasureItem 阶段
    /// 逐标签测量——WinForms TabControl 不支持，故由本类在 TabContainerControl
    /// 的 OnResize/标签增删时统一计算并写回 ItemSize（单值取当前所有标签的
    /// 加权最大宽度，超出可视宽自动降档，最少 90px）。
    /// </summary>
    public sealed class TabChromePainter
    {
        public const int CloseButtonWidth = 16;
        public const int CloseButtonHeight = 18;
        // × 右边距 18→22：旧值下 × 右沿距标签右沿仅 2px，视觉贴边；
        // 新值右边留 6px，textRightPad 同步，× 不再挤边缘。
        public const int CloseButtonRightMargin = 22;
        public const int CloseButtonTopOffset = 2;

        // L1 弹性宽度档位（设计 px，调用方按 DPI 缩放）
        public const int MinTabWidth = 90;
        public const int MaxTabWidth = 180;

        /// <summary>悬停中的标签索引（-1 无）。由 TabContainerControl 的 MouseMove 维护。</summary>
        public int HoverIndex { get; set; }

        /// <summary>
        /// 计算所有标签的统一宽度（设计 px）：取"最长标签理想宽"与可视宽/标签数中较小者，
        /// 并夹在 [MinTabWidth, MaxTabWidth]。标签很多时自动收窄，避免互相挤压出滚动。
        /// </summary>
        public int ComputeTabWidth(TabControl tabControl, Font font, float dpi)
        {
            if (tabControl == null || font == null) return MinTabWidth;

            int ideal = MinTabWidth;
            try
            {
                var proposed = new Size(int.MaxValue, (int)Math.Round(20 * dpi));
                foreach (TabPage page in tabControl.TabPages)
                {
                    var sz = TextRenderer.MeasureText(page.Text, font, proposed,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    int need = (int)Math.Ceiling(sz.Width / dpi) + 24; // 左 12 + 右 12 内边距（设计 px）
                    if (need > ideal) ideal = need;
                }
            }
            catch { }

            // 可视宽约束：总宽不超过标签行可用宽（留 8px 余量给新增按钮区）
            int avail = (int)Math.Floor(tabControl.ClientRectangle.Width / dpi) - 8;
            int count = Math.Max(1, tabControl.TabPages.Count);
            int fit = avail / count;
            if (fit < MinTabWidth) fit = MinTabWidth; // 太多标签时保底 90，超出由系统裁剪

            int width = Math.Min(Math.Min(ideal, fit), MaxTabWidth);
            return Math.Max(MinTabWidth, width);
        }

        /// <summary>按 DPI 缩放绘制标签页与关闭按钮。</summary>
        public void DrawTab(DrawItemEventArgs e, TabControl tabControl)
        {
            if (e == null || tabControl == null) return;
            if (e.Index < 0 || e.Index >= tabControl.TabPages.Count) return;

            var tab = tabControl.TabPages[e.Index];
            var rect = e.Bounds;
            var dpi = DpiScale.Factor(tabControl);

            bool isSelected = (e.Index == tabControl.SelectedIndex);
            bool isHover = (e.Index == HoverIndex);

            // v2: 未选中透明底（bg 透出）/悬停 Hover/选中 Surface2 + 底部强调线
            Color back;
            if (isSelected) back = GdtermColorTable.Surface2;
            else if (isHover) back = GdtermColorTable.Hover;
            else back = GdtermColorTable.Background;
            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, rect);

            if (isSelected)
            {
                // 底部 2px 强调线（DPI 缩放）
                int h = Math.Max(2, (int)Math.Round(2 * dpi));
                using (var brush = new SolidBrush(GdtermColorTable.Accent))
                    e.Graphics.FillRectangle(brush, rect.Left, rect.Bottom - h, rect.Width, h);
            }

            // L2: 关闭钮仅悬停/选中时绘制；文字先用满宽，绘制关闭钮时再让位
            bool showClose = isSelected || isHover;
            var closeW = (int)Math.Round(CloseButtonWidth * dpi);
            var closeH = (int)Math.Round(CloseButtonHeight * dpi);
            var closeR = (int)Math.Round(CloseButtonRightMargin * dpi);
            var closeT = (int)Math.Round(CloseButtonTopOffset * dpi);

            int textRightPad = showClose ? closeR : (int)Math.Round(10 * dpi);
            var textRect = new Rectangle(rect.X + (int)Math.Round(4 * dpi), rect.Y + 2,
                rect.Width - textRightPad - (int)Math.Round(4 * dpi), rect.Height - 4);
            TextRenderer.DrawText(e.Graphics, tab.Text, e.Font, textRect,
                isSelected ? GdtermColorTable.Foreground : GdtermColorTable.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (showClose)
            {
                var closeRect = new Rectangle(
                    rect.Right - closeR,
                    rect.Y + closeT,
                    closeW,
                    closeH);
                using (var brush = new SolidBrush(isSelected ? GdtermColorTable.Foreground : GdtermColorTable.Muted))
                    e.Graphics.DrawString("×", e.Font, brush,
                        closeRect.X + (closeW - 8) / 2,
                        closeRect.Y + (closeH - e.Font.Height) / 2);
            }
        }

        /// <summary>
        /// 若点击落在关闭按钮上，返回对应 TabPage；否则 null。
        /// 关闭钮仅悬停/选中时可见，但命中测试对悬停标签也生效（与可见性一致）。
        /// </summary>
        public TabPage HitTestClose(TabControl tabControl, Point location)
        {
            if (tabControl == null) return null;
            var dpi = DpiScale.Factor(tabControl);

            for (int i = 0; i < tabControl.TabPages.Count; i++)
            {
                var rect = tabControl.GetTabRect(i);
                if (!rect.Contains(location)) continue;
                bool showClose = (i == tabControl.SelectedIndex) || (i == HoverIndex);
                if (!showClose) return null;
                var closeRect = GetCloseRect(rect, dpi);
                if (closeRect.Contains(location))
                    return tabControl.TabPages[i];
                return null;
            }
            return null;
        }

        public static Rectangle GetCloseRect(Rectangle tabRect)
        {
            return new Rectangle(
                tabRect.Right - CloseButtonRightMargin,
                tabRect.Y + CloseButtonTopOffset,
                CloseButtonWidth,
                CloseButtonHeight);
        }

        /// <summary>按 DPI 缩放后的关闭按钮矩形（供 HitTestClose 使用）。</summary>
        public static Rectangle GetCloseRect(Rectangle tabRect, float dpi)
        {
            var closeW = (int)Math.Round(CloseButtonWidth * dpi);
            var closeH = (int)Math.Round(CloseButtonHeight * dpi);
            var closeR = (int)Math.Round(CloseButtonRightMargin * dpi);
            var closeT = (int)Math.Round(CloseButtonTopOffset * dpi);
            return new Rectangle(
                tabRect.Right - closeR,
                tabRect.Y + closeT,
                closeW,
                closeH);
        }
    }
}
