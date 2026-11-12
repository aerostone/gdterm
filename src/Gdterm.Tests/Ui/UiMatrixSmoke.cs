using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// L2 矩阵冒烟（docs/UI-UX-VERIFICATION.md §4.2，此前标注为"最大缺口"）。
    ///
    /// 做同一件事在**不同显示条件下重跑**：同一批窗体/面板，在
    /// DPI 100%/150% × UI 字号 9/14pt × 主题 Dark/OLED 共 8 个格子里构造 + Show + dump。
    /// 几何仍由 tools/ui-tree-check.py 判（同一套 R1–R6），本文件只负责"换条件再产一遍证据"。
    ///
    /// 两条轴的性质必须说清楚（别把近似当真实）：
    ///   - 字号轴是**真实**的：UIFontSize 真的换了字体，TextRenderer 用真实 GDI 度量，
    ///     所以"14pt 下文字裁切/行重叠"这类历史事故在这里能被真判；
    ///   - DPI 轴是**受控近似**：CI 没有高 DPI 显示器（GHA windows-latest 恒 96dpi），
    ///     只能通过 DpiScale.SetOverrideForTests 注入系数，覆盖的是我们自己的 DpiScale 数学；
    ///     WinForms AutoScaleMode / PerMonitorV2 的系统缩放仍需真机矩阵（见文档"仍开放的缺口"）。
    ///
    /// 反假绿：每个格子的 dump 都真实落盘，且 tools/ui-matrix-check.py 会比对跨格子的几何
    /// 差异（字号 14 的控件总高必须显著大于字号 9）——条件没生效时矩阵会红，而不是"全过"。
    /// </summary>
    public static class UiMatrixSmoke
    {
        private static readonly string[] Themes = { "Dark", "OLED" };
        private static readonly float[] DpiFactors = { 1.0f, 1.5f };
        private static readonly float[] FontSizes = { 9f, 14f };

        /// <summary>
        /// 矩阵目标：2 个窗 + 6 个侧栏面板（覆盖表格/输入/按钮/标签/空态/进度条/多列表）。
        /// 每格跑全量同一批目标——tools/ui-matrix-check.py 会断言"同一批目标在每格都出现"，
        /// 少一个就是某格被静默跳过；那是防假绿的护栏，不能为了省时间改成按主题分片。
        /// </summary>
        private static readonly string[] Targets =
        {
            "keepass-manager", "keepass-unlock", "port-forward", "command-history",
            "secret-scan", "toolbox", "transfer-center", "health-monitor"
        };

        private static int _fails;

        public static int Run(string[] args)
        {
            string outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "ui-matrix";
            Directory.CreateDirectory(outDir);
            Console.WriteLine("=== gdterm L2 matrix (DPI x font x theme) -> " + outDir + " ===");

            var prevAppearance = Gdterm.UI.Program.GlobalAppearance;
            string prevTheme = "Dark";
            int combos = 0, dumps = 0;
            try
            {
                foreach (var theme in Themes)
                {
                    foreach (var dpi in DpiFactors)
                    {
                        foreach (var font in FontSizes)
                        {
                            string combo = string.Format("{0}-d{1}-f{2}",
                                theme, (int)Math.Round(dpi * 100), (int)font);
                            string dir = Path.Combine(outDir, combo);
                            Directory.CreateDirectory(dir);
                            Apply(theme, dpi, font);
                            Console.WriteLine("[combo] " + combo);
                            foreach (var t in Targets)
                            {
                                try
                                {
                                    Dump(dir, t, font, dpi);
                                    dumps++;
                                }
                                catch (Exception ex)
                                {
                                    _fails++;
                                    Console.WriteLine("  [FAIL] " + t + ": " + UiSmokeRunner.FlatEx(ex));
                                }
                            }
                            File.WriteAllText(Path.Combine(dir, "combo.json"),
                                "{\"theme\":\"" + theme + "\",\"dpiFactor\":" +
                                dpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                                ",\"uiFontSize\":" +
                                font.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "}",
                                System.Text.Encoding.UTF8);
                            combos++;
                        }
                    }
                }
            }
            finally
            {
                Gdterm.UI.Services.DpiScale.SetOverrideForTests(0f);
                Gdterm.UI.Program.GlobalAppearance = prevAppearance;
                try { GdtermColorTable.ApplyTheme(prevTheme); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("matrix combos={0} dumps={1} fails={2}", combos, dumps, _fails);
            return _fails > 0 ? 1 : 0;
        }

        /// <summary>注入一条显示条件：主题 + DPI 系数 + 全局 UI 字号。</summary>
        private static void Apply(string theme, float dpi, float font)
        {
            GdtermColorTable.ApplyTheme(theme);
            Gdterm.UI.Services.DpiScale.SetOverrideForTests(dpi);
            var ga = Gdterm.UI.Program.GlobalAppearance;
            if (ga == null)
            {
                ga = new AppearanceSettings();
                Gdterm.UI.Program.GlobalAppearance = ga;
            }
            ga.UIFontSize = (int)Math.Round(font); // 外观设置里 UIFontSize 是整型 pt
        }

        private static void Dump(string dir, string target, float font, float dpi)
        {
            switch (target)
            {
                case "keepass-manager":
                    using (var f = new KeePassManagerForm(new FakeKeePassService()))
                        ShowAndDump(f, dir, target, font, dpi);
                    break;
                case "keepass-unlock":
                    using (var f = new KeePassUnlockForm(new FakeKeePassService()))
                        ShowAndDump(f, dir, target, font, dpi);
                    break;
                case "port-forward":
                    using (var host = PanelHost())
                    using (var pm = new Gdterm.Tunnel.PortForwardManager())
                    {
                        host.Controls.Add(new PortForwardPanel(pm) { Dock = DockStyle.Fill, Visible = true });
                        ShowAndDump(host, dir, target, font, dpi);
                    }
                    break;
                case "command-history":
                    using (var host = PanelHost())
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "gdterm-matrix-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tmp);
                        try
                        {
                            host.Controls.Add(new CommandHistoryPanel(
                                new Gdterm.Logging.CommandHistoryStore(tmp)) { Dock = DockStyle.Fill, Visible = true });
                            ShowAndDump(host, dir, target, font, dpi);
                        }
                        finally { try { Directory.Delete(tmp, true); } catch { } }
                    }
                    break;
                case "secret-scan":
                    // 分数 Label 用 +11pt 粗体、按钮 3 个、上方还有进度条——
                    // 这是 14pt 字号下最容易被顶出侧栏的一页（10 分钟内改过两次布局）
                    using (var scanner = new Gdterm.Security.SecretScanner(
                        Gdterm.Security.SecretScanConfig.GetDefault()))
                    using (var host = PanelHost())
                    {
                        host.Controls.Add(new SecretScanPanel(scanner, null) { Dock = DockStyle.Fill, Visible = true });
                        ShowAndDump(host, dir, target, font, dpi);
                    }
                    break;
                case "toolbox":
                    using (var registry = new Gdterm.Tools.ToolRegistry())
                    using (var host = PanelHost())
                    {
                        host.Controls.Add(new ToolboxPanel(registry) { Dock = DockStyle.Fill, Visible = true });
                        ShowAndDump(host, dir, target, font, dpi);
                    }
                    break;
                case "transfer-center":
                    using (var host = PanelHost())
                    {
                        host.Controls.Add(new TransferCenterPanel { Dock = DockStyle.Fill, Visible = true });
                        ShowAndDump(host, dir, target, font, dpi);
                    }
                    break;
                case "health-monitor":
                    using (var host = PanelHost())
                    {
                        host.Controls.Add(new HealthMonitorPanel { Dock = DockStyle.Fill, Visible = true });
                        ShowAndDump(host, dir, target, font, dpi);
                    }
                    break;
                default:
                    throw new ArgumentException("unknown matrix target: " + target);
            }
        }

        /// <summary>侧栏面板的宿主窗：360×480 贴近真实侧栏几何，与 UiSmokeRunner 的口径一致。</summary>
        private static Form PanelHost()
        {
            // 宿主窗也要显式吃 UI 字体：以前这里不设 Font，为了让"字体已生效"的断言不误红，
            // 断言里专门把 port-forward/command-history 排除掉——等于 8 个目标里 6 个不设防
            // （面板自己设字体不代表宿主设了，字号轴退化会被漏掉）。现在宿主也设，
            // 断言对全部 8 个目标生效。
            return new Form
            {
                Text = "matrix-host",
                ClientSize = new Size(360, 480),
                BackColor = GdtermColorTable.Background,
                Font = Gdterm.UI.Services.FormFontPolicy.UiFont(),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0)
            };
        }

        private static void ShowAndDump(Form f, string dir, string target, float font, float dpi)
        {
            f.Show();
            f.BringToFront();
            Application.DoEvents();
            Thread.Sleep(350);
            Application.DoEvents();

            // 反空转：字体必须真的落到窗体上（FormFontPolicy 读 GlobalAppearance，若被缓存/绕过，
            // 字号轴会安静地退化成"同一张图跑 8 遍"，这行断言就是那道防线）。
            if (f.Font != null && Math.Abs(f.Font.SizeInPoints - font) > 0.6f)
                throw new Exception("font not applied to form: " + f.Font.SizeInPoints + "pt != " + font + "pt");
            if (Math.Abs(Gdterm.UI.Services.DpiScale.Factor(f) - dpi) > 0.01f)
                throw new Exception("dpi override not in effect: " +
                    Gdterm.UI.Services.DpiScale.Factor(f) + " != " + dpi);

            int n = UiSmokeRunner.CountControls(f);
            if (n <= 3) throw new Exception("controls=" + n + " (<=3)");

            File.WriteAllText(Path.Combine(dir, target + ".json"),
                UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
            UiSmokeRunner.Shot(f, Path.Combine(dir, target + ".png"));
        }
    }
}
