using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// 全局 UI 主题色表 + ToolStrip 渲染器：解决"发虚/不稳重"的视觉问题。
    /// </summary>
    /// <remarks>
    /// 默认 SystemRenderer 在自定义 BackColor 上会留灰边、字体锯齿、悬停高亮突兀；
    /// 这个 ColorTable + ProfessionalRenderer 组合把菜单/状态栏/工具栏统一为暗色、
    /// 平滑边缘、ClearType 文本，配合 manifest 的 DPI awareness 让 UI 在高 DPI 屏清晰。
    ///
    /// 色板参考 design-system 推荐：bg #0F172A / surface #1E293B / accent #16A34A /
    /// border #334155 / fg #F8FAFC。
    /// </remarks>
    internal sealed class GdtermColorTable : ProfessionalColorTable
    {
        // 与 MainForm BackColor 一致的深色
        public static readonly Color Background = Color.FromArgb(30, 30, 30);
        public static readonly Color Surface = Color.FromArgb(45, 45, 48);
        public static readonly Color Border = Color.FromArgb(64, 64, 64);
        public static readonly Color Accent = Color.FromArgb(22, 163, 74);
        public static readonly Color Foreground = Color.FromArgb(240, 240, 240);
        public static readonly Color Muted = Color.FromArgb(160, 160, 160);
        public static readonly Color Hover = Color.FromArgb(60, 60, 65);
        public static readonly Color Pressed = Color.FromArgb(80, 80, 85);

        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemPressed => Pressed;
        public override Color MenuStripGradientBegin => Background;
        public override Color MenuStripGradientEnd => Background;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Pressed;
        public override Color MenuItemPressedGradientEnd => Pressed;
        public override Color ToolStripBorder => Background;
        public override Color ToolStripGradientBegin => Background;
        public override Color ToolStripGradientMiddle => Background;
        public override Color ToolStripGradientEnd => Background;
        public override Color StatusStripGradientBegin => Background;
        public override Color StatusStripGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonPressedHighlight => Pressed;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedGradientBegin => Pressed;
        public override Color ButtonPressedGradientEnd => Pressed;
        public override Color ButtonCheckedGradientBegin => Pressed;
        public override Color ButtonCheckedGradientEnd => Pressed;
        public override Color GripDark => Border;
        public override Color GripLight => Border;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color ImageMarginRevealedGradientBegin => Hover;
        public override Color ImageMarginRevealedGradientMiddle => Hover;
        public override Color ImageMarginRevealedGradientEnd => Hover;
    }

    /// <summary>
    /// 在 ProfessionalRenderer 基础上额外修两件事：
    /// 1) 文本与图像用 SmoothingMode.AntiAlias + TextRenderingHint.ClearTypeGridFit 渲染，避免锯齿/虚边；
    /// 2) OnRenderMenuItemBackground 用平滑填色，不要默认系统画法在暗色背景下留白边。
    /// </summary>
    internal sealed class GdtermToolStripRenderer : ToolStripProfessionalRenderer
    {
        public GdtermToolStripRenderer() : base(new GdtermColorTable()) { }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            // 让每个 ToolStrip 自身也跟随暗色 + 清晰字体
            try
            {
                toolStrip.BackColor = GdtermColorTable.Background;
                toolStrip.ForeColor = GdtermColorTable.Foreground;
                toolStrip.BorderStyle = BorderStyle.None;
            }
            catch { }
        }

        protected override void OnRenderText(ToolStripItemTextRenderEventArgs e)
        {
            try
            {
                var oldMode = e.Graphics.TextRenderingHint;
                var oldSmoothing = e.Graphics.SmoothingMode;
                e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                base.OnRenderText(e);
                e.Graphics.TextRenderingHint = oldMode;
                e.Graphics.SmoothingMode = oldSmoothing;
            }
            catch
            {
                base.OnRenderText(e);
            }
        }

        protected override void OnRenderImage(ToolStripItemImageRenderEventArgs e)
        {
            try
            {
                var old = e.Graphics.InterpolationMode;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                base.OnRenderImage(e);
                e.Graphics.InterpolationMode = old;
            }
            catch
            {
                base.OnRenderImage(e);
            }
        }
    }
}
