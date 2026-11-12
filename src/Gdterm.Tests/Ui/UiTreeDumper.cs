using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 全控件树盒模型 dump：递归窗体（含 layout 容器、按钮边距、绝对/相对坐标），输出 JSON。
    /// 零 NuGet：手拼 JSON，不依赖序列化库。
    /// 绝对坐标 = RectangleToScreen(相对 Bounds)；相对 = 相对父容器的 Bounds；
    /// 盒模型 = Bounds + Margin + Padding + ClientSize + Dock/Anchor/AutoSize/Visible。
    /// </summary>
    public static class UiTreeDumper
    {
        public static string Dump(Form form)
        {
            var sb = new StringBuilder(65536);
            sb.Append("{\"form\":\"").Append(Esc(form.GetType().Name)).Append("\",");
            sb.Append("\"text\":\"").Append(Esc(form.Text)).Append("\",");
            sb.Append("\"clientSize\":").Append(SizeJson(form.ClientSize)).Append(",");
            // 客户区(0,0) 的屏幕坐标：截图是 PrintWindow 抓客户区，像素坐标 = abs - 这个原点。
            // 没有它就没法把 dump 里的控件盒对到截图像素上，"白块"这类只能靠像素发现的问题无从自动判。
            try
            {
                var org = form.PointToScreen(new Point(0, 0));
                sb.Append("\"clientOrigin\":{\"x\":").Append(org.X).Append(",\"y\":").Append(org.Y).Append("},");
            }
            catch { }
            sb.Append("\"dpi\":").Append(DpiOf(form)).Append(",");
            // 主题名 + 每控件实际色值：判"暗色主题下还留白底/黑字"（旧色残留）只靠截图看不出来，
            // 有了色值就能在 R7 里逐控件判，也能让矩阵主题轴有可比的证据。
            sb.Append("\"theme\":\"").Append(Esc(Gdterm.UI.Diagnostics.GdtermColorTable.CurrentThemeName)).Append("\",");
            sb.Append("\"back\":\"").Append(ColorHex(form.BackColor)).Append("\",");
            sb.Append("\"fore\":\"").Append(ColorHex(form.ForeColor)).Append("\",");
            sb.Append("\"controls\":[");
            bool first = true;
            foreach (Control c in form.Controls)
                DumpControl(c, sb, ref first, 0);
            sb.Append("]}");
            return sb.ToString();
        }

        private static void DumpControl(Control c, StringBuilder sb, ref bool first, int depth)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("{\"depth\":").Append(depth).Append(",");
            sb.Append("\"type\":\"").Append(Esc(c.GetType().FullName)).Append("\",");
            sb.Append("\"name\":\"").Append(Esc(c.Name)).Append("\",");
            sb.Append("\"text\":\"").Append(Esc(ShortText(c.Text))).Append("\",");
            // 相对盒（相对父容器）
            sb.Append("\"bounds\":").Append(RectJson(c.Bounds)).Append(",");
            // 绝对盒（屏幕坐标）
            Rectangle abs;
            try { abs = c.Parent == null ? c.Bounds : c.Parent.RectangleToScreen(c.Bounds); }
            catch { abs = c.Bounds; }
            sb.Append("\"abs\":").Append(RectJson(abs)).Append(",");
            sb.Append("\"clientSize\":").Append(SizeJson(c.ClientSize)).Append(",");
            sb.Append("\"margin\":").Append(PadJson(c.Margin)).Append(",");
            sb.Append("\"padding\":").Append(PadJson(c.Padding)).Append(",");
            sb.Append("\"dock\":\"").Append(c.Dock).Append("\",");
            sb.Append("\"anchor\":\"").Append(c.Anchor).Append("\",");
            sb.Append("\"autoSize\":").Append(c.AutoSize ? "true" : "false").Append(",");
            sb.Append("\"tabIndex\":").Append(c.TabIndex).Append(",");
            sb.Append("\"tabStop\":").Append(c.TabStop ? "true" : "false").Append(",");
            sb.Append("\"visible\":").Append(c.Visible ? "true" : "false").Append(",");
            sb.Append("\"enabled\":").Append(c.Enabled ? "true" : "false").Append(",");
            sb.Append("\"font\":\"").Append(Esc(c.Font.Name + " " + c.Font.SizeInPoints.ToString("0.##") + "pt")).Append("\",");
            sb.Append("\"back\":\"").Append(ColorHex(c.BackColor)).Append("\",");
            sb.Append("\"fore\":\"").Append(ColorHex(c.ForeColor)).Append("\",");
            // 文本适配度量（审计 C7-1）：单行所需宽 / 换行所需高 / 是否可换行。
            // 历史最高频 bug 是“文字被裁”，此前 dump 只有 Bounds 没有文字需求尺寸，
            // 因此这类 bug 只能靠人眼看截图。有了这三个数，ui-tree-check R6 就能自动判。
            AppendTextMetrics(c, sb);
            // 自绘命中区（审计 C5/C7-③）：IHitRegionProvider 控件的交互区不是 Control 节点，
            // 没有这个字段 R1 永远报“全部达标”而标签关闭钮点不准。
            AppendHitRegions(c, sb);
            // AntdUI 特有：RowHeight / BorderWidth / Type（反射读，有则记）
            sb.Append("\"extra\":{");
            BeginExtra();
            AppendProp(c, sb, "RowHeight");
            AppendProp(c, sb, "BorderWidth");
            AppendProp(c, sb, "Type");
            AppendProp(c, sb, "RowCount");
            AppendProp(c, sb, "AutoScroll");
            AppendProp(c, sb, "DrawMode");
            sb.Append("},");
            // 子树：先走 Controls；AntdUI.Window 自绘容器可能把内容藏在内部字段，穿透找
            sb.Append("\"children\":[");
            bool cf = true;
            try
            {
                foreach (Control ch in c.Controls)
                    DumpControl(ch, sb, ref cf, depth + 1);
                if (c.Controls.Count == 0)
                {
                    foreach (var ch in InnerControls(c))
                        DumpControl(ch, sb, ref cf, depth + 1);
                }
            }
            catch { }
            sb.Append("]}");
        }

        /// <summary>色值转 #RRGGBB（有透明度时 #AARRGGBB）；未设置时给空串。</summary>
        private static string ColorHex(Color c)
        {
            try
            {
                if (c.IsEmpty) return "";
                return c.A == 255
                    ? "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2")
                    : "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            }
            catch { return ""; }
        }

        /// <summary>
        /// 写 textSingleW（不换行所需的像素宽）、textWrapH（按可用宽换行所需高）、wrap（能否换行）。
        /// 用真实 GDI 度量（TextRenderer + 控件自身 Font），不是估算。
        /// </summary>
        private static void AppendTextMetrics(Control c, StringBuilder sb)
        {
            int singleW = 0, wrapH = 0;
            bool wrap = false;
            try
            {
                string text = c.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    // 按显式换行切行后取最宽行——否则 SetupWizard 那类「\r\n 分段说明」
                    // 会被整串量成一行（实测 1411px vs 可用 514px），R6 报的是假溢出。
                    foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    {
                        int w = TextRenderer.MeasureText(line, c.Font, new Size(int.MaxValue, int.MaxValue),
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                        if (w > singleW) singleW = w;
                    }
                    wrap = CanWrap(c);
                    if (wrap)
                    {
                        int availW = Math.Max(1, c.ClientSize.Width - c.Padding.Left - c.Padding.Right);
                        wrapH = TextRenderer.MeasureText(text, c.Font, new Size(availW, int.MaxValue),
                            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
                    }
                }
            }
            catch { }
            sb.Append("\"textSingleW\":").Append(singleW).Append(",");
            sb.Append("\"textWrapH\":").Append(wrapH).Append(",");
            sb.Append("\"wrap\":").Append(wrap ? "true" : "false").Append(",");
        }

        /// <summary>
        /// 写 hitRegions（自绘交互命中区，屏幕坐标）。非 IHitRegionProvider 控件不写该字段；
        /// 异常吞掉（dump 不得因命中区采集失败而整棵树丢失）。
        /// </summary>
        private static void AppendHitRegions(Control c, StringBuilder sb)
        {
            var p = c as Gdterm.UI.Services.IHitRegionProvider;
            if (p == null) return;
            sb.Append("\"hitRegions\":[");
            bool first = true;
            try
            {
                foreach (var hr in p.GetHitRegions())
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"label\":\"").Append(Esc(hr.Label)).Append("\",");
                    sb.Append("\"abs\":").Append(RectJson(hr.Bounds)).Append("}");
                }
            }
            catch { }
            sb.Append("],");
        }

        /// <summary>文本是否会按宽度换行（换行后溢出判据变成高度而不是宽度）。</summary>
        private static bool CanWrap(Control c)
        {
            var tb = c as TextBoxBase;
            if (tb != null) return tb.Multiline;
            var lab = c as Label;
            if (lab != null) return !lab.AutoSize;
            var rtb = c as RichTextBox;
            if (rtb != null) return true;
            return false;
        }

        private static void AppendProp(Control c, StringBuilder sb, string prop)
        {
            try
            {
                var pi = c.GetType().GetProperty(prop);
                if (pi == null) return;
                object v = pi.GetValue(c, null);
                if (_extraCount > 0) sb.Append(",");
                sb.Append("\"").Append(prop).Append("\":\"").Append(Esc(v == null ? "null" : v.ToString())).Append("\"");
                _extraCount++;
            }
            catch { }
        }

        [System.ThreadStatic]
        private static int _extraCount;

        private static void BeginExtra() { _extraCount = 0; }

        private static System.Collections.Generic.IEnumerable<Control> InnerControls(Control c)
        {
            // AntdUI.Window/BaseForm 内部容器字段名随版本变，逐个试
            // （yield 不能在 try/catch 里：先收集再返回）
            var found = new System.Collections.Generic.List<Control>();
            string[] fields = { "innerPanel", "_innerPanel", "panel", "_panel", "container", "_container", "bodyPanel", "_body", "contentPanel" };
            foreach (var fn in fields)
            {
                try
                {
                    var fi = c.GetType().GetField(fn, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (fi == null) continue;
                    if (fi.GetValue(c) is Control inner && inner != c)
                    {
                        foreach (Control ch in inner.Controls) found.Add(ch);
                        break;
                    }
                    if (fi.GetValue(c) is System.Collections.IEnumerable list)
                    {
                        foreach (var it in list) if (it is Control cc && cc != c) found.Add(cc);
                        break;
                    }
                }
                catch { }
            }
            return found;
        }

        private static int DpiOf(Control c)
        {
            try
            {
                using (var g = c.CreateGraphics()) return (int)g.DpiX;
            }
            catch { return 96; }
        }

        private static string ShortText(string t)
        {
            if (t == null) return "";
            t = t.Replace("\r", " ").Replace("\n", " ");
            return t.Length > 60 ? t.Substring(0, 60) : t;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string RectJson(Rectangle r)
        {
            return "{\"x\":" + r.X + ",\"y\":" + r.Y + ",\"w\":" + r.Width + ",\"h\":" + r.Height + "}";
        }

        private static string SizeJson(Size s)
        {
            return "{\"w\":" + s.Width + ",\"h\":" + s.Height + "}";
        }

        private static string PadJson(Padding p)
        {
            return "{\"l\":" + p.Left + ",\"t\":" + p.Top + ",\"r\":" + p.Right + ",\"b\":" + p.Bottom + "}";
        }
    }
}
