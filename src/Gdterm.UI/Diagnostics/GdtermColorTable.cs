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
    /// 色板对齐 ui-ux-pro-max 推荐（GitHub Dark / 终端绿）：
    /// bg #0D1117 / surface #161B22 / border #30363D / muted #25292F /
    /// accent #00FF41 终端绿 / destructive #EF4444 / fg #E6EDF3。
    /// 参照 Xshell/SecureCRT 暗色专业外观：深背景、低饱和、单一绿色强调，
    /// 不要彩色渐变。
    /// </remarks>
    internal sealed class GdtermColorTable : ProfessionalColorTable
    {
        // GitHub Dark + 终端绿 — 对齐成熟终端客户端的暗色规范
        public static readonly Color Background = Color.FromArgb(0x0D, 0x11, 0x17);
        public static readonly Color Surface = Color.FromArgb(0x16, 0x1B, 0x22);
        public static readonly Color Border = Color.FromArgb(0x30, 0x36, 0x3D);
        public static readonly Color Accent = Color.FromArgb(0x00, 0xFF, 0x41);
        public static readonly Color Foreground = Color.FromArgb(0xE6, 0xED, 0xF3);
        public static readonly Color Muted = Color.FromArgb(0x8B, 0x94, 0x9E);
        public static readonly Color Hover = Color.FromArgb(0x25, 0x29, 0x2F);
        public static readonly Color Pressed = Color.FromArgb(0x35, 0x39, 0x3F);

        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
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
            }
            catch { }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            try
            {
                var oldMode = e.Graphics.TextRenderingHint;
                var oldSmoothing = e.Graphics.SmoothingMode;
                e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                base.OnRenderItemText(e);
                e.Graphics.TextRenderingHint = oldMode;
                e.Graphics.SmoothingMode = oldSmoothing;
            }
            catch
            {
                base.OnRenderItemText(e);
            }
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            try
            {
                var old = e.Graphics.InterpolationMode;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                base.OnRenderItemImage(e);
                e.Graphics.InterpolationMode = old;
            }
            catch
            {
                base.OnRenderItemImage(e);
            }
        }
    }
}
