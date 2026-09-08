using System;
using System.Drawing;
using System.Runtime.CompilerServices;
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
        private static readonly ConditionalWeakTable<Control, ControlEventHandler> AntdShapeHooks =
            new ConditionalWeakTable<Control, ControlEventHandler>();

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

        /// <summary>
        /// 真实 GDI 行高 × 节奏系数——对应 CSS line-height 的“行盒”高度。
        ///
        /// 用途：标题/单行标签所在容器的行高。配合 TextAlign=MiddleLeft，
        /// 让字形在盒内垂直居中——CSS 的 line-box + baseline 默认白送的能力，
        /// WinForms 必须显式：AutoSize 控件盒高=em 高（无 leading），
        /// 固定 Height 控件字形按 baseline 摆，缺那 25% 呼吸，看着“贴顶/贴底”。
        ///
        /// 与 RowStep 的分工：
        ///   RowStep = 行高 + 9（行与行之间的步进，y += RowStep）；
        ///   LineBox = 行高 × rhythm（单行容器自身的高度，配 Middle 居中）。
        /// </summary>
        /// <param name="font">量哪个字体（标题往往用独立字体，不随控件 Font）。</param>
        /// <param name="c">用于 CreateGraphics 取 DPI 的宿主；可空。</param>
        /// <param name="rhythm">行高倍率，对应 CSS line-height 数值；默认 1.25（紧凑偏松）。</param>
        public static int LineBox(Font font, Control c, float rhythm = 1.25f)
        {
            var f = font ?? UiFont();
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
            double box = h * (double)rhythm;
            if (box < 1) box = 1;
            return (int)Math.Round(box);
        }

        /// <overloads>宿主字体版（控件自身 Font）。</overloads>
        public static int LineBox(Control c, float rhythm = 1.25f)
            => LineBox(c != null ? c.Font : null, c, rhythm);

        /// <summary>运行时切换 UI 字号时，同步已有的显式 UI 字体。</summary>
        public static void ApplyChildUIFont(Control root, string name, float size)
        {
            if (root == null || string.IsNullOrEmpty(name) || size <= 0) return;
            ReplaceChildFonts(root.Controls, name, size, true);
            NormalizeAntdShapes(root);
        }

        public static void Apply(Form form)
        {
            if (form == null) return;
            var name = UiFontName;
            float size = UiFontSize;

            try { form.Font = new Font(name, size, FontStyle.Regular); }
            catch { return; }

            ReplaceChildFonts(form.Controls, name, size, false);
            NormalizeAntdShapes(form);
        }

        /// <summary>
        /// AntdUI 默认控件带圆角，而原生 WinForms 工作台控件是方角。
        /// 统一采用方角密集工作台语言，使用 AntdUI 自身的 Radius 属性，不自绘控件。
        /// 通过反射兼容当前随包 DLL 的属性版本；没有该属性的控件保持原样。
        /// </summary>
        private static void NormalizeAntdShapes(Control root)
        {
            if (root == null) return;
            var stack = new System.Collections.Generic.Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                try
                {
                    ApplyAntdShape(c);
                }
                catch { }
                HookAntdShapeChanges(c);
                foreach (Control child in c.Controls) stack.Push(child);
            }
        }

        /// <summary>把单个 AntdUI 控件纳入方角语言，供运行时动态创建控件调用。</summary>
        public static void ApplyAntdShape(Control control)
        {
            if (control == null || !IsAntdControl(control)) return;
            SetNumericProperty(control, "Radius", 0);
            SetNumericProperty(control, "RadiusX", 0);
            SetNumericProperty(control, "RadiusY", 0);
        }

        private static bool IsAntdControl(Control control)
        {
            var ns = control != null ? control.GetType().Namespace : null;
            return ns == "AntdUI" || (ns != null && ns.StartsWith("AntdUI.", StringComparison.Ordinal));
        }

        private static void HookAntdShapeChanges(Control control)
        {
            if (control == null) return;
            lock (AntdShapeHooks)
            {
                ControlEventHandler ignored;
                if (AntdShapeHooks.TryGetValue(control, out ignored)) return;
                ControlEventHandler handler = (sender, args) =>
                {
                    var added = args != null ? args.Control : null;
                    if (added != null) NormalizeAntdShapes(added);
                };
                control.ControlAdded += handler;
                AntdShapeHooks.Add(control, handler);
            }
        }

        private static void SetNumericProperty(Control control, string name, int value)
        {
            var property = control.GetType().GetProperty(name);
            if (property == null || !property.CanWrite) return;
            if (property.PropertyType == typeof(int)) property.SetValue(control, value, null);
            else if (property.PropertyType == typeof(short)) property.SetValue(control, (short)value, null);
            else if (property.PropertyType == typeof(byte)) property.SetValue(control, (byte)value, null);
            else if (property.PropertyType == typeof(float)) property.SetValue(control, (float)value, null);
            else if (property.PropertyType == typeof(double)) property.SetValue(control, (double)value, null);
            else if (property.PropertyType == typeof(decimal)) property.SetValue(control, (decimal)value, null);
        }

        private static void ReplaceChildFonts(Control.ControlCollection controls, string name, float size, bool replaceAllUiFonts)
        {
            if (controls == null) return;
            foreach (Control c in controls)
            {
                try
                {
                    var f = c.Font;
                    if (f != null && !string.IsNullOrEmpty(f.Name) && IsReplaceableUiFont(f.Name, replaceAllUiFonts))
                    {
                        c.Font = new Font(name, size, f.Style);
                    }
                }
                catch { }
                ReplaceChildFonts(c.Controls, name, size, replaceAllUiFonts);
            }
        }

        private static bool IsReplaceableUiFont(string name, bool replaceAllUiFonts)
        {
            if (name.StartsWith("Microsoft YaHei", StringComparison.OrdinalIgnoreCase)
                || name == "微软雅黑")
                return true;
            if (!replaceAllUiFonts) return false;

            // 这些字体承载终端、密码或图标语义，不能被 UI 字号覆盖。
            return name.IndexOf("Consolas", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Courier", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Mono", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Emoji", StringComparison.OrdinalIgnoreCase) < 0;
        }
    }
}
