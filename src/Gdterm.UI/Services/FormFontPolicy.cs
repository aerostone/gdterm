using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 对话框字体统一策略——所有弹出窗体跟随 外观设置 → UI 字体（UIFontName/UIFontSize）。
    /// 主窗体由 MainForm.ApplyGlobalUIFont 负责；对话框在构造末尾调 Apply(this)。
    ///
    /// 背景：此前 KeePassManager/ConnectionDialog 等十余个窗体各自硬编码
    /// "Microsoft YaHei" 9f，用户改了全局 UI 字体后弹窗纹丝不动，观感割裂。
    ///
    /// 规则：
    ///   - Form.Font 设为全局 UI 字体（未显式设字体的子控件自动级联）；
    ///   - 显式设置了雅黑系字体的子控件替换为全局字体（保留粗斜体样式）；
    ///   - Consolas/Courier 等等宽字体是代码/终端语义，保留原字号不动。
    /// </summary>
    public static class FormFontPolicy
    {
        /// <summary>
        /// UI 字体的安全解析——带安装探测与 Win7 回退链。
        ///
        /// 背景（2026-09 Win7/2008R2 兼容 + 字体重叠排查）：
        ///   - “Microsoft YaHei UI” 是 Win8 才引入的字族，Win7 上不存在；
        ///   - new Font("不存在的名字", …) 不抛异常，GDI+ 静默回退到默认字体（宋体），
        ///     中文度量偏窄、行高不同，是老系统上“文字挤压/重叠”的直接推手；
        ///   - 因此任何把 UI 字体名交给 Font 构造器的路径都必须先经过这里。
        ///
        /// 回退链（首个“已安装且非模拟”的字族生效）：
        ///   Microsoft YaHei UI → Microsoft YaHei → Segoe UI → 系统默认 UI 字体。
        /// 用户在外观设置中显式选择的字体若可用则优先；不可用（如换机器）也走回退链，
        /// 避免配置里存了个 Win10 字体名在 Win7 机器上静默变成宋体。
        /// </summary>
        private static string ResolveUiFamilyName(string requestedName)
        {
            var candidates = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(requestedName))
                candidates.Add(requestedName);
            candidates.Add("Microsoft YaHei UI");
            candidates.Add("Microsoft YaHei");
            candidates.Add("Segoe UI");

            try
            {
                using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (var want in candidates)
                    {
                        foreach (var f in fonts.Families)
                        {
                            if (string.Equals(f.Name, want, StringComparison.OrdinalIgnoreCase))
                                return f.Name;
                        }
                    }
                }
            }
            catch { /* InstalledFontCollection 失败（极端：GDI+ 异常）→ 最后回退 */ }
            return SystemFonts.DefaultFont.FontFamily.Name;
        }

        /// <summary>当前生效的全局 UI 字体名（探测后）——设置回显/诊断用。</summary>
        public static string UiFontName
        {
            get
            {
                var ga = Gdterm.UI.Program.GlobalAppearance;
                var requested = ga != null && !string.IsNullOrWhiteSpace(ga.UIFontName) ? ga.UIFontName : null;
                return ResolveUiFamilyName(requested);
            }
        }

        /// <summary>当前生效的全局 UI 字号（pt）。</summary>
        public static float UiFontSize
        {
            get
            {
                var ga = Gdterm.UI.Program.GlobalAppearance;
                return ga != null && ga.UIFontSize > 0 ? ga.UIFontSize : 9f;
            }
        }

        /// <summary>
        /// 全局 UI 字体工厂——供面板/UserControl 等非 Form 场景在构造期使用。
        /// 规范规则③的合法取字体方式；禁止再手写 new Font("Microsoft YaHei", …)。
        /// </summary>
        /// <param name="sizeDelta">相对全局字号的偏移（标题 +N，次要文字 -N）。</param>
        public static Font UiFont(float sizeDelta = 0f, FontStyle style = FontStyle.Regular)
        {
            var name = UiFontName;
            float size = Math.Max(6f, UiFontSize + sizeDelta);
            try { return new Font(name, size, style); }
            catch { return new Font(SystemFonts.DefaultFont.FontFamily, size, style); }
        }

        /// <summary>
        /// 字体驱动的表单行距——按当前全局 UI 字体实际行高推导每行步进。
        ///
        /// 背景：大量手写对话框用固定 y += 35 步进布局。该步进按 9pt@96dpi 设计；
        /// 用户把字号调到 11–12pt 后行高超过步进，上下两行控件文字互相重叠。
        /// 改法：控件高度尽量 AutoSize，步进取 RowStep（= 字体行高 + 9 间距，≥30），
        /// 任何字号下“行高 + 留白”都成立，永不重叠。
        /// </summary>
        public static int RowStep(Control c)
        {
            var f = c != null && c.Font != null ? c.Font : UiFont();
            int h;
            try
            {
                using (var g = c != null ? c.CreateGraphics() : null)
                {
                    h = g != null ? TextRenderer.MeasureText(g, "M建g", f).Height
                                  : TextRenderer.MeasureText("M建g", f).Height;
                }
            }
            catch { h = 16; }
            // 行高 + 9px 间距；小字号时保底 30（维持既有密度观感）
            return Math.Max(30, h + 9);
        }

        public static void Apply(Form form)
        {
            if (form == null) return;
            var name = UiFontName;
            float size = UiFontSize;

            try { form.Font = new Font(name, size, FontStyle.Regular); }
            catch { return; }

            ReplaceChildFonts(form.Controls, name, size);
        }

        private static void ReplaceChildFonts(Control.ControlCollection controls, string name, float size)
        {
            if (controls == null) return;
            foreach (Control c in controls)
            {
                try
                {
                    var f = c.Font;
                    if (f != null && !string.IsNullOrEmpty(f.Name)
                        && (f.Name.StartsWith("Microsoft YaHei", StringComparison.OrdinalIgnoreCase)
                            || f.Name == "微软雅黑"))
                    {
                        c.Font = new Font(name, size, f.Style);
                    }
                }
                catch { }
                ReplaceChildFonts(c.Controls, name, size);
            }
        }
    }
}
