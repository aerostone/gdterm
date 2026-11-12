using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Gdterm.Core;

namespace Gdterm.Tools.Modules
{
    /// <summary>
    /// 网络诊断（P2：ping / traceroute / DNS 查询）。
    ///
    /// 设计取舍：
    /// 1) **直接跑系统命令**，不用 .NET 的 Ping 类自己拼。理由是现场排障要的正是
    ///    "和我在 cmd 里敲出来一样"的证据——用户能把输出原样贴给同事/工单，
    ///    程序自己实现的 ICMP 输出格式和系统工具不一致，反而增加沟通成本。
    /// 2) 命令名按平台解析（<see cref="ResolveCommand"/>）：Windows 是 ping/tracert/nslookup，
    ///    Linux 是 ping/traceroute/dig。找不到可执行文件时**明确报"没装"**，不静默失败。
    /// 3) 输出**流式**追加（OutputDataReceived），不是等进程结束才一次性贴出来：
    ///    traceroute 一跑十几跳、ping 要等 4 秒，阻塞式读取会让用户以为卡死。
    /// 4) 一次只允许一个诊断在跑（<see cref="_running"/>），再点就提示先停——避免
    ///    用户连点五下平白起五个 ping 进程。
    /// </summary>
    public class NetworkDiagnosticsTool : IToolModule
    {
        private NetworkDiagnosticsConfig _config;
        private Process _current;
        private bool _running;

        public string ToolId { get { return "network-diagnostics"; } }
        public string DisplayName { get { return L10n.T("网络诊断"); } }
        public string Description { get { return L10n.T("ping / traceroute / DNS 查询（跑系统命令，输出原样可贴）"); } }
        public string Category { get { return L10n.T("网络"); } }

        public void LoadConfig() { _config.LoadFromFile(); }
        public void SaveConfig() { _config.SaveToFile(); }

        /// <summary>
        /// 平台命令解析：同一诊断在不同系统的可执行文件不同，且 unix 上 ping 常在
        /// /bin 而非 PATH（某些精简发行版）。返回 null 表示这台机器上没有。
        /// </summary>
        internal static string ResolveCommand(DiagnosticKind kind, Func<string, bool> exists)
        {
            string[] candidates;
            switch (kind)
            {
                case DiagnosticKind.Ping:
                    candidates = new[] { "ping" };
                    break;
                case DiagnosticKind.Traceroute:
                    candidates = new[] { "tracert", "traceroute" };
                    break;
                default:
                    candidates = new[] { "nslookup", "dig", "host" };
                    break;
            }
            if (exists == null) return candidates[0];
            foreach (var c in candidates)
            {
                try { if (exists(c)) return c; }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 参数拼装。**刻意不做 shell 字符串拼接**（不用 cmd /c）：目标名进参数列表而不是
        /// 一条命令行，注入面直接消失；同时保留 -n/-d 这类纯数字输出选项，避免 DNS 反解拖慢排障。
        /// 平台判定按"Windows 语义"显式传入：unix 上 `ping -n` 是"不反解"而不是"次数"，
        /// 靠 exe 名字猜会在 unix 上把次数选项变成数字输出选项——这是必须传参的原因。
        /// </summary>
        internal static List<string> BuildArguments(DiagnosticKind kind, string exe, string target, int count, bool isWindows)
        {
            var args = new List<string>();
            if (string.IsNullOrEmpty(target)) return args;
            if (count <= 0) count = 4;

            switch (kind)
            {
                case DiagnosticKind.Ping:
                    args.Add(isWindows ? "-n" : "-c");
                    args.Add(count.ToString());
                    args.Add(target);
                    break;
                case DiagnosticKind.Traceroute:
                    // tracert/traceroute 的"不反解"选项同为 -d/-n，但 Windows 的 tracert 没有 -n
                    args.Add(isWindows ? "-d" : "-n");
                    args.Add(target);
                    break;
                default:
                    args.Add(target);
                    break;
            }
            return args;
        }

        public Control CreatePanel()
        {
            var root = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(0) };

            var title = new Label
            {
                Text = DisplayName,
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = Color.FromArgb(220, 220, 220)
            };
            title.ParentChanged += (s, e) =>
            {
                var c = (Control)s;
                try { c.Font = new Font(c.Font.FontFamily, c.Font.Size + 3f, FontStyle.Bold); } catch { }
            };

            var desc = new Label
            {
                Text = Description,
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150)
            };

            var status = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 200, 120),
                Text = L10n.T("就绪")
            };

