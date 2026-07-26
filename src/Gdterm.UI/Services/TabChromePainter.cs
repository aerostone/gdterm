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
        public const int CloseButtonWidth = 18;
        public const int CloseButtonHeight = 16;
        public const int CloseButtonRightMargin = 18;
        public const int CloseButtonTopOffset = 4;

        public void DrawTab(DrawItemEventArgs e, TabControl tabControl)
        {
            if (e == null || tabControl == null) return;
            if (e.Index < 0 || e.Index >= tabControl.TabPages.Count) return;

            var tab = tabControl.TabPages[e.Index];
            var rect = e.Bounds;

            bool isSelected = (e.Index == tabControl.SelectedIndex);
            using (var brush = new SolidBrush(isSelected ? SystemColors.ControlLight : SystemColors.Control))
                e.Graphics.FillRectangle(brush, rect);

            var textRect = new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - 24, rect.Height - 4);
            TextRenderer.DrawText(e.Graphics, tab.Text, e.Font, textRect, SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var closeRect = GetCloseRect(rect);
            using (var brush = new SolidBrush(Color.DarkGray))
                e.Graphics.DrawString("×", e.Font, brush, closeRect);
        }

        /// <summary>
        /// 若点击落在关闭按钮上，返回对应 TabPage；否则 null。
        /// </summary>
        public TabPage HitTestClose(TabControl tabControl, Point location)
        {
            if (tabControl == null) return null;

            for (int i = 0; i < tabControl.TabPages.Count; i++)
            {
                var rect = tabControl.GetTabRect(i);
                var closeRect = GetCloseRect(rect);
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
                14,
                CloseButtonHeight);
        }
    }
}
