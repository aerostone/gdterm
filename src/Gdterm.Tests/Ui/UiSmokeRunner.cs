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
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    var table = FindByName(f, "KeePassEntryTable") as Control;
                    Check(table != null, "KeePassEntryTable 可定位");
                    if (table != null)
                    {
                        dynamic dt = table;
                        Check((int)dt.RowCount == 2, "表行=2（实际" + dt.RowCount + ")");
                    }
                    Shot(f, Path.Combine(outDir, "keepass-manager.png"));
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
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    Check(FindByName(f, "HealthCloseButton") != null, "关闭按钮可定位");
                    Check(f.CancelButton != null, "ESC(CancelButton)已绑");
                    Shot(f, Path.Combine(outDir, "password-health.png"));
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
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    Check(FindByName(f, "ScannerPluginTable") != null, "插件表可定位");
                    Check(FindByName(f, "ScannerFindingTable") != null, "结果表可定位");
                    Shot(f, Path.Combine(outDir, "scanner-center.png"));
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
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("  shot: " + path);
        }
    }
}
