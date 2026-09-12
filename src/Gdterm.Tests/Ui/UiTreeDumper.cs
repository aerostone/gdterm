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
            sb.Append("\"dpi\":").Append(DpiOf(form)).Append(",");
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
            sb.Append("\"visible\":").Append(c.Visible ? "true" : "false").Append(",");
            sb.Append("\"enabled\":").Append(c.Enabled ? "true" : "false").Append(",");
            sb.Append("\"font\":\"").Append(Esc(c.Font.Name + " " + c.Font.SizeInPoints.ToString("0.##") + "pt")).Append("\",");
            // AntdUI 特有：RowHeight / BorderWidth / Type（反射读，有则记）
            sb.Append("\"extra\":{");
            BeginExtra();
            AppendProp(c, sb, "RowHeight");
            AppendProp(c, sb, "BorderWidth");
            AppendProp(c, sb, "Type");
            AppendProp(c, sb, "RowCount");
            AppendProp(c, sb, "AutoScroll");
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
