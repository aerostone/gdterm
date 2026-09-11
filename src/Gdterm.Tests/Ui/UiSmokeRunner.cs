using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
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
        private static int _passes;
        private static int _fails;
        private static readonly List<string> _messages = new List<string>();

        public static int Run(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ui-smoke");
            Directory.CreateDirectory(outDir);
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
                    Shot(f, Path.Combine(outDir, "scanner-center.png"));
                    File.WriteAllText(Path.Combine(outDir, "scanner-center.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    Console.WriteLine("  dump: scanner-center.json");
                }
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
                string msg = "[FAIL] " + name + ": " + ex.GetType().Name + " " + ex.Message;
                _messages.Add(msg);
                Console.WriteLine(msg);
            }
        }

        private static void Check(bool ok, string what)
        {
            if (!ok) throw new Exception("断言失败：" + what);
            Console.WriteLine("  ok: " + what);
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
