using System;
using System.Drawing;
using System.Windows.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>控件 ToolTip 扩展（单个共享 ToolTip，避免每个控件创建独立组件）。
    /// F02 自 TmuxBarPanel.cs 迁移：BottomBarPanel 仍在用，orphan 删除后共用件保留于此。</summary>
    internal static class ButtonTipExtension
    {
        private static readonly ToolTip Tip = new ToolTip();

        public static void ToolTipText2(this Control c, string text)
        {
            Tip.SetToolTip(c, text);
        }
    }

    /// <summary>深色菜单渲染器（ContextMenuStrip 深色主题）。
    /// F02 自 QuickBarPanel.cs 迁移：BottomBarPanel 的 …/右键菜单仍在用，orphan 删除后共用件保留于此。</summary>
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            e.Item.BackColor = e.Item.Selected ? GdtermColorTable.Border : GdtermColorTable.Surface;
            e.Item.ForeColor = GdtermColorTable.Foreground;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.FillRectangle(new SolidBrush(GdtermColorTable.Surface), e.AffectedBounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            e.Graphics.FillRectangle(new SolidBrush(GdtermColorTable.Border), 0, 3, e.Item.Width, 1);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = GdtermColorTable.Foreground;
            base.OnRenderItemText(e);
        }
    }
}
