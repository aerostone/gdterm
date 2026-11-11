using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.Security;
using Gdterm.Security.Otp;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using Gdterm.UI.Services;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 独立对话框冒烟：全部独立窗体逐个 Show + dump 盒模型 + 基础断言（控件数/ClientSize 非零）。
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
            Show("otp-manager", () => (Form)new OtpManagerForm(new OtpStoreJson(Path.Combine(tmp, "otp-entries.json"))), outDir, log, check);
            Show("add-otp", () => (Form)new AddOtpForm(), outDir, log, check);
            Show("pwd-generator", () => (Form)new PasswordGeneratorForm(), outDir, log, check);
            Show("quickcmd-editor", () => (Form)new QuickCommandEditorForm(), outDir, log, check);
            Show("setup-wizard", () => (Form)new SetupWizardForm(security), outDir, log, check);
            Show("sshkey-manager", () => (Form)new SshKeyManagerForm(keepass), outDir, log, check);
            Show("transfer-progress", () => (Form)new TransferProgressDialog("smoke"), outDir, log, check);
            // 回收站：先灌 2 条锁三列/按钮启用态，再开空态外的有数据态
            TrashStore.Default.Add("smoke", "烟测", "烟测条目一", new object());
            TrashStore.Default.Add("smoke", "烟测", "烟测条目二", new object());
            Show("trash", () => (Form)new TrashForm(), outDir, log, check);
            // 连接导入预览：裁决入口不渲染过就没人知道「导入前能不能看清、确定键是否置灰」
            Show("import-preview", () => (Form)new ImportPreviewForm(
                new List<ConnectionConfig>
                {
                    new ConnectionConfig { Name = "prod-web-01", Protocol = ProtocolType.SSH, Host = "10.0.0.11", Port = 22 },
                    new ConnectionConfig { Name = "db-replica", Protocol = ProtocolType.SSH, Host = "10.0.0.12", Port = 22 }
                },
                new List<ConnectionConfig>
                {
                    new ConnectionConfig { Name = "已存在的主机", Protocol = ProtocolType.SSH, Host = "10.0.0.9", Port = 22 }
                }), outDir, log, check);

            // SFTP 权限编辑：此前是唯一没有 dump 的窗体（registry 里自己都写着"建议补 Show 用例"）。
            // 3×3 复选 + octal 双向联动靠反射量不到，至少把盒模型和"确定/取消绑定"锁住。
            Show("sftp-permission", () => (Form)new SftpPermissionForm("report.csv", "rw-r--r--", 644), outDir, log, check);

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
                        // 停靠遮挡门(change 2026-09-25-dangerous-cmd-dock-overlap D3):CI 全量 dump 实测除
                        // scanner-center 与 dangerous-cmd 外零命中,故对全部对话框挂载是零噪声的;
                        // 只挂单个窗体则下个窗体复发仍要人眼发现。
                        // 放在 dump 写出之后：门一旦失败(dump 已落盘)仍有产物可诊断。
                        UiSmokeRunner.AssertNoDockOverlap(f, name);
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
                    // R3 关闭绑定（change 2026-09-24）：免判清单——transfer-progress（进度窗无
                    // 关闭语义）、pwd-generator（工具小窗点×关）、setup-wizard（向导[上一步/下一步]导航）、
                    // dangerous-cmd（Dock 布局配置页无关闭语义，CI 320 实测 CancelButton 未绑，设计即此）、
                    // trash（工具小窗点×关，按钮全是操作语义无确认/取消对）。
                    if (name != "transfer-progress" && name != "pwd-generator" && name != "setup-wizard"
                        && name != "dangerous-cmd" && name != "trash")
                        check(f.CancelButton != null, name + "-cancelbtn(ESC绑定)");
                    check(f.ClientSize.Width > 200 && f.ClientSize.Height > 150,
                        name + "-client=" + f.ClientSize.Width + "x" + f.ClientSize.Height);
                    // 停靠遮挡门(change 2026-09-25-dangerous-cmd-dock-overlap D3):CI 全量 dump 实测除
                    // scanner-center 与 dangerous-cmd 外零命中,故对全部对话框挂载是零噪声的;
                    // 只挂单个窗体则下个窗体复发仍要人眼发现。
                    File.WriteAllText(Path.Combine(outDir, name + ".json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                    log("dump: " + name + ".json");
                    UiSmokeRunner.AssertNoDockOverlap(f, name);
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
