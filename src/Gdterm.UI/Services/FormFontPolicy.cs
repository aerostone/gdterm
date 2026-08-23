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
        public static void Apply(Form form)
        {
            if (form == null) return;
            var ga = Gdterm.UI.Program.GlobalAppearance;
            var name = ga != null && !string.IsNullOrWhiteSpace(ga.UIFontName)
                ? ga.UIFontName : "Microsoft YaHei UI";
            float size = ga != null && ga.UIFontSize > 0 ? ga.UIFontSize : 9f;

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
                        && f.Name.StartsWith("Microsoft YaHei", StringComparison.OrdinalIgnoreCase))
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
