using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Core.Models;
using Gdterm.Security;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 独立对话框冒烟：11 个窗体 Show + dump 盒模型 + 基础断言（控件数/ClientSize 非零）。
    /// 全依赖 tmp 隔离，不连主机不碰真实 data/。每个窗体独立 try/catch，一个炸不影响其余。
    /// </summary>
    public static class DialogsSmoke
    {
        private static int _oneFails;

        public static void Run(string outDir, Action<string> log, Action<bool, string> check)
        {
            _oneFails = 0;
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-dlg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var keepass = new FakeKeePassService();
            var security = new SecurityManager();
            var detector = new DangerousCommandDetector(Path.Combine(tmp, "dangerous.json"));

            Show("ai-settings", () => (Form)new AiSettingsForm(new AiModelStore(Path.Combine(tmp, "ai.json"))), outDir, log, check);
            Show("appearance-settings", () => (Form)new AppearanceSettingsForm(tmp), outDir, log, check);
            Show("change-masterpwd", () => (Form)new ChangeMasterPasswordForm(security), outDir, log, check);
            ShowConnectionVariants(keepass, outDir, log, check);
            Show("dangerous-cmd", () => (Form)new DangerousCommandConfigForm(detector), outDir, log, check);
            Show("keepass-picker", () => (Form)new KeePassEntryPicker(keepass), outDir, log, check);
            Show("keepass-unlock", () => (Form)new KeePassUnlockForm(keepass), outDir, log, check);
            Show("pwd-generator", () => (Form)new PasswordGeneratorForm(), outDir, log, check);
            Show("quickcmd-editor", () => (Form)new QuickCommandEditorForm(), outDir, log, check);
            Show("setup-wizard", () => (Form)new SetupWizardForm(security), outDir, log, check);
            Show("sshkey-manager", () => (Form)new SshKeyManagerForm(keepass), outDir, log, check);
            Show("transfer-progress", () => (Form)new TransferProgressDialog("smoke"), outDir, log, check);

            check(_oneFails == 0, "dialogs-one-fail=" + _oneFails + " (==0)");
            try { Directory.Delete(tmp, true); } catch { }
            try { detector.Dispose(); } catch { }
            try { security.Dispose(); } catch { }
        }

        // 新建连接三协议各 dump 一次：反射展开高级区 + 切换协议（折叠态量不到 RDP/串口区真实尺寸）
        private static void ShowConnectionVariants(FakeKeePassService keepass, string outDir, Action<string> log, Action<bool, string> check)
        {
            string[] protos = new string[] { "SSH", "RDP", "Serial" };
            string[] names = new string[] { "connection-ssh", "connection-rdp", "connection-serial" };
            for (int i = 0; i < protos.Length; i++)
            {
                string name = names[i], proto = protos[i];
                try
                {
                    using (var f = new ConnectionDialog((ConnectionConfig)null, keepass))
                    {
                        f.Show();
                        Application.DoEvents();
                        Thread.Sleep(300);
                        Application.DoEvents();
                        var t = f.GetType();
                        var combo = t.GetField("_protocolCombo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(f);
                        combo.GetType().GetProperty("SelectedValue").SetValue(combo, proto, null);
                        Application.DoEvents();
                        Thread.Sleep(300);
                        Application.DoEvents();
                        t.GetMethod("ToggleAdvanced", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(f, null);
                        Application.DoEvents();
                        Thread.Sleep(500);
                        Application.DoEvents();
                        int n = Count(f);
                        check(n > 5, name + "-controls=" + n + " (>5)");
                        check(f.ClientSize.Width > 200 && f.ClientSize.Height > 150,
                            name + "-client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                        File.WriteAllText(Path.Combine(outDir, name + ".json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                        log("dump: " + name + ".json");
                    }
                    log("[PASS] " + name);
                }
                catch (Exception ex)
                {
                    _oneFails++;
                    log("[FAIL-ONE] dialog-" + name + "-failed: " + UiSmokeRunner.FlatEx(ex));
                }
            }
        }

        private static void Show(string name, Func<Form> make, string outDir, Action<string> log, Action<bool, string> check)
        {
            try
            {
                using (var f = make())
                {
                    f.Show();
                    f.BringToFront();
                    f.Activate();
                    Application.DoEvents();
                    Thread.Sleep(500);
                    Application.DoEvents();
                    int n = Count(f);
                    check(n > 5, name + "-controls=" + n + " (>5)");
                    check(f.ClientSize.Width > 200 && f.ClientSize.Height > 150,
                        name + "-client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                    File.WriteAllText(Path.Combine(outDir, name + ".json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    log("dump: " + name + ".json");
                }
                log("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _oneFails++;
                log("[FAIL-ONE] dialog-" + name + "-failed: " + UiSmokeRunner.FlatEx(ex));
            }
        }

        private static int Count(Control root)
        {
            int n = 1;
            foreach (Control c in root.Controls) n += Count(c);
            return n;
        }
    }
}