            var output = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(200, 200, 200),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = false
            };

            var targetBox = ToolPanelHelper.MakeTextBox(L10n.T("主机 / 域名，例：example.com 或 10.0.0.8"));
            targetBox.Dock = DockStyle.Top;

            var countBox = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 30,
                Value = _config.PingCount > 0 ? _config.PingCount : 4,
                Dock = DockStyle.Left,
                Width = 60,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true
            };
            bar.Controls.Add(MakeButton(L10n.T("Ping"), () => Run(DiagnosticKind.Ping, targetBox, countBox, output, status)));
            bar.Controls.Add(MakeButton(L10n.T("路由追踪"), () => Run(DiagnosticKind.Traceroute, targetBox, countBox, output, status)));
            bar.Controls.Add(MakeButton(L10n.T("DNS 查询"), () => Run(DiagnosticKind.Dns, targetBox, countBox, output, status)));
            bar.Controls.Add(MakeButton(L10n.T("停止"), () => { StopCurrent(); SetStatus(status, L10n.T("已停止"), false); }));
            bar.Controls.Add(countBox);

            root.Controls.Add(output);
            root.Controls.Add(bar);
            root.Controls.Add(targetBox);
            root.Controls.Add(desc);
            root.Controls.Add(title);
            root.Controls.Add(status);
            return root;
        }

        private static Button MakeButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                Margin = new Padding(0)
            };
            btn.Click += (s, e) => { try { onClick(); } catch (Exception) { /* 按钮回调内部已各自兜底，这里只防炸 UI 线程 */ } };
            return btn;
        }

        private void Run(DiagnosticKind kind, TextBox targetBox, NumericUpDown countBox, RichTextBox output, Label status)
        {
            var target = targetBox != null ? (targetBox.Text ?? "").Trim() : "";
            if (target.Length == 0) { SetStatus(status, L10n.T("请输入主机或域名"), false); return; }

            if (_running)
            {
                // 不排队不并发：并发只会让用户看不懂哪段输出属于哪次诊断
                ToolPanelHelper.AppendLine(output, L10n.T("[跳过] 上一次诊断还在跑，先点“停止”"));
                return;
            }

            var exe = ResolveCommand(kind, CommandExists);
            if (exe == null)
            {
                var missing = kind == DiagnosticKind.Traceroute ? "tracert/traceroute"
                            : (kind == DiagnosticKind.Dns ? "nslookup/dig" : "ping");
                ToolPanelHelper.AppendLine(output, string.Format(L10n.T("[缺失] 本机没找到 {0}，无法执行该诊断"), missing));
                SetStatus(status, L10n.T("缺少系统命令"), false);
                return;
            }

            int count = countBox != null ? (int)countBox.Value : 4;
            var args = BuildArguments(kind, exe, target, count,
                Environment.OSVersion.Platform == PlatformID.Win32NT);
            _config.PingCount = (int)(countBox != null ? countBox.Value : 4);
            try { _config.SaveToFile(); } catch { }

            ToolPanelHelper.AppendLine(output, "> " + exe + " " + string.Join(" ", args.ToArray()));
            SetStatus(status, L10n.T("执行中..."), true);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = EncodingForOutput()
                };
                foreach (var a in args) psi.Arguments += (psi.Arguments.Length > 0 ? " " : "") + a;

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) ToolPanelHelper.AppendLine(output, e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) ToolPanelHelper.AppendLine(output, e.Data); };
                proc.Exited += (s, e) =>
                {
                    var code = -1;
                    try { code = proc.ExitCode; } catch { }
                    _running = false;
                    _current = null;
                    SetStatus(status, code == 0 ? L10n.T("完成") : L10n.T("结束（退出码 ") + code + "）", code == 0);
                    try { proc.Dispose(); } catch { }
                };

                _current = proc;
                _running = true;
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _running = false;
                _current = null;
                ToolPanelHelper.AppendLine(output, L10n.T("启动失败: ") + ex.Message);
                SetStatus(status, L10n.T("失败"), false);
            }
        }

        /// <summary>
        /// 命令输出编码：中文 Windows 的 ping/tracert 按控制台代码页（cp936）输出，
        /// 直接按 UTF-8 解会得到一串乱码——这是"输出原样可贴"承诺的一部分。
        /// </summary>
        private static Encoding EncodingForOutput()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch { }
            return Encoding.UTF8;
        }

        /// <summary>可执行文件是否存在于 PATH（或绝对路径存在）。</summary>
        internal static bool CommandExists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return System.IO.File.Exists(name);
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(new[] { System.IO.Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var ext in new[] { "", ".exe" })
                    {
                        try
                        {
                            if (System.IO.File.Exists(System.IO.Path.Combine(dir.Trim('"'), name + ext))) return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        private void StopCurrent()
        {
            var proc = _current;
            if (proc == null) return;
            try { if (!proc.HasExited) proc.Kill(); }
            catch (Exception) { /* 进程可能已退出，kill 失败无信息量 */ }
            _running = false;
        }

        private static void SetStatus(Label status, string text, bool ok)
        {
            if (status == null) return;
            Action apply = () =>
            {
                status.Text = text;
                status.ForeColor = ok ? Color.FromArgb(120, 200, 120) : Color.FromArgb(255, 200, 80);
            };
            try
            {
                if (status.IsHandleCreated) status.BeginInvoke(apply); else apply();
            }
            catch { }
        }

        public void Dispose()
        {
            StopCurrent();
            _current = null;
        }
    }

    /// <summary>诊断类型</summary>
    public enum DiagnosticKind
    {
        Ping,
        Traceroute,
        Dns
    }

    /// <summary>网络诊断配置（只存一个小整数：上次用的次数）</summary>
    public class NetworkDiagnosticsConfig : ToolConfigBase
    {
        public int PingCount { get; set; }

        public override void ResetDefaults() { PingCount = 4; }

        protected override void LoadFromJson(string json)
        {
            PingCount = ExtractInt(json, "pingCount", 4);
            if (PingCount <= 0) PingCount = 4;
        }

        protected override string ToJson()
        {
            return "{\"pingCount\":" + PingCount + "}";
        }
    }
}
