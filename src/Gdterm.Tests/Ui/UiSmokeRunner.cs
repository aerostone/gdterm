using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// UI 冒烟：免 FlaUI 启动关键窗体，截图到 out 目录供 PIL 断言。
    /// 用法：UiSmoke.exe [outDir]
    /// 退出码 0=全过，1=有失败。每个用例独立 try/catch，一个炸不影响其余。
    /// 注意：必须在 Windows 有桌面会话的 CI agent 上跑（AppVeyor VS2022 默认有）。
    /// </summary>
    public static class UiSmokeRunner
    {
        /// <summary>
        private static int _passes;
        private static int _fails;
        private static readonly List<string> _messages = new List<string>();

        // ── 看门狗（2026-11-13：一次提交把 CI 卡了 25 分钟，日志要等整条流水线
        //    超时才拿得到，等于"hang 一次亏半小时"）。规则很简单：任何一步在
        //    IDLE_LIMIT 秒内没有推进进度，就把"卡在哪一步"写进 console/progress.log
        //    并且自己退出（退出码 3）——失败要快，失败要指得出位置。
        private const int IdleLimitSeconds = 120;
        private static readonly object _progLock = new object();
        private static string _progStep = "(not started)";
        private static DateTime _progAt = DateTime.UtcNow;
        private static string _progFile;

        /// <summary>声明"我正在做第 N 步"。所有可能长跑的用例都要调一次。</summary>
        public static void Progress(string step)
        {
            lock (_progLock)
            {
                _progStep = step;
                _progAt = DateTime.UtcNow;
                try
                {
                    if (_progFile != null)
                        File.AppendAllText(_progFile,
                            DateTime.Now.ToString("HH:mm:ss") + " " + step + "\r\n",
                            System.Text.Encoding.UTF8);
                }
                catch (Exception) { }
            }
        }

        /// <summary>启动看门狗并把轨迹写到 outDir/progress.log（矩阵冒烟也要，卡一次亏半小时）。</summary>
        public static void StartWatchdog(string outDir)
        {
            _progFile = Path.Combine(outDir, "progress.log");
            try { File.WriteAllText(_progFile, "", System.Text.Encoding.UTF8); } catch (Exception) { }
            StartWatchdog();
        }

        private static void StartWatchdog()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(10000);
                    string step;
                    double idle;
                    lock (_progLock)
                    {
                        step = _progStep;
                        idle = (DateTime.UtcNow - _progAt).TotalSeconds;
                    }
                    if (idle < IdleLimitSeconds) continue;
                    string msg = "[HANG] 卡在 " + step + "：已 " + (int)idle + " 秒无进度"
                                 + "（看门狗上限 " + IdleLimitSeconds + "s，progress.log 有完整轨迹）";
                    try { Console.WriteLine(msg); Console.Out.Flush(); } catch (Exception) { }
                    try
                    {
                        if (_progFile != null) File.AppendAllText(_progFile, msg + "\r\n", System.Text.Encoding.UTF8);
                    }
                    catch (Exception) { }
                    Environment.Exit(3);
                }
            });
            t.IsBackground = true;
            t.Name = "smoke-watchdog";
            t.Start();
        }

        public static int Run(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ui-smoke");
            Directory.CreateDirectory(outDir);
            _smokeOutDir = outDir;
            Environment.SetEnvironmentVariable("GDTERM_TRACE_FILE", Path.Combine(outDir, "ctor-trace.log"));
            if (Environment.GetEnvironmentVariable("GDTERM_SMOKE_PROBE") == "1") NativeProbe(outDir);
            StartWatchdog(outDir);
            Progress("start");
            Console.WriteLine("=== gdterm UI smoke (screenshots to " + outDir + ") ===");

            // 1) 密码管理器：行数 + 列宽 + 行高 + 关闭
            RunCase("KeePassManager", () =>
            {
                using (var f = new KeePassManagerForm(new FakeKeePassService()))
                {
                    f.Show();
                    f.BringToFront();
                    f.Activate();
                    Application.DoEvents();
                    Thread.Sleep(800);
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    AssertNoDockOverlap(f, "KeePassManager");
                    var table = FindByName(f, "KeePassEntryTable");
                    Check(table != null, "KeePassEntryTable 可定位");
                    if (table != null)
                    {
                        var pi = table.GetType().GetProperty("RowCount");
                        int rc = pi != null ? (int)pi.GetValue(table, null) : -1;
                        Check(rc == 2, "表行=2（实际" + rc + ")");
                        // 布局断言（代替抓屏行高）：表有非零客户区 + 行高地板 24
                        Check(table.ClientSize.Width > 200 && table.ClientSize.Height > 100,
                            "表客户区=" + table.ClientSize.Width + "x" + table.ClientSize.Height);
                        var rh = table.GetType().GetProperty("RowHeight");
                        int rhv = rh != null ? (int)(rh.GetValue(table, null) ?? -1) : -1;
                        Check(rhv >= 24 && rhv <= 34, "表 RowHeight=" + rhv + " 在 24-34");
                        // 列宽：5 列百分比之和 100%，实得像素宽之和 >0
                        var cols = table.GetType().GetProperty("Columns");
                        object colObj = cols != null ? cols.GetValue(table, null) : null;
                        int colCount = -1;
                        if (colObj is System.Collections.ICollection cc) colCount = cc.Count;
                        else if (colObj != null)
                        {
                            var cp = colObj.GetType().GetProperty("Count");
                            if (cp != null) colCount = (int)cp.GetValue(colObj, null);
                        }
                        Check(colCount == 5, "表列=5（实际" + colCount + ")");
                    }
                    Shot(f, Path.Combine(outDir, "keepass-manager.png"));
                    File.WriteAllText(Path.Combine(outDir, "keepass-manager.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    Console.WriteLine("  dump: keepass-manager.json");
                    var closeBtn = FindByName(f, "KeePassCloseButton") as Control;
                    Check(closeBtn != null, "关闭按钮可定位");
                    Check(f.CancelButton != null, "ESC(CancelButton)已绑");
                }
            });

            // 2) 密码健康报告：关闭条 + Tab 页
            RunCase("PasswordHealth", () =>
            {
                using (var f = new PasswordHealthForm(new FakeKeePassService()))
                {
                    f.Show();
                    f.BringToFront();
                    f.Activate();
                    Application.DoEvents();
                    Thread.Sleep(800);
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    Check(FindByName(f, "HealthCloseButton") != null, "关闭按钮可定位");
                    Check(f.CancelButton != null, "ESC(CancelButton)已绑");
                    // UX change 2026-09-25：表头“上次扫描”状态行必须已填入非占位值
                    AssertNoDockOverlap(f, "PasswordHealth");
                    var state = FindByName(f, "HealthScanStateLabel");
                    Check(state != null, "HealthScanStateLabel 可定位");
                    if (state != null)
                        Check(state.Text != null && state.Text.StartsWith("上次扫描：") && state.Text != "上次扫描：—",
                            "状态行已填充=" + state.Text);
                    Shot(f, Path.Combine(outDir, "password-health.png"));
                    File.WriteAllText(Path.Combine(outDir, "password-health.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    Console.WriteLine("  dump: password-health.json");
                }
            });

            // 3) 扫描中心：两张表可定位
            RunCase("ScannerCenter", () =>
            {
                using (var f = new ScannerCenterForm(
                    new Gdterm.Tools.Scanning.ScanPluginStore(),
                    () => null))
                {
                    f.Show();
                    f.BringToFront();
                    f.Activate();
                    Application.DoEvents();
                    Thread.Sleep(800);
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    Check(FindByName(f, "ScannerPluginTable") != null, "插件表可定位");
                    Check(FindByName(f, "ScannerFindingTable") != null, "结果表可定位");
                    AssertNoDockOverlap(f, "ScannerCenter");
                    // UX change 2026-09-25：0 插件 / 0 发现 / 空输出 三条空状态引导都在。
                    // 插件侧已从「一行 20px 灰字」升级为 EmptyStateView（C1 标准空态层），
                    // 旧标签删除——断言跟着搬到空态层，否则门会跟着死代码一起假绿。
                    foreach (var nm in new[] { "ScannerFindingEmptyHint", "ScannerRawEmptyHint" })
                    {
                        var h = FindByName(f, nm);
                        Check(h != null && h.Visible, nm + " 空状态可见");
                    }
                    var plugEmpty = FindByType(f, "EmptyStateView");
                    Check(plugEmpty != null && plugEmpty.Visible, "ScannerPluginEmpty 空状态可见");
                    // 右列左缘与左列同 pad（原 0：空态下左右不对称）。
                    // 发现表现在包在 TableShell 里（Parent 是壳而非列 Panel），不能再直接比
                    // Parent 相对的 Left——改为沿父链走到列 Panel 累加偏移（gutter），两列内缩相等才算对齐。
                    var pt = FindByName(f, "ScannerPluginTable");
                    var ft = FindByName(f, "ScannerFindingTable");
                    Check(pt != null && ft != null && Math.Abs(LeftGutter(pt) - LeftGutter(ft)) <= 2,
                        "右列左缘对齐 gutter=" + (pt != null ? LeftGutter(pt) : -1) + "/" + (ft != null ? LeftGutter(ft) : -1) + " (≤2px 容差:壳边框)");
                    Check(f.KeyPreview, "KeyPreview 已开（ESC 可达）");
                    Shot(f, Path.Combine(outDir, "scanner-center.png"));
                    File.WriteAllText(Path.Combine(outDir, "scanner-center.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    Console.WriteLine("  dump: scanner-center.json");
                }
            });

            // 3b) 空库引导（UX change 2026-09-25）：空库显示引导层，有行则隐藏
            RunCase("KeePassManagerEmpty", () =>
            {
                using (var f = new KeePassManagerForm(new FakeKeePassService(empty: true)))
                {
                    f.Show();
                    f.BringToFront();
                    f.Activate();
                    Application.DoEvents();
                    Thread.Sleep(800);
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    AssertNoDockOverlap(f, "KeePassManagerEmpty");
                    var guide = FindByName(f, "KeePassEmptyGuide");
                    Check(guide != null, "KeePassEmptyGuide 可定位");
                    Check(guide != null && guide.Visible, "空库时引导可见");
                    Check(FindByName(f, "KeePassEmptyGuideAddButton") != null, "引导主按钮可定位");
                    Check(FindByName(f, "KeePassEmptyGuideHint") != null, "引导说明可定位");
                    Shot(f, Path.Combine(outDir, "keepass-manager-empty.png"));
                    File.WriteAllText(Path.Combine(outDir, "keepass-manager-empty.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    Console.WriteLine("  dump: keepass-manager-empty.json");
                }
                using (var f = new KeePassManagerForm(new FakeKeePassService()))
                {
                    f.Show();
                    Application.DoEvents();
                    Thread.Sleep(300);
                    Application.DoEvents();
                    var guide = FindByName(f, "KeePassEmptyGuide");
                    Check(guide != null, "非空库引导层仍在树上");
                    Check(guide != null && !guide.Visible, "有行时引导隐藏");
                }
            });

            // 3c) 侧栏面板群（审计 C7-2）：面板清单与构造只在 UiPageCatalog 里写一次，
            // 矩阵/失败态冒烟共用同一批配方（此前三处各写一份，加页面就漏改）。
            RunCase("SidePanels", () =>
            {
                var failures = new List<string>();
                int total = 0;
                foreach (var page in UiPageCatalog.Panels())
                {
                    total++;
                    try { DumpSidePanel(page); }
                    catch (Exception ex) { failures.Add(page.Name + ": " + FlatEx(ex)); }
                }
                Check(failures.Count == 0, "侧栏面板失败 " + failures.Count + "/" + total + ": " +
                    string.Join(" | ", failures));
                Console.WriteLine("  side-panels done");
            });

            // 4) 主窗 layout：全依赖临时目录构造，dump 整棵树。
            // 主窗构造链长（hotkey/托盘/欢迎页/会话恢复），独立 STA 线程 + 8 分钟硬超时，
            // 超时只记 FAIL 不卡 job，后续 job（artifact/树检）照常跑。
            RunCase("MainForm", () =>
            {
                Exception workerEx = null;
                var t = new System.Threading.Thread(() =>
                {
                    try { MainFormSmoke.Run(outDir, s => Console.WriteLine("  " + s), (ok, what) => Check(ok, what)); }
                    catch (Exception ex) { workerEx = ex; }
                });
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
                if (!t.Join(TimeSpan.FromMinutes(8)))
                {
                    // 不 Abort（STA 窗体半残句柄风险）：后台线程随进程退出回收，只记 FAIL 让 job 继续。
                    throw new TimeoutException("mainform-smoke timeout 8min (construct/restore suspected), job continues");
                }
                if (workerEx != null) throw workerEx;
            });

            // 5) 独立对话框群：12 小窗 Show + dump（主线程顺序跑，单窗异常只记该窗 FAIL）
            RunCase("Dialogs", () =>
            {
                int p0 = _passes, f0 = _fails;
                DialogsSmoke.Run(outDir, s => Console.WriteLine("  " + s), (ok, what) => Check(ok, what));
                Console.WriteLine("  dialogs done");
            });

            // 6) 状态轴（docs/UI-UX-VERIFICATION.md §4.1 遗留缺口①）：禁用/错误/忙碌/窄窗。
            // 此前全仓 dump 只有"满/默认态"，用户真报的 bug 全发生在其余状态。
            RunCase("StateAxis", () =>
            {
                UiStateSmoke.Run(outDir, s => Console.WriteLine("  " + s), (ok, what) => Check(ok, what));
            });

            // 7) 失败态轴（§4.1 遗留缺口③）：网络/密码/读盘/扫描真的失败时界面说了什么。
            // 这类状态以前只有人工 L4，catch 里只写日志或弹个 2 秒就消失的 toast。
            RunCase("FailureStates", () =>
            {
                UiFailureSmoke.Run(outDir, s => Console.WriteLine("  " + s), (ok, what) => Check(ok, what));
            });

            Console.WriteLine();
            Console.WriteLine("UI smoke Passed: {0}  Failed: {1}", _passes, _fails);
            foreach (var m in _messages) Console.WriteLine(m);
            return _fails > 0 ? 1 : 0;
        }

        private static void RunCase(string name, Action body)
        {
            Progress("case:" + name);
            try
            {
                body();
                _passes++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _fails++;
                string msg = "[FAIL] " + name + ": " + FlatEx(ex);
                _messages.Add(msg);
                Console.WriteLine(msg);
            }
        }

        // 异常链拍平为 ASCII（CI 日志 GBK 下中文变 ??，用英文 + HResult 定位）
        internal static string FlatEx(Exception ex)
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
                int depth = 0;
                while (ex != null && depth < 4)
                {
                    string m = ex.Message ?? "";
                    var sb = new System.Text.StringBuilder();
                    foreach (char ch in m) sb.Append(ch < 128 ? ch : '?');
                    parts.Add(ex.GetType().Name + "(HR=0x" + ex.HResult.ToString("X8") + "):" + sb.ToString());
                    ex = ex.InnerException;
                    depth++;
                }
                return string.Join(" <- ", parts.ToArray());
            }
            catch { return "FlatEx-failed"; }
        }

        private static void Check(bool ok, string what)
        {
            if (!ok) throw new Exception("断言失败：" + what);
            Console.WriteLine("  ok: " + what);
        }

        /// <summary>
        /// 停靠遮挡断言（UX change 2026-09-25 D5）：同父可见兄弟中，Dock=Fill 与非 Fill 边停靠
        /// （Top/Bottom/Left/Right）两者不得相交 —— 相交即“工具栏/表头盖住表体”这类布局 bug 的可测签名。
        /// 该类 bug 已被 CI 321 像素实测证实：scanner-center 工具栏盖住两表表头 56px，Python 侧因
        /// “停靠对不算重叠”而漏判，故在 C# 侧落门（CI 真跑得到；tools/ui-tree-check.py 不参与流水线）。
        /// 免判：Fill 与 Fill 兄弟（同格覆盖层，如 KeePass 空库引导）不算遮挡。
        /// </summary>
        /// <summary>表左缘相对其列 Panel 左缘的内缩偏移：沿父链（穿过 TableShell 壳）走到
        /// Panel/UserControl 止，逐层累加 Left。两列各自的内缩相等即“左缘对齐”。</summary>
        private static int LeftGutter(Control t)
        {
            int x = t.Left;
            for (var p = t.Parent; p != null && (p is Panel || p is UserControl); p = p.Parent)
                x += p.Left;
            return x;
        }

        /// <summary>
        /// 临时探针（GDTERM_SMOKE_PROBE=1）：把"原生 TreeView 挂到父容器"拆成最小步骤逐步落盘。
        /// 用来回答"是 WinForms 原生控件在建句柄时挂，还是我们自己的代码挂"。
        /// </summary>
        private static void NativeProbe(string outDir)
        {
            string f = Path.Combine(outDir, "ctor-trace.log");
            Action<string> P = st =>
            {
                try { File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + " probe:" + st + "\n"); } catch { }
            };
            P("start");
            var host = new UserControl();
            P("host");
            var tv = new TreeView();
            P("tv-new");
            host.Controls.Add(tv);
            P("tv-added-noimagelist");
            var il = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            il.Images.Add("folder", new Bitmap(16, 16));
            P("il-built");
            tv.ImageList = il;
            P("il-assigned");
            tv.Nodes.Add(new TreeNode("x"));
            P("nodes-added");
            tv.ExpandAll();
            P("expanded");
            tv.Dispose(); host.Dispose();
            P("done");

            // 第二臂：先让父容器建句柄（CreateGraphics —— 产品构造期 DpiScale/RowStep 就这么干），
            // 再挂 TreeView。对照组一臂是"父容器未建句柄"。
            var host2 = new UserControl();
            P("2:host");
            using (var g = host2.CreateGraphics()) P("2:host-handle=" + host2.IsHandleCreated);
            var il2 = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            il2.Images.Add("folder", new Bitmap(16, 16));
            var tv2 = new TreeView
            {
                Dock = DockStyle.Fill,
                ImageList = il2,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HotTracking = true,
                FullRowSelect = true
            };
            P("2:tv-ready");
            host2.Controls.Add(tv2);
            P("2:tv-added");
            tv2.Nodes.Add(new TreeNode("x"));
            P("2:nodes");
            tv2.ExpandAll();
            P("2:expanded");
            tv2.Dispose(); host2.Dispose();
            P("2:done");
        }

        internal static void AssertNoDockOverlap(Control root, string where)
        {
            int bad = 0;
            var stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var parent = stack.Pop();
                var kids = new List<Control>();
                foreach (Control c in parent.Controls)
                {
                    kids.Add(c);
                    if (c.Controls.Count > 0) stack.Push(c);
                }
                for (int i = 0; i < kids.Count; i++)
                {
                    for (int j = i + 1; j < kids.Count; j++)
                    {
                        var a = kids[i];
                        var b = kids[j];
                        if (!a.Visible || !b.Visible) continue;
                        bool aFill = a.Dock == DockStyle.Fill, bFill = b.Dock == DockStyle.Fill;
                        bool aEdge = IsEdgeDock(a.Dock), bEdge = IsEdgeDock(b.Dock);
                        if (!((aFill && bEdge) || (bFill && aEdge))) continue;
                        var ov = Rectangle.Intersect(a.Bounds, b.Bounds);
                        if (ov.Width <= 0 || ov.Height <= 0) continue;
                        bad++;
                        if (bad <= 3)
                            Check(false, where + " DOCK_OVERLAP " + Pretty(a) + " vs " + Pretty(b)
                                + " overlap=" + ov.Width + "x" + ov.Height);
                    }
                }
            }
            if (bad == 0) Check(true, where + " DOCK_OK (0 overlaps)");
        }

        private static bool IsEdgeDock(DockStyle d)
        {
            return d == DockStyle.Top || d == DockStyle.Bottom || d == DockStyle.Left || d == DockStyle.Right;
        }

        private static string Pretty(Control c)
        {
            string label = string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name;
            return label + "(" + c.Dock + ")";
        }

        internal static Control FindByName(Control root, string name)
        {
            if (root.Name == name) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByName(c, name);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>
        /// 侧栏面板 dump 公用壳（审计 C7-2）：宿主窗 360x480（贴近真实侧栏几何），
        /// 面板 Dock=Fill。断言：能构造、能 Show、控件数 >3、dump 落盘。
        /// 空态引导层（EmptyStateView）若在树上，额外断言其存在 —— 把 C1 的新组件也锁住。
        /// </summary>
        private static void DumpSidePanel(UiPage page)
        {
            // outDir 由 Run() 开头设一次（避免改 RunCase 签名）
            string outDir = _smokeOutDir;
            string name = page.Name;
            var emptyMode = page.Empty;
            // 单页复现钩子：GDTERM_SMOKE_PAGE=<页面名> 只跑这一页（排查"某一页把 CI 卡死"时
            // 不必整轮冒烟重跑，也不用把看门狗当唯一线索）。
            string only = Environment.GetEnvironmentVariable("GDTERM_SMOKE_PAGE");
            if (!string.IsNullOrEmpty(only) && !string.Equals(only, name, StringComparison.OrdinalIgnoreCase))
                return;
            Progress("panel:" + name + ":create");
            // 构造期细粒度轨迹（产品侧 GDTERM_TRACE_FILE 钩子）落同一目录，随证据一起上传
            Environment.SetEnvironmentVariable("GDTERM_TRACE_FILE", Path.Combine(outDir, "ctor-trace.log"));

            Control panel = null;
            // tmp 作用域：面板的 store/manager/scanner 依赖随作用域释放，目录自动清理
            using (var scope = new UiTempScope("panels"))
            using (var host = new Form())
            {
                host.Text = name + "-host";
                host.ClientSize = new Size(360, 480);
                host.BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(0, 0);

                try { panel = page.Create(scope); }
                catch (Exception ex)
                {
                    Check(false, name + " CTOR_FAIL: " + FlatEx(ex));
                    return;
                }
                Progress("panel:" + name + ":created");
                Check(panel != null, name + " PANEL_NULL");
                if (panel == null) return;

                panel.Dock = DockStyle.Fill;
                host.Controls.Add(panel);
                // 覆盖式面板（SnippetSearch 等浮层）出厂 Visible=false，靠 ShowAndFocus 才现身；
                // 不显式点亮的话它整棵子树都是"不可见"，空态断言会假红/假绿。
                panel.Visible = true;
                Progress("panel:" + name + ":show");
                host.Show();
                host.BringToFront();
                Application.DoEvents();
                Thread.Sleep(500);
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();

                Progress("panel:" + name + ":pumped");
                AssertNoDockOverlap(host, name);
                Check(CountControls(panel) > 3, name + " TOO_FEW_CONTROLS=" + CountControls(panel) + " (>3)");

                // 空态引导层：空数据源面板必须在树上挂 EmptyStateView（C1）。
                // expectEmpty=true 时先断言存在再断言可见（场景要求节点存在：
                // “找到才检查”会在组件被删除后假绿）；false 时找到才查可见性（其余面板空态未接，属已知遗留）。
                var empty = FindByType(panel, "EmptyStateView");
                if (emptyMode == PanelEmptyExpect.Visible)
                {
                    Check(empty != null, name + " EMPTY_MISSING (no EmptyStateView)");
                    if (empty != null)
                        Check(empty.Visible, name + " EMPTY_HIDDEN (empty source but layer invisible)");
                }
                else if (emptyMode == PanelEmptyExpect.Hidden)
                {
                    Check(empty != null, name + " EMPTY_MISSING (no EmptyStateView)");
                    if (empty != null)
                        Check(!empty.Visible, name + " EMPTY_SHOWN (data present but layer still visible)");
                }

                Progress("panel:" + name + ":shot");
                Shot(host, Path.Combine(outDir, name + ".png"));
                File.WriteAllText(Path.Combine(outDir, name + ".json"), UiTreeDumper.Dump(host), System.Text.Encoding.UTF8);
                Console.WriteLine("  dump: " + name + ".json");
            }
            // 面板是宿主窗的子控件，宿主 Dispose 时会连带释放；这里补一手以防异常路径漏掉
            try { if (panel != null) panel.Dispose(); } catch { }
        }

        /// <summary>侧栏 dump 用：outDir 由 Run() 开头设一次。</summary>
        private static string _smokeOutDir;

        internal static int CountControls(Control root)
        {
            int n = 1;
            foreach (Control c in root.Controls) n += CountControls(c);
            return n;
        }

        /// <summary>先序遍历整棵控件树（状态轴断言 / 语义色扫描用）。</summary>
        internal static IEnumerable<Control> AllControls(Control root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (Control c in root.Controls)
                foreach (var d in AllControls(c))
                    yield return d;
        }

        internal static Control FindByType(Control root, string typeName)
        {
            if (root.GetType().Name == typeName) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByType(c, typeName);
                if (hit != null) return hit;
            }
            return null;
        }

        internal static void Shot(Form f, string path)
        {
            // AntdUI 自绘控件 DrawToBitmap 抓不到内容，必须用 Win32 PrintWindow 抓真实渲染。
            // PW_CLIENTONLY(0x1): 只抓客户区；失败时回退 DrawToBitmap（至少有窗口框架）。
            try
            {
                var rect = f.RectangleToScreen(f.ClientRectangle);
                using (var bmp = new Bitmap(rect.Width, rect.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        try { PrintWindow(f.Handle, hdc, 0x1); }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    bmp.Save(path, ImageFormat.Png);
                }
            }
            catch
            {
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(path, ImageFormat.Png);
                }
            }
            Console.WriteLine("  shot: " + path);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    }
}
