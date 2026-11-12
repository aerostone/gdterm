using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;

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
        // F03：行高量测缓存——RowStep/LineBox 每次 CreateGraphics 在底栏逐钮/树节点/标签页等高频路径重复创建 GDI 对象。
        // 键含 DPI（跨屏拖动 DPI 变则失效重算），64 条上限防字体枚举攻击撑内存，命中直接返回。
        private static readonly System.Collections.Generic.Dictionary<string, int> LineHeightCache =
            new System.Collections.Generic.Dictionary<string, int>(64);
        private static readonly object LineHeightLock = new object();

        private static int CachedLineHeight(Font f, Control c)
        {
            float dpi = 96f;
            try
            {
                if (c != null)
                {
                    using (var g = c.CreateGraphics()) dpi = g.DpiX;
                }
            }
            catch { dpi = 96f; }
            string key = (f != null ? f.Name : "?") + "|" + (f != null ? f.Size : 0f).ToString("F2")
                + "|" + (f != null ? (int)f.Style : 0) + "|" + dpi.ToString("F0");
            lock (LineHeightLock)
            {
                int hit;
                if (LineHeightCache.TryGetValue(key, out hit)) return hit;
            }
            int h;
            try
            {
                // 无宿主时退回无参重载（与旧行为一致）；有宿主用其 Graphics 保证 DPI 一致
                if (c != null)
                {
                    using (var g = c.CreateGraphics())
                        h = TextRenderer.MeasureText(g, "M建g", f).Height;
                }
                else
                {
                    h = TextRenderer.MeasureText("M建g", f).Height;
                }
            }
            catch { h = 16; }
            lock (LineHeightLock)
            {
                if (LineHeightCache.Count >= 64) LineHeightCache.Clear();
                LineHeightCache[key] = h;
            }
            return h;
        }
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
        /// 输入框统一高度 SSOT —— max(DPI 缩放 38px 地板, 字体行步进)。
        /// 背景（审计 2026-09-25-ui-coverage-input-audit F5）：此前 17 窗体各自本地写
        /// Math.Max(DpiScale.V(this,38), RowStep(this))，另有 36/30/28 三种漂移口径。
        /// 新代码一律用本方法；锁屏覆盖层（LockOverlayControl 30 地板）为显式豁免。
        /// </summary>
        public static int FieldHeight(Control c)
        {
            return Math.Max(DpiScale.V(c, 38), RowStep(c));
        }

        /// <summary>
        /// 把「工具条动作钮」按 DPI/字号真实量出来（AutoSize + Padding 的写法量不出来）。
        ///
        /// 背景（2026-11-13 状态轴矩阵实证）：AntdUI.Button 的 AutoSize 只用**库内固定 sps**
        /// 算尺寸，赋给它的 Padding 只影响绘制、不进测量。于是 `AutoSize=true; Padding=DpiScale(8,3)`
        /// 的钮在 96dpi 是 46x30，在 150dpi 仍是 46x30——逻辑尺寸反而缩到 30.6x20，
        /// ui-tree-check R1（触击 ≥32 逻辑 px）在 d150 全红（HistoryExportButton/ForwardExportButton...）。
        ///
        /// 改法：关掉 AutoSize，宽度按真实 GDI 文字宽 + 两侧内边距，高度取 <see cref="FieldHeight"/>
        /// （与输入框同高，DPI 感知且 ≥ 触击下限）。
        /// </summary>
        /// <param name="host">宿主控件（取 DPI 换算比例）。</param>
        /// <param name="b">要定尺寸的钮；null 直接返回。</param>
        /// <param name="padX">左右内边距（设计 px，会被 DPI 缩放）。</param>
        /// <param name="padY">上下内边距（设计 px，仅影响绘制留白）。</param>
        /// <param name="designHeight">
        /// 显式高度（设计 px）。0 = 取 <see cref="FieldHeight"/>（与输入框同高，适合工具条/页脚）。
        /// 状态栏等紧凑场景传 22~24：仍然 DPI 感知，但不会被撑到输入框那么高。
        /// </param>
        public static void SizeActionButton(Control host, AntdUI.Button b, int padX = 10, int padY = 4,
            int designHeight = 0)
        {
            if (b == null) return;
            b.AutoSize = false;
            int px = Math.Max(0, DpiScale.V(host, padX));
            int py = Math.Max(0, DpiScale.V(host, padY));
            b.Padding = new Padding(px, py, px, py);

            void Apply()
            {
                if (b.IsDisposed) return;
                var f = b.Font ?? UiFont();
                string text = string.IsNullOrEmpty(b.Text) ? " " : b.Text;
                int tw;
                try
                {
                    tw = TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding).Width;
                }
                catch { tw = text.Length * (int)Math.Ceiling(f.SizeInPoints * 96f / 72f); }
                // +2 是字体 hinting 的余量：量出来的宽度与库内绘制宽度常差 1~2px，缺了会被判"文字被裁"
                b.Width = tw + px * 2 + DpiScale.V(host, 2);
                if (designHeight > 0)
                {
                    // 紧凑场景：设计高按 DPI 缩放，但不得小于"行高 + 上下留白"（否则文字被裁）
                    int min = LineBox(f, host) + py * 2;
                    b.Height = Math.Max(Math.Max(DpiScale.V(host, designHeight), min), 1);
                }
                else
                {
                    b.Height = FieldHeight(host);
                }
            }

            Apply();
            // AutoSize 关掉后宽度是定值，文字后改（「显示/隐藏」「下一步 → 开始设置」
            // 「⚠ 确认执行 ⚠」及倒计时）必须重量宽——否则长文案在固定宽里被裁。
            b.TextChanged += (s, e) => Apply();
            // 字体后改同理，而且更隐蔽：构造期按钮继承的还是父容器的当前字体，
            // FormFontPolicy.Apply 在窗体末尾才把 14pt 落下来 —— 于是宽度是 9pt 量的、
            // 画的时候是 14pt 的字。UI 字号调大到 14pt 后，这一族按钮整排被裁
            //（CI 矩阵 f14 格实证：'打开终端' 需 88px 只有 81px）。
            b.FontChanged += (s, e) => Apply();
        }

        /// <summary>
        /// 单行标签按真实字宽定宽（关掉 AntdUI.Label 的 AutoSize）。
        ///
        /// 为什么不能用 AutoSize：AntdUI.Label 的 AutoSize 用库内 sps 量宽，比 GDI 实测窄约 10%
        /// （CI dump 实证：「还没有命令历史」需 100px 只给 89px，「共 0 条记录」需 67px 只给 64px，
        /// 「没有匹配的条目」需 100px 只给 89px）——超出的字符直接裁掉，且不报错。
        /// 与 R16（AntdUI.Button 的 AutoSize 不吃 Padding）同一族。
        ///
        /// 只定宽、不动高：高度由 Dock/显式 Height 决定，贸然改高会推动整片布局。
        /// </summary>
        /// <param name="lbl">目标标签（AntdUI.Label / HyperlinkLabel 等同族；按 Control 约束收口）。</param>
        /// <param name="host">取 DPI 的宿主；空则用标签自身。</param>
        /// <param name="padX">左右各留的最小内边距（设计 px，按 DPI 缩放）。</param>
        /// <returns>同一个标签，便于写成 <c>= FitSingleLineLabel(new AntdUI.Label {...})</c>（字段初始化器里只有表达式可用）。</returns>
        public static T FitSingleLineLabel<T>(T lbl, Control host = null, int padX = 6) where T : Control
        {
            if (lbl == null) return null;
            lbl.AutoSize = false;

            void Apply()
            {
                if (lbl.IsDisposed) return;
                var f = lbl.Font ?? UiFont();
                var h = host ?? lbl;
                string text = lbl.Text ?? string.Empty;
                int tw = 0;
                foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    int w;
                    try
                    {
                        w = TextRenderer.MeasureText(line, f, new Size(int.MaxValue, int.MaxValue),
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                    }
                    catch
                    {
                        w = line.Length * (int)Math.Ceiling(f.SizeInPoints * 96f / 72f);
                    }
                    if (w > tw) tw = w;
                }
                if (tw <= 0)
                {
                    // 空文案收成 0 宽：AutoSize 关掉后若留默认宽，流式布局里会白占一行空间
                    lbl.Width = 0;
                    return;
                }
                int px = Math.Max(0, DpiScale.V(h, padX));
                var pad = lbl.Padding;
                lbl.Width = tw + Math.Max(pad.Left, px) + Math.Max(pad.Right, px) + DpiScale.V(h, 2);
                // 高度只托底不压缩：AntdUI 的 AutoSize 高度≈em 盒（无 leading），
                // 关掉之后若沿用更矮的默认高，字形下缘会被裁
                if (!lbl.AutoSize)
                {
                    int minH = LineBox(f, h);
                    if (lbl.Height < minH && lbl.Dock != DockStyle.Left && lbl.Dock != DockStyle.Right) lbl.Height = minH;
                }
            }

            Apply();
            // 文案后改（「共 N 条记录」「3/12」这类计数）必须重量宽，否则长文案在定宽里被裁
            lbl.TextChanged += delegate { Apply(); };
            // 字体后改同上一族：构造期继承的是父容器当前字体，字宽要跟着变
            lbl.FontChanged += delegate { Apply(); };
            return lbl;
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
            int h = CachedLineHeight(f, c);
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
            int h = CachedLineHeight(f, c);
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
            // 审计 C5：全局回填无障碍名称（只填空名，不覆盖显式命名）
            try { UiA11y.AutoName(form); } catch { }
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
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("FormFontPolicy", exSwallowed); } catch { } }
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

        /// <summary>
        /// P0-3：保留 size delta——子控件若已用 UiFont(+N) 设了标题字号，Apply 只换字体族、
        /// 按“当前字号相对旧基准的差”平移，不再压回全局基准（原先标题与正文一样大）。
        /// 基准 = 调用时的全局字号 size；delta = c.Font.Size - size。
        /// </summary>
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
                        float delta = f.Size - size;
                        float target = delta > 0.25f ? size + delta : size;
                        c.Font = new Font(name, target, f.Style);
                    }
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("FormFontPolicy", exSwallowed); } catch { } }
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

        // ───────────────────────── 等宽字体（代码/路径/密钥/密码语义）─────────────────────────

        private static readonly System.Collections.Generic.Dictionary<string, Font> MonoFontCache =
            new System.Collections.Generic.Dictionary<string, Font>(16);
        private static readonly object MonoFontLock = new object();

        /// <summary>
        /// 等宽字体族解析（Consolas → Cascadia Mono → Courier New → 系统默认）。
        /// 与 ResolveUiFamilyName 同一套“先探测再回退”的思路，避免字族不存在时 GDI+ 静默变宋体。
        /// </summary>
        private static string ResolveMonoFamilyName()
        {
            var candidates = new[] { "Consolas", "Cascadia Mono", "Courier New" };
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
            catch { /* 极端 GDI+ 异常 → 系统默认 */ }
            return SystemFonts.DefaultFont.FontFamily.Name;
        }

        /// <summary>
        /// 等宽字号（pt）：跟随全局 UI 字号但夹在 8–16pt。
        /// 背景（UI/UX 审计 2026-10-07 C3）：等宽文本原先直接吃 UIFontSize（8–24pt），
        /// 用户把字号调到 14/20pt 时 ListView 行高、AntdUI.Table 行高、固定字段高度不跟随，
        /// 必然裁切。夹顶 16pt 让极端字号下仍可读且不撑破固定行高。
        /// </summary>
        public static float MonoFontSize
        {
            get { return Math.Max(8f, Math.Min(16f, UiFontSize)); }
        }

        /// <summary>
        /// 等宽字体工厂——DL §2.1“UI 字体 vs 等宽字体”两套分离的唯一出口。
        ///
        /// 取代原先 40 处逐点复制的 <c>new Font("Consolas", … UIFontSize …)</c>
        /// （审计 C3：fallback 有 8.5/9/9.5/10/11/14 六种口径，且每处 new Font 都不 Dispose，
        /// 面板重建即泄漏 GDI 句柄）。新代码禁止再直接构造等宽 Font。
        ///
        /// 契约：
        ///   - 字号 = MonoFontSize × scale，夹 6–20pt；
        ///   - 按 (族, 字号, 样式) 缓存实例，返回的 Font <b>不得 Dispose</b>，直接赋给 Control.Font 即可；
        ///   - 字号变化（ApplyGlobalUIFont）时缓存键自然变化，旧实例由 GC 回收。
        /// </summary>
        /// <param name="scale">相对等宽基准的倍数（密码/密钥展示可 1.15）。</param>
        /// <param name="style">通常 Regular；需要强调时 Bold。</param>
        public static Font MonoFont(float scale = 1f, FontStyle style = FontStyle.Regular)
        {
            float size = MonoFontSize * (scale > 0f ? scale : 1f);
            if (size < 6f) size = 6f;
            if (size > 20f) size = 20f;
            string key = "mono|" + size.ToString("F2") + "|" + ((int)style);
            lock (MonoFontLock)
            {
                Font hit;
                if (MonoFontCache.TryGetValue(key, out hit)) return hit;
            }
            Font made;
            try { made = new Font(ResolveMonoFamilyName(), size, style); }
            catch { made = new Font(SystemFonts.DefaultFont.FontFamily, size, style); }
            lock (MonoFontLock)
            {
                if (MonoFontCache.Count >= 32) MonoFontCache.Clear();
                MonoFontCache[key] = made;
            }
            return made;
        }
    }
}
