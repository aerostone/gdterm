using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.Tools
{
    /// <summary>
    /// 通用工具面板工厂——为没有专用 UI 的工具生成暗色操作面板
    /// </summary>
    public static class ToolPanelHelper
    {
        public static Control CreateActionPanel(
            string title,
            string description,
            Action<TextBox, RichTextBox> onBuildInputs,
            Action<TextBox[], RichTextBox, Label> onRun)
        {
            var root = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(10)
            };

            var lblTitle = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                AutoSize = true,
                // Gdterm.Tools 不引用 Gdterm.UI（避免反向依赖），这里跟随主窗体环境字体即可；
                // 标题强调用相对写法（规范规则③），构造时字体尚未挂到父链，延迟到 ParentChanged 生效
                ForeColor = Color.FromArgb(220, 220, 220)
            };
            lblTitle.ParentChanged += (s, e) =>
            {
                var c = (Control)s;
                try { c.Font = new Font(c.Font.FontFamily, c.Font.Size + 3f, FontStyle.Bold); }
                catch { }
            };

            var lblDesc = new Label
            {
                Text = description ?? "",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Color.FromArgb(150, 150, 150)
            };

            var inputHost = new Panel
            {
                Dock = DockStyle.Top,
                Height = 90,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            var status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = Color.FromArgb(120, 200, 120),
                Text = "就绪"
            };

            var output = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Consolas", 9f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None
            };

            var inputs = new System.Collections.Generic.List<TextBox>();
            if (onBuildInputs != null)
            {
                // 提供一个可追加输入框的容器
                var flow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false
                };
                inputHost.Controls.Add(flow);

                // finding-16：移除孤儿 dummy TextBox（原 new TextBox { Visible = false } 未挂到任何父容器）；
                // 现存 5 个调用方均传 null，此分支仅保留扩展点语义（null 占位）
                onBuildInputs(null, output);

                // 简化：标准两行输入
            }

            // 标准：Host / Args
            var hostBox = MakeTextBox("目标/参数");
            hostBox.Dock = DockStyle.Top;
            inputHost.Controls.Add(hostBox);
            inputs.Add(hostBox);

            var runBtn = new Button
            {
                Text = "执行",
                Dock = DockStyle.Right,
                Width = 80,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            runBtn.Click += (s, e) =>
            {
                try
                {
                    status.Text = "执行中...";
                    status.ForeColor = Color.FromArgb(255, 200, 80);
                    onRun?.Invoke(inputs.ToArray(), output, status);
                }
                catch (Exception ex)
                {
                    status.Text = "失败: " + ex.Message;
                    status.ForeColor = Color.FromArgb(255, 100, 100);
                    output.AppendText(ex.Message + Environment.NewLine);
                }
            };

            var bar = new Panel { Dock = DockStyle.Top, Height = 32 };
            bar.Controls.Add(runBtn);

            root.Controls.Add(output);
            root.Controls.Add(status);
            root.Controls.Add(bar);
            root.Controls.Add(inputHost);
            root.Controls.Add(lblDesc);
            root.Controls.Add(lblTitle);
            return root;
        }

        public static TextBox MakeTextBox(string placeholder)
        {
            return new TextBox
            {
                Width = 400,
                Height = 26,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Text = placeholder,
                Margin = new Padding(0, 4, 0, 4)
            };
        }

        public static void AppendLine(RichTextBox box, string text)
        {
            if (box == null) return;
            if (box.InvokeRequired)
            {
                box.BeginInvoke(new Action(() => AppendLine(box, text)));
                return;
            }
            box.AppendText(text + Environment.NewLine);
            box.ScrollToCaret();
        }
    }
}
