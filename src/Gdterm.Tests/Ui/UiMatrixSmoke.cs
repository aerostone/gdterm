using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.UI.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// L2 矩阵冒烟（docs/UI-UX-VERIFICATION.md §4.2）。
    ///
    /// 同一批 UI 在**不同显示条件下重跑**：几何轴（DPI 100%/150% × UI 字号 9/14pt，主题固定 Dark）
    /// + 主题轴（OLED / Darker 各跑一格基线几何）。几何仍由 tools/ui-tree-check.py 判（同一套 R1–R6），
    /// 本文件只负责"换条件再产一遍证据"。
    ///
    /// 为什么要拆成两条轴而不是继续 2×2×2 全交叉（2026-11 决定）：
    ///   主题对几何几乎没有影响，却要按全交叉再乘一遍——一旦目标从 12 页扩到 38 页，
    ///   全交叉会变成 38×2×2×3=456 张图。拆开之后几何 4 格 + 主题 2 格 = 6 格 ×38 = 228，
    ///   覆盖的东西一样（每页都有 4 个几何点 + 3 个主题点），成本减半。
    ///
    /// 两条轴的性质必须说清楚（别把近似当真实）：
    ///   - 字号轴是**真实**的：UIFontSize 真的换了字体，TextRenderer 用真实 GDI 度量；
    ///   - DPI 轴是**受控近似**：CI 没有高 DPI 显示器，只能通过 DpiScale.SetOverrideForTests
    ///     注入系数，覆盖的是我们自己的 DpiScale 数学；系统级 PerMonitorV2 缩放仍需真机。
    ///
    /// 目标清单**不在本文件**：来自 <see cref="UiPageCatalog"/>（侧栏 + 对话框只有一份构造代码），
    /// 目标清单另写一份 ui-matrix/targets.json 供 tools/ui-matrix-check.py 做覆盖度棘轮。
    /// </summary>
    public static class UiMatrixSmoke
    {
        private static readonly float[] DpiFactors = { 1.0f, 1.5f };
        private static readonly float[] FontSizes = { 9f, 14f };
        /// <summary>几何轴固定用 Dark（对比度最高、最容易看出裁切）；另两个主题各跑一格基线。</summary>
        private const string GeometryTheme = "Dark";
        private static readonly string[] ExtraThemes = { "OLED", "Darker" };

        private static int _fails;

        public static int Run(string[] args)
        {
            string outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "ui-matrix";
            Directory.CreateDirectory(outDir);
            Console.WriteLine("=== gdterm L2 matrix (geometry + theme axis) -> " + outDir + " ===");

            var pages = new List<UiPage>();
            pages.AddRange(UiPageCatalog.Panels());
            pages.AddRange(UiPageCatalog.Dialogs());

            var prevAppearance = Gdterm.UI.Program.GlobalAppearance;
            var combos = new List<string[]>();
            foreach (var dpi in DpiFactors)
                foreach (var font in FontSizes)
                    combos.Add(new[] { GeometryTheme, Inv(dpi), Inv(font) });
            foreach (var theme in ExtraThemes)
                combos.Add(new[] { theme, Inv(1.0f), Inv(9f) });

            int dumps = 0;
            try
            {
                WriteTargets(outDir, pages);

                foreach (var combo in combos)
                {
                    string theme = combo[0];
                    float dpi = float.Parse(combo[1], System.Globalization.CultureInfo.InvariantCulture);
                    float font = float.Parse(combo[2], System.Globalization.CultureInfo.InvariantCulture);
                    string label = ComboLabel(theme, dpi, font);
                    string dir = Path.Combine(outDir, label);
                    Directory.CreateDirectory(dir);
                    Apply(theme, dpi, font);
                    Console.WriteLine("[combo] " + label);
                    foreach (var page in pages)
                    {
                        try
                        {
                            DumpPage(page, dir, font, dpi);
                            dumps++;
                        }
                        catch (Exception ex)
                        {
                            _fails++;
                            Console.WriteLine("  [FAIL] " + page.Name + ": " + UiSmokeRunner.FlatEx(ex));
                        }
                    }
                    // back 一栏是"主题真的切过去了"的自证：门拿它做主题轴反空转
                    // （AppearanceSettings 只换本地状态、GdtermColorTable 沒切的话，
                    //  每个格子的背景色会一模一样——三格跑同一张图，全绿也是假的）。
                    File.WriteAllText(Path.Combine(dir, "combo.json"),
                        "{\"theme\":\"" + theme + "\",\"dpiFactor\":" + combo[1] +
                        ",\"uiFontSize\":" + combo[2] +
                        ",\"back\":\"" + ColorHex(GdtermColorTable.Background) + "\"}",
                        System.Text.Encoding.UTF8);
                }
            }
            finally
            {
                Gdterm.UI.Services.DpiScale.SetOverrideForTests(0f);
                Gdterm.UI.Program.GlobalAppearance = prevAppearance;
            }

            Console.WriteLine();
            Console.WriteLine("matrix combos={0} targets={1} dumps={2} fails={3}",
                combos.Count, pages.Count, dumps, _fails);
            return _fails > 0 ? 1 : 0;
        }

        private static string ColorHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static string Inv(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string ComboLabel(string theme, float dpi, float font)
        {
            return string.Format("{0}-d{1}-f{2}", theme, (int)Math.Round(dpi * 100), (int)font);
        }

        /// <summary>
        /// 落一份目标清单给 python 门：名字 + 分组 + 形态。
        /// 形态是**机器可读**的（不是注释），门会断言"多行输入/搜索/表格/进度/树"这些形态
        /// 各自至少有一页——否则 38 页全是表格也会被算作"覆盖完整"。
        /// </summary>
        private static void WriteTargets(string outDir, List<UiPage> pages)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"targets\":[");
            for (int i = 0; i < pages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(pages[i].Name)
                  .Append("\",\"group\":\"").Append(pages[i].Group)
                  .Append("\",\"shape\":\"").Append(pages[i].Shape).Append("\"}");
            }
            sb.Append("],\"requiredShapes\":[\"表格\",\"多行输入\",\"搜索\",\"进度\",\"树\",\"空态\"]}");
            File.WriteAllText(Path.Combine(outDir, "targets.json"), sb.ToString(), System.Text.Encoding.UTF8);
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

        /// <summary>按目录配方构造一页并 dump：面板套 360×480 宿主，对话框直接 Show。</summary>
        private static void DumpPage(UiPage page, string dir, float font, float dpi)
        {
            using (var scope = new UiTempScope("matrix"))
            {
                Control root = null;
                try
                {
                    root = page.Create(scope);
                    if (root == null) throw new Exception("catalog returned null control");
                    var form = root as Form;
                    if (form != null)
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(0, 0);
                        ShowAndDump(form, dir, page.Name, font, dpi);
                        return;
                    }
                    using (var host = PanelHost())
                    {
                        root.Dock = DockStyle.Fill;
                        host.Controls.Add(root);
                        root.Visible = true;
                        ShowAndDump(host, dir, page.Name, font, dpi);
                    }
                }
                finally
                {
                    var d = root as IDisposable;
                    if (d != null) { try { d.Dispose(); } catch (Exception) { } }
                }
            }
        }

        /// <summary>侧栏面板的宿主窗：360×480 贴近真实侧栏几何，与 UiSmokeRunner 的口径一致。</summary>
        private static Form PanelHost()
        {
            return new Form
            {
                Text = "matrix-host",
                ClientSize = new Size(360, 480),
                BackColor = GdtermColorTable.Background,
                // 宿主窗必须显式吃 UI 字体，否则字号轴只对面板自己设的字体生效（6/8 页漏防）
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
            Thread.Sleep(280);
            Application.DoEvents();

            // 反空转：字体必须真的落到窗体上（FormFontPolicy 读 GlobalAppearance，若被缓存/绕过，
            // 字号轴会安静地退化成"同一张图跑 N 遍"，这行断言就是那道防线）。
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
