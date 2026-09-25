using System;
using System.Drawing;
using System.Windows.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>控件 ToolTip 扩展（单个共享 ToolTip，避免每个控件创建独立组件）。
    /// F02 自 TmuxBarPanel.cs 迁移而来；原 DarkMenuRenderer 死码已删（包 F），现仅存 ToolTip 扩展。</summary>
    internal static class MenuTipExtension
    {
        private static readonly ToolTip Tip = new ToolTip();

        public static void ToolTipText2(this Control c, string text)
        {
            Tip.SetToolTip(c, text);
        }
    }
}
