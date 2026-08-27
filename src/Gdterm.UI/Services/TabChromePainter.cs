using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签页绘制与关闭按钮命中测试（finding-10）。
    /// </summary>
    public sealed class TabChromePainter
    {
        public const int CloseButtonWidth = 16;
        public const int CloseButtonHeight = 18;
        public const int CloseButtonRightMargin = 18;
        public const int CloseButtonTopOffset = 2;

        /// <summary>按 DPI 缩放绘制标签页与关闭按钮。</summary>
        public void DrawTab(DrawItemEventArgs e, TabControl tabControl)
        {
            if (e == null || tabControl == null) return;
            if (e.Index < 0 || e.Index >= tabControl.TabPages.Count) return;

            var tab = tabControl.TabPages[e.Index];
            var rect = e.Bounds;
            var dpi = DpiScale.Factor(tabControl);

            bool isSelected = (e.Index == tabControl.SelectedIndex);
            using (var brush = new SolidBrush(isSelected ? SystemColors.ControlLight : SystemColors.Control))
                e.Graphics.FillRectangle(brush, rect);

            var closeW = (int)Math.Round(CloseButtonWidth * dpi);
            var closeH = (int)Math.Round(CloseButtonHeight * dpi);
            var closeR = (int)Math.Round(CloseButtonRightMargin * dpi);
            var closeT = (int)Math.Round(CloseButtonTopOffset * dpi);

            var textRect = new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - closeR - 4, rect.Height - 4);
            TextRenderer.DrawText(e.Graphics, tab.Text, e.Font, textRect, SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var closeRect = new Rectangle(
                rect.Right - closeR,
                rect.Y + closeT,
                closeW,
                closeH);
            using (var brush = new SolidBrush(isSelected ? Color.Black : Color.Gray))
                e.Graphics.DrawString("×", e.Font, brush,
                    closeRect.X + (closeW - 8) / 2,
                    closeRect.Y + (closeH - e.Font.Height) / 2);
        }

        /// <summary>
        /// 若点击落在关闭按钮上，返回对应 TabPage；否则 null。
        /// </summary>
        public TabPage HitTestClose(TabControl tabControl, Point location)
        {
            if (tabControl == null) return null;
            var dpi = DpiScale.Factor(tabControl);

            for (int i = 0; i < tabControl.TabPages.Count; i++)
            {
                var rect = tabControl.GetTabRect(i);
                var closeRect = GetCloseRect(rect, dpi);
                if (closeRect.Contains(location))
                    return tabControl.TabPages[i];
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
