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
}
