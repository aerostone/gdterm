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
        /// 空态层期望。三态是必须的：CI 0.1.382 实证"层在树上就必须可见"是错规则——
        /// 高亮规则自带 7 条默认数据、通知中心是进程级静态缓冲（被前面用例写过），
        /// 两者的层本就该隐藏；二态 bool 无法表达"有数据/无数据/没有层"。
        /// </summary>
        private enum EmptyExpect
        {
            /// <summary>不判空态层（面板没有空态层，或数据源不可控）。</summary>
            Ignore,
            /// <summary>空数据源：层必须在树上且可见。</summary>
            Visible,
            /// <summary>有数据源：层必须在树上且隐藏。</summary>
            Hidden
        }

        /// <summary>侧栏用例委托（具名：允许调用方用 empty: 命名参数）。</summary>
        private delegate void PanelCase(string name, Func<Control> create, EmptyExpect empty);

        private static int _passes;
        private static int _fails;
        private static readonly List<string> _messages = new List<string>();

        public static int Run(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ui-smoke");
            Directory.CreateDirectory(outDir);
            _smokeOutDir = outDir;
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
                    // UX change 2026-09-25：0 插件 / 0 发现 / 空输出 三条空状态引导都在
                    foreach (var nm in new[] { "ScannerPluginEmptyHint", "ScannerFindingEmptyHint", "ScannerRawEmptyHint" })
                    {
                        var h = FindByName(f, nm);
                        Check(h != null && h.Visible, nm + " 空状态可见");
                    }
                    // 右列左缘与左列同 pad（原 0：空态下左右不对称）
                    var pt = FindByName(f, "ScannerPluginTable");
                    var ft = FindByName(f, "ScannerFindingTable");
                    Check(pt != null && ft != null && pt.Left == ft.Left,
                        "右列左缘对齐 Left=" + (pt != null ? pt.Left : -1) + "/" + (ft != null ? ft.Left : -1));
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

            // 3c) 侧栏面板群（审计 C7-2）：6 个高频侧栏此前从未进 dump，布局回归无人守。
            // 每个面板塞进 360x480 宿主窗（贴近真实侧栏几何）单独 Show + dump。
            // 单面板失败只记该面板 FAIL（不拖垮其余）；全过后统一汇总断言。
            // 依赖全部走临时目录，不碰 data/。
            RunCase("SidePanels", () =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), "gdterm-panels-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                var toolRegistry = new Gdterm.Tools.ToolRegistry();
                var secretScanner = new SecretScanner(SecretScanConfig.GetDefault());
                var portManager = new Gdterm.Tunnel.PortForwardManager();
                var failures = new List<string>();

                // 命名参数必须绑到具名委托上——Action<...> 的委托参数是 arg0/arg1/arg2，
                // 对裸 Action 传 expectEmpty: 会 CS1746（2026-11-01 CI 0.1.347 实证）
                PanelCase tryPanel = (name, create, empty) =>
                {
                    try { DumpSidePanel(name, create, empty); }
                    catch (Exception ex) { failures.Add(name + ": " + FlatEx(ex)); }
                };

                tryPanel("command-history", () => new CommandHistoryPanel(
                    new Gdterm.Logging.CommandHistoryStore(Path.Combine(tmp, "cmdhist"))), empty: EmptyExpect.Visible);
                tryPanel("port-forward", () => new PortForwardPanel(portManager), empty: EmptyExpect.Visible);
                tryPanel("secret-scan", () => new SecretScanPanel(secretScanner, null), empty: EmptyExpect.Ignore);
                // 快捷键仓库开箱即带内置预设（tmux 等）→ 表非空，空态层应隐藏
                tryPanel("key-binding", () => new KeyBindingPanel(
                    new Gdterm.Connections.TerminalKeyBindingStore(Path.Combine(tmp, "keybind.json"))), empty: EmptyExpect.Hidden);
                tryPanel("toolbox", () => new ToolboxPanel(toolRegistry), empty: EmptyExpect.Ignore);
                tryPanel("logon-script", () => new LogonScriptPanel(
                    new Gdterm.Connections.LogonScriptStore(Path.Combine(tmp, "logon.json"))), empty: EmptyExpect.Visible);

                // ===== 2026-11 补：此前这 10 个侧栏面板没有任何 dump =====
                // registry 里它们全是 dump:none，意味着"渲染成空白页"也没人会发现
                // （树形断言只能覆盖被构造过的面板）。这里逐个用 tmp 空库构造 + dump；
                // 空数据源的必须自带空态引导（expectEmpty:true 会断言 EmptyStateView 可见）。
                tryPanel("macro", () => new MacroPanel(Path.Combine(tmp, "macros"), null), empty: EmptyExpect.Visible);
                tryPanel("bookmarks", () => new SessionBookmarksPanel(
                    new Gdterm.Connections.BookmarkStoreJson(Path.Combine(tmp, "bookmarks")),
                    new Gdterm.Connections.ConnectionStoreJson(Path.Combine(tmp, "conns.json"))), empty: EmptyExpect.Visible);
                tryPanel("command-template", () => new CommandTemplatePanel(
                    new Gdterm.Connections.CommandTemplateStore(Path.Combine(tmp, "tpl.json")), null), empty: EmptyExpect.Visible);
                // 高亮规则自带 7 条默认规则（HighlightRuleConfig.GetDefault）→ 非空库
                tryPanel("highlight-rule", () => new HighlightRulePanel(
                    new Gdterm.Connections.HighlightStore(Path.Combine(tmp, "highlight.json"))), empty: EmptyExpect.Hidden);
                tryPanel("transfer-center", () => new TransferCenterPanel(), empty: EmptyExpect.Visible);
                // 通知中心用进程级静态缓冲，前面用例可能已写入 → 空态不可控，只 dump 不判
                tryPanel("notification-center", () => new NotificationCenterPanel(), empty: EmptyExpect.Ignore);
                tryPanel("multi-channel", () => new MultiChannelPanel(new Gdterm.Terminal.MultiChannelManager()), empty: EmptyExpect.Visible);
                tryPanel("batch-command", () => new BatchCommandPanel(), empty: EmptyExpect.Ignore);
                tryPanel("health-monitor", () => new HealthMonitorPanel(), empty: EmptyExpect.Ignore);
                tryPanel("snippet-search", () => new SnippetSearchPanel(
                    new System.Collections.Generic.List<Gdterm.Core.Models.QuickCommand>()), empty: EmptyExpect.Ignore);

                try { Directory.Delete(tmp, true); } catch { }
                try { toolRegistry.Dispose(); } catch { }
                try { secretScanner.Dispose(); } catch { }
                try { portManager.Dispose(); } catch { }
                Check(failures.Count == 0, "侧栏面板失败 " + failures.Count + "/16: " +
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

            Console.WriteLine();
            Console.WriteLine("UI smoke Passed: {0}  Failed: {1}", _passes, _fails);
            foreach (var m in _messages) Console.WriteLine(m);
            return _fails > 0 ? 1 : 0;
        }

        private static void RunCase(string name, Action body)
        {
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

        private static Control FindByName(Control root, string name)
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
        private static void DumpSidePanel(string name, Func<Control> create, EmptyExpect emptyMode = EmptyExpect.Ignore)
        {
            // outDir 由 Run() 开头设一次（避免改 RunCase 签名）
            string outDir = _smokeOutDir;

            Control panel = null;
            using (var host = new Form())
            {
                host.Text = name + "-host";
                host.ClientSize = new Size(360, 480);
                host.BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(0, 0);

                try { panel = create(); }
                catch (Exception ex)
                {
                    Check(false, name + " CTOR_FAIL: " + FlatEx(ex));
                    return;
                }
                Check(panel != null, name + " PANEL_NULL");
                if (panel == null) return;

                panel.Dock = DockStyle.Fill;
                host.Controls.Add(panel);
                host.Show();
                host.BringToFront();
                Application.DoEvents();
                Thread.Sleep(500);
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();

                AssertNoDockOverlap(host, name);
                Check(CountControls(panel) > 3, name + " TOO_FEW_CONTROLS=" + CountControls(panel) + " (>3)");

                // 空态引导层：空数据源面板必须在树上挂 EmptyStateView（C1）。
                // expectEmpty=true 时先断言存在再断言可见（场景要求节点存在：
                // “找到才检查”会在组件被删除后假绿）；false 时找到才查可见性（其余面板空态未接，属已知遗留）。
                var empty = FindByType(panel, "EmptyStateView");
                if (emptyMode == EmptyExpect.Visible)
                {
                    Check(empty != null, name + " EMPTY_MISSING (no EmptyStateView)");
                    if (empty != null)
                        Check(empty.Visible, name + " EMPTY_HIDDEN (empty source but layer invisible)");
                }
                else if (emptyMode == EmptyExpect.Hidden)
                {
                    Check(empty != null, name + " EMPTY_MISSING (no EmptyStateView)");
                    if (empty != null)
                        Check(!empty.Visible, name + " EMPTY_SHOWN (data present but layer still visible)");
                }

                Shot(host, Path.Combine(outDir, name + ".png"));
                File.WriteAllText(Path.Combine(outDir, name + ".json"), UiTreeDumper.Dump(host), System.Text.Encoding.UTF8);
                Console.WriteLine("  dump: " + name + ".json");
            }
            // 面板是宿主窗的子控件，宿主 Dispose 时会连带释放；这里补一手以防异常路径漏掉
            try { if (panel != null) panel.Dispose(); } catch { }
        }

        /// <summary>侧栏 dump 用：outDir 由 Run() 开头设一次。</summary>
        private static string _smokeOutDir;

        private static int CountControls(Control root)
        {
            int n = 1;
            foreach (Control c in root.Controls) n += CountControls(c);
            return n;
        }

        private static Control FindByType(Control root, string typeName)
        {
            if (root.GetType().Name == typeName) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByType(c, typeName);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void Shot(Form f, string path)
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
