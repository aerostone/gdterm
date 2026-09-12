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
    /// v2 优化（原型 design/prototype/v2-optimized.html 验收通过）：
    ///   P1 三级明度拉开（bg/surface/surface2 差值过小导致“灰泥感”）
    ///   P2 边框分级：Border 弱化 + BorderStrong 新设（交互件边缘）
    ///   P3 强调色提纯 #34D399→#3DDC97；状态栏去链接蓝改彩点+中性文字
    ///   P5 选中态 = Tint（Accent 10%）底，树/标签统一
    /// </remarks>
    internal sealed class GdtermColorTable : ProfessionalColorTable
    {
        // 石墨暗色 + 薄荷绿 — 降低荧光色造成的视觉噪声
        // 采用静态属性 + 后台字段，使运行时可切换主题而不重启。
        private static Color s_background = Color.FromArgb(0x0D, 0x11, 0x16);
        private static Color s_surface = Color.FromArgb(0x16, 0x1D, 0x24);
        private static Color s_surface2 = Color.FromArgb(0x1E, 0x27, 0x31);
        private static Color s_border = Color.FromArgb(0x29, 0x32, 0x3D);
        private static Color s_border_strong = Color.FromArgb(0x41, 0x50, 0x5F);
        private static Color s_accent = Color.FromArgb(0x3D, 0xDC, 0x97);
        private static Color s_foreground = Color.FromArgb(0xE8, 0xEE, 0xF3);
        private static Color s_muted = Color.FromArgb(0x8D, 0x9A, 0xA7);
        private static Color s_hover = Color.FromArgb(0x22, 0x2C, 0x36);
        private static Color s_pressed = Color.FromArgb(0x2A, 0x35, 0x40);
        private static Color s_on_accent = Color.FromArgb(0x06, 0x25, 0x1A);
        private static Color s_on_danger = Color.FromArgb(0x2A, 0x0E, 0x0C);
        private static Color s_on_info = Color.FromArgb(0x0B, 0x1B, 0x2B);
        private static Color s_overlay = Color.FromArgb(210, 0x0A, 0x0E, 0x12);
        // P5：选中态强调底 = Accent 10%（alpha 26/255）
        private static Color s_tint = Color.FromArgb(26, 0x3D, 0xDC, 0x97);

        public static Color Background { get { return s_background; } }
        public static Color Surface { get { return s_surface; } }
        /// <summary>悬浮层/弹出面板面（P1：与 Surface 拉开一档，菜单/下拉背景）。</summary>
        public static Color Surface2 { get { return s_surface2; } }
        public static Color Border { get { return s_border; } }
        /// <summary>交互件边缘（按钮/输入框/对话框外框，P2 分级边框）。</summary>
        public static Color BorderStrong { get { return s_border_strong; } }
        public static Color Accent { get { return s_accent; } }
        public static Color Foreground { get { return s_foreground; } }
        public static Color Muted { get { return s_muted; } }
        public static Color Hover { get { return s_hover; } }
        public static Color Pressed { get { return s_pressed; } }
        public static Color OnAccent { get { return s_on_accent; } }
        public static Color OnDanger { get { return s_on_danger; } }
        public static Color OnInfo { get { return s_on_info; } }
        public static Color Overlay { get { return s_overlay; } }
        /// <summary>选中态强调色底（P5：树/标签选中，10% 透明度叠加）。</summary>
        public static Color Tint { get { return s_tint; } }

        // ── 语义色（DESIGN-LANGUAGE v1.3：状态色只用于文字与图标，不做大面积底色）──
        private static Color s_danger = Color.FromArgb(0xF4, 0x7C, 0x74);
        private static Color s_warning = Color.FromArgb(0xE8, 0xC0, 0x78);
        private static Color s_success = Color.FromArgb(0x63, 0xD2, 0x88);
        private static Color s_info = Color.FromArgb(0x7F, 0xB3, 0xEE);

        /// <summary>危险/删除/错误文字（#F06A64）。</summary>
        public static Color Danger { get { return s_danger; } }
        /// <summary>告警文字（#E4B86A）。</summary>
        public static Color Warning { get { return s_warning; } }
        /// <summary>成功状态文字（#59C77B，区别于 Accent 按钮绿）。</summary>
        public static Color Success { get { return s_success; } }
        /// <summary>链接/信息（#75A9E6，蓝色仅在可点击文字场景）。</summary>
        public static Color Info { get { return s_info; } }

        /// <summary>运行时切换外壳主题（与终端 ColorScheme 独立）。v2 色板：三级明度 + 分级边框 + Tint。</summary>
        public static void ApplyTheme(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Dark";
            switch (name)
            {
                case "Darker":
                    s_background = Color.FromArgb(0x08, 0x0B, 0x0E);
                    s_surface = Color.FromArgb(0x11, 0x16, 0x1C);
                    s_surface2 = Color.FromArgb(0x19, 0x1F, 0x27);
                    s_border = Color.FromArgb(0x22, 0x2B, 0x33);
                    s_border_strong = Color.FromArgb(0x39, 0x46, 0x53);
                    s_accent = Color.FromArgb(0x3D, 0xDC, 0x97);
                    s_foreground = Color.FromArgb(0xE6, 0xED, 0xF2);
                    s_muted = Color.FromArgb(0x84, 0x91, 0x9D);
                    s_hover = Color.FromArgb(0x1B, 0x23, 0x2B);
                    s_pressed = Color.FromArgb(0x23, 0x2D, 0x36);
                    s_tint = Color.FromArgb(26, 0x3D, 0xDC, 0x97);
                    break;
                case "OLED":
                    s_background = Color.FromArgb(0x03, 0x04, 0x04);
                    s_surface = Color.FromArgb(0x0A, 0x0F, 0x0F);
                    s_surface2 = Color.FromArgb(0x12, 0x1A, 0x1A);
                    s_border = Color.FromArgb(0x1B, 0x24, 0x25);
                    s_border_strong = Color.FromArgb(0x33, 0x44, 0x44);
                    s_accent = Color.FromArgb(0x3D, 0xDC, 0x97);
                    s_foreground = Color.FromArgb(0xF0, 0xF5, 0xF5);
                    s_muted = Color.FromArgb(0x87, 0x94, 0x94);
                    s_hover = Color.FromArgb(0x13, 0x1B, 0x1B);
                    s_pressed = Color.FromArgb(0x1B, 0x25, 0x25);
                    s_tint = Color.FromArgb(26, 0x3D, 0xDC, 0x97);
                    break;
                case "Dark":
                default:
                    s_background = Color.FromArgb(0x0D, 0x11, 0x16);
                    s_surface = Color.FromArgb(0x16, 0x1D, 0x24);
                    s_surface2 = Color.FromArgb(0x1E, 0x27, 0x31);
                    s_border = Color.FromArgb(0x29, 0x32, 0x3D);
                    s_border_strong = Color.FromArgb(0x41, 0x50, 0x5F);
                    s_accent = Color.FromArgb(0x3D, 0xDC, 0x97);
                    s_foreground = Color.FromArgb(0xE8, 0xEE, 0xF3);
                    s_muted = Color.FromArgb(0x8D, 0x9A, 0xA7);
                    s_hover = Color.FromArgb(0x22, 0x2C, 0x36);
                    s_pressed = Color.FromArgb(0x2A, 0x35, 0x40);
                    s_tint = Color.FromArgb(26, 0x3D, 0xDC, 0x97);
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
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("GdtermColorTable", exSwallowed); } catch { } }
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
