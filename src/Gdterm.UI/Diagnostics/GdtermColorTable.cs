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
    /// 色板采用石墨暗色 + 低饱和薄荷绿：
    /// 背景和容器保持中性，薄荷绿只负责主操作和选中反馈，状态色只做小面积提示。
    /// 参照 Xshell/SecureCRT 暗色专业外观：深背景、清晰层级、无彩色渐变。
    /// </remarks>
    internal sealed class GdtermColorTable : ProfessionalColorTable
    {
        // 石墨暗色 + 薄荷绿 — 降低荧光色造成的视觉噪声
        // 采用静态属性 + 后台字段，使运行时可切换主题而不重启。
        private static Color s_background = Color.FromArgb(0x11, 0x16, 0x1A);
        private static Color s_surface = Color.FromArgb(0x1A, 0x21, 0x27);
        private static Color s_border = Color.FromArgb(0x35, 0x41, 0x4A);
        private static Color s_accent = Color.FromArgb(0x34, 0xD3, 0x99);
        private static Color s_foreground = Color.FromArgb(0xE8, 0xEE, 0xF2);
        private static Color s_muted = Color.FromArgb(0x9A, 0xA7, 0xB1);
        private static Color s_hover = Color.FromArgb(0x26, 0x30, 0x39);
        private static Color s_pressed = Color.FromArgb(0x32, 0x3D, 0x47);
        private static Color s_on_accent = Color.FromArgb(0x08, 0x22, 0x18);
        private static Color s_on_danger = Color.FromArgb(0x2A, 0x0E, 0x0C);
        private static Color s_on_info = Color.FromArgb(0x0B, 0x1B, 0x2B);
        private static Color s_overlay = Color.FromArgb(210, 0x0A, 0x0E, 0x12);

        public static Color Background { get { return s_background; } }
        public static Color Surface { get { return s_surface; } }
        public static Color Border { get { return s_border; } }
        public static Color Accent { get { return s_accent; } }
        public static Color Foreground { get { return s_foreground; } }
        public static Color Muted { get { return s_muted; } }
        public static Color Hover { get { return s_hover; } }
        public static Color Pressed { get { return s_pressed; } }
        public static Color OnAccent { get { return s_on_accent; } }
        public static Color OnDanger { get { return s_on_danger; } }
        public static Color OnInfo { get { return s_on_info; } }
        public static Color Overlay { get { return s_overlay; } }

        // ── 语义色（DESIGN-LANGUAGE v1.3：状态色只用于文字与图标，不做大面积底色）──
        private static Color s_danger = Color.FromArgb(0xF0, 0x6A, 0x64);
        private static Color s_warning = Color.FromArgb(0xE4, 0xB8, 0x6A);
        private static Color s_success = Color.FromArgb(0x59, 0xC7, 0x7B);
        private static Color s_info = Color.FromArgb(0x75, 0xA9, 0xE6);

        /// <summary>危险/删除/错误文字（#F06A64）。</summary>
        public static Color Danger { get { return s_danger; } }
        /// <summary>告警文字（#E4B86A）。</summary>
        public static Color Warning { get { return s_warning; } }
        /// <summary>成功状态文字（#59C77B，区别于 Accent 按钮绿）。</summary>
        public static Color Success { get { return s_success; } }
        /// <summary>链接/信息（#75A9E6，蓝色仅在可点击文字场景）。</summary>
        public static Color Info { get { return s_info; } }

        /// <summary>运行时切换外壳主题（与终端 ColorScheme 独立）。</summary>
        public static void ApplyTheme(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Dark";
            switch (name)
            {
                case "Darker":
                    s_background = Color.FromArgb(0x0C, 0x10, 0x13);
                    s_surface = Color.FromArgb(0x14, 0x1A, 0x1E);
                    s_border = Color.FromArgb(0x2B, 0x35, 0x3C);
                    s_accent = Color.FromArgb(0x34, 0xD3, 0x99);
                    s_foreground = Color.FromArgb(0xE8, 0xEE, 0xF2);
                    s_muted = Color.FromArgb(0x87, 0x94, 0x9D);
                    s_hover = Color.FromArgb(0x20, 0x28, 0x2E);
                    s_pressed = Color.FromArgb(0x2B, 0x34, 0x3B);
                    break;
                case "OLED":
                    s_background = Color.FromArgb(0x05, 0x06, 0x06);
                    s_surface = Color.FromArgb(0x0C, 0x10, 0x10);
                    s_border = Color.FromArgb(0x20, 0x28, 0x29);
                    s_accent = Color.FromArgb(0x34, 0xD3, 0x99);
                    s_foreground = Color.FromArgb(0xF1, 0xF5, 0xF5);
                    s_muted = Color.FromArgb(0x8B, 0x96, 0x96);
                    s_hover = Color.FromArgb(0x15, 0x1A, 0x1A);
                    s_pressed = Color.FromArgb(0x20, 0x28, 0x28);
                    break;
                case "Dark":
                default:
                    s_background = Color.FromArgb(0x11, 0x16, 0x1A);
                    s_surface = Color.FromArgb(0x1A, 0x21, 0x27);
                    s_border = Color.FromArgb(0x35, 0x41, 0x4A);
                    s_accent = Color.FromArgb(0x34, 0xD3, 0x99);
                    s_foreground = Color.FromArgb(0xE8, 0xEE, 0xF2);
                    s_muted = Color.FromArgb(0x9A, 0xA7, 0xB1);
                    s_hover = Color.FromArgb(0x26, 0x30, 0x39);
                    s_pressed = Color.FromArgb(0x32, 0x3D, 0x47);
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
