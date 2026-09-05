using System;
using System.Drawing;
using System.Windows.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// C/S 客户端设计语言辅助——把“GitHub Dark + 终端绿”视觉规范沉淀成可复用的工厂方法。
    ///
    /// 背景（2026-09 布局治理）：此前十余个手写对话框各自 new Button/new Label 再
    /// 逐个设置颜色/FlatStyle/边框，结果样式漂移（蓝按钮/灰按钮混用、圆角直角不一、
    /// 硬编码字体），且换主题时无法统一收口。本类是唯一的样式入口：
    ///
    ///   - 颜色一律取 GdtermColorTable（Background/Surface/Border/Accent/…），
    ///     禁止再写 Color.FromArgb 的外壳色；
    ///   - 字体一律 FormFontPolicy（全局 UI 字体 + Win7 回退链），禁止硬编码字族；
    ///   - 文本控件 AutoSize + RowStep 行距（字体驱动，见 FormFontPolicy.RowStep）；
    ///   - 按钮分三级：Primary（终端绿实心，每窗体至多一个）/ Secondary（Surface 面）
    ///     / Danger（红系，破坏性操作）；
    ///   - 对话框底边 1px Border 分隔线 + 右对齐按钮条（Windows 惯例：主按钮最右）。
    ///
    /// 用法（手写窗体末尾）：
    ///   DialogStyle.ApplyChrome(this, 440, preferredClientHeight);
    ///   DialogStyle.MakePrimary(btnOk); DialogStyle.MakeSecondary(btnCancel);
    ///   Controls.Add(DialogStyle.ButtonStrip(btnOk, btnCancel));
    /// </summary>
    public static class DialogStyle
    {
        /// <summary>
        /// 暗色窗体外壳：背景/前景/字体/DPI 缩放的 ClientSize/对话框边框/居中。
        /// preferredClientHeight 是按 9pt 设计的期望高度；实际高度会被放大到
        /// “全局字号相对 9pt 的比例”，保证字号调大后客户区同步变大。
        /// </summary>
        public static void ApplyChrome(Form f, int designClientWidth, int designClientHeight)
        {
            if (f == null) return;
            f.BackColor = GdtermColorTable.Background;
            f.ForeColor = GdtermColorTable.Foreground;
            f.Font = FormFontPolicy.UiFont();

            float grow = FormFontPolicy.UiFontSize / 9f;
            int w = DpiScale.V(f, designClientWidth);
            int h = DpiScale.V(f, (int)Math.Round(designClientHeight * Math.Max(1f, grow)));
            try { f.ClientSize = new Size(w, h); } catch { }

            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
        }

        /// <summary>主操作按钮——终端绿实心。每个窗体至多一个（单一 CTA 原则）。</summary>
        public static void MakePrimary(Button b)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = GdtermColorTable.Accent;
            b.ForeColor = Color.Black; // 绿底黑字对比度最高（纯绿 #00FF41 上白字仅 ~1.4:1）
            b.FlatAppearance.MouseOverBackColor = GdtermColorTable.Pressed;
            b.FlatAppearance.MouseDownBackColor = GdtermColorTable.Pressed;
            EnsureButtonAutoSize(b);
        }

        /// <summary>次级按钮——Surface 底色 + 1px 边框（取消/关闭/辅助操作）。</summary>
        public static void MakeSecondary(Button b)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = GdtermColorTable.Border;
            b.BackColor = GdtermColorTable.Surface;
            b.ForeColor = GdtermColorTable.Foreground;
            b.FlatAppearance.MouseOverBackColor = GdtermColorTable.Hover;
            b.FlatAppearance.MouseDownBackColor = GdtermColorTable.Pressed;
            EnsureButtonAutoSize(b);
        }

        /// <summary>危险操作按钮——红系（删除/移除/断开）。</summary>
        public static void MakeDanger(Button b)
        {
            if (b == null) return;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(0xEF, 0x44, 0x44);
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xB9, 0x1C, 0x1C);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0x99, 0x1B, 0x1B);
            EnsureButtonAutoSize(b);
        }

        /// <summary>
        /// 底部按钮条：底边 1px 分隔线之上、右对齐排列（Windows 惯例主按钮在最右）。
        /// 按钮 AutoSize，高度随字体与 DPI 自动变化，条高 = 按钮高 + 上下留白。
        /// </summary>
        public static Panel ButtonStrip(params Button[] buttons)
        {
            var strip = new Panel
            {
                Dock = DockStyle.Bottom,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(DpiScale.V(null, 12), DpiScale.V(null, 8), DpiScale.V(null, 12), DpiScale.V(null, 8))
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoScroll = false,
                BackColor = GdtermColorTable.Background
            };
            foreach (var b in buttons)
            {
                if (b != null)
                {
                    b.Margin = new Padding(DpiScale.V(null, 4), 0, DpiScale.V(null, 4), 0);
                    flow.Controls.Add(b);
                }
            }

            // 分隔线画在条顶（1px Border 色）
            strip.Paint += (s, e) =>
            {
                using (var pen = new Pen(GdtermColorTable.Border))
                    e.Graphics.DrawLine(pen, 0, 0, strip.Width, 0);
            };

            strip.Controls.Add(flow);
            // 条高按按钮实际高度定
            int btnH = 0;
            foreach (var b in buttons) { if (b != null) btnH = Math.Max(btnH, b.PreferredSize.Height); }
            strip.Height = Math.Max(DpiScale.V(null, 36), btnH + DpiScale.V(null, 20));
            return strip;
        }

        /// <summary>表单行标签——Muted 色、AutoSize、右对齐（配 TableLayoutPanel 标签列）。</summary>
        public static Label FieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(3, DpiScale.V(null, 8), 3, 0)
            };
        }

        /// <summary>表单输入控件统一暗色（TextBox/NumericUpDown/ComboBox 通用设置）。</summary>
        public static void ApplyInput(Control input)
        {
            if (input == null) return;
            input.BackColor = GdtermColorTable.Surface;
            input.ForeColor = GdtermColorTable.Foreground;
            var tb = input as TextBoxBase;
            if (tb != null) tb.BorderStyle = BorderStyle.FixedSingle;
        }

        /// <summary>分组标题——全局字号 +0.5 粗体。</summary>
        public static Label GroupTitle(string text)
        {
            var l = new AntdUI.Label {
                Text = text,
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont(0.5f, FontStyle.Bold),
                Margin = new Padding(3, DpiScale.V(null, 6), 3, 2)
            };
            return l;
        }

        private static void EnsureButtonAutoSize(Button b)
        {
            b.AutoSize = true;
            b.Padding = new Padding(DpiScale.V(null, 10), 0, DpiScale.V(null, 10), 0);
            b.MinimumSize = new Size(0, DpiScale.V(null, 26));
        }
    }
}
