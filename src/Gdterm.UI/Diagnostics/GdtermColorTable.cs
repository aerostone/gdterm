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
        // 采用静态属性 + 后台字段，使运行时可切换主题而不重启。
        private static Color s_background = Color.FromArgb(0x0D, 0x11, 0x17);
        private static Color s_surface = Color.FromArgb(0x16, 0x1B, 0x22);
        private static Color s_border = Color.FromArgb(0x30, 0x36, 0x3D);
        private static Color s_accent = Color.FromArgb(0x00, 0xFF, 0x41);
        private static Color s_foreground = Color.FromArgb(0xE6, 0xED, 0xF3);
        private static Color s_muted = Color.FromArgb(0x8B, 0x94, 0x9E);
        private static Color s_hover = Color.FromArgb(0x25, 0x29, 0x2F);
        private static Color s_pressed = Color.FromArgb(0x35, 0x39, 0x3F);

        public static Color Background { get { return s_background; } }
        public static Color Surface { get { return s_surface; } }
        public static Color Border { get { return s_border; } }
        public static Color Accent { get { return s_accent; } }
        public static Color Foreground { get { return s_foreground; } }
        public static Color Muted { get { return s_muted; } }
        public static Color Hover { get { return s_hover; } }
        public static Color Pressed { get { return s_pressed; } }

        /// <summary>运行时切换外壳主题（与终端 ColorScheme 独立）。</summary>
        public static void ApplyTheme(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Dark";
            switch (name)
            {
                case "Darker":
                    s_background = Color.FromArgb(0x10, 0x10, 0x10);
                    s_surface = Color.FromArgb(0x1A, 0x1A, 0x1A);
                    s_border = Color.FromArgb(0x33, 0x33, 0x33);
                    s_accent = Color.FromArgb(0x3B, 0x82, 0xF6);
                    s_foreground = Color.FromArgb(0xE6, 0xED, 0xF3);
                    s_muted = Color.FromArgb(0x82, 0x82, 0x82);
                    s_hover = Color.FromArgb(0x26, 0x26, 0x26);
                    s_pressed = Color.FromArgb(0x36, 0x36, 0x36);
                    break;
                case "OLED":
                    s_background = Color.FromArgb(0x00, 0x00, 0x00);
                    s_surface = Color.FromArgb(0x08, 0x08, 0x08);
                    s_border = Color.FromArgb(0x20, 0x20, 0x20);
                    s_accent = Color.FromArgb(0x00, 0xFF, 0x41);
                    s_foreground = Color.FromArgb(0xFA, 0xFA, 0xFA);
                    s_muted = Color.FromArgb(0x80, 0x80, 0x80);
                    s_hover = Color.FromArgb(0x14, 0x14, 0x14);
                    s_pressed = Color.FromArgb(0x22, 0x22, 0x22);
                    break;
                case "Dark":
                default:
                    s_background = Color.FromArgb(0x0D, 0x11, 0x17);
                    s_surface = Color.FromArgb(0x16, 0x1B, 0x22);
                    s_border = Color.FromArgb(0x30, 0x36, 0x3D);
                    s_accent = Color.FromArgb(0x00, 0xFF, 0x41);
                    s_foreground = Color.FromArgb(0xE6, 0xED, 0xF3);
                    s_muted = Color.FromArgb(0x8B, 0x94, 0x9E);
                    s_hover = Color.FromArgb(0x25, 0x29, 0x2F);
                    s_pressed = Color.FromArgb(0x35, 0x39, 0x3F);
                    break;
            }
        }

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
