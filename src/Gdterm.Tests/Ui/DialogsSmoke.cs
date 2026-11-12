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

            // 主机密钥确认（2026-11 主机密钥校验的唯一安全决策出口）：
            // 首次信任 + 密钥变更两种危险态各 dump 一次——指纹看不见/按钮点不到的盒子问题
            // 在这里会直接变成"用户闭眼点信任"，比排版难看严重得多。
            Show("hostkey-unknown", () => (Form)new HostKeyDialog(new Gdterm.Core.Security.HostKeyPromptInfo
            {
                Host = "10.0.0.11",
                Port = 22,
                KeyType = "ssh-ed25519",
                KeyBits = 256,
                Sha256Fingerprint = "SHA256:smokeUnknownFingerprintValueForLayoutOnly",
                Md5Fingerprint = "aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99",
                Situation = Gdterm.Core.Security.HostKeySituation.Unknown
            }), outDir, log, check);
            Show("hostkey-changed", () => (Form)new HostKeyDialog(new Gdterm.Core.Security.HostKeyPromptInfo
            {
                Host = "10.0.0.11",
                Port = 2222,
                KeyType = "ssh-ed25519",
                KeyBits = 256,
                Sha256Fingerprint = "SHA256:smokeNewFingerprintValueForLayoutOnly",
                Md5Fingerprint = "11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00",
                Situation = Gdterm.Core.Security.HostKeySituation.Changed,
                PreviousSha256Fingerprint = "SHA256:smokeOldFingerprintValueForLayoutOnly"
            }), outDir, log, check);

            // 日志查看器（P1-3）：用 tmp 目录灌一份真实日志 + 一份二进制，
            // 锁住"文件列表有行、十六进制视图不是空白、查找/关闭按钮在"。
            string logDir = Path.Combine(tmp, "logs");
            Directory.CreateDirectory(Path.Combine(logDir, "terminal"));
            File.WriteAllText(Path.Combine(logDir, "terminal", "smoke_host_20260101_000000.log"),
                "# gdterm terminal log\r\n# Host: smoke\r\n[2026-01-01 00:00:00.000] $ ls -la\r\n[2026-01-01 00:00:00.100] total 0\r\n");
            File.WriteAllBytes(Path.Combine(logDir, "crash-smoke.bin"),
                new byte[] { 0x47, 0x44, 0x00, 0x0a, 0xff, 0x7f, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b });
            Show("log-viewer", () => (Form)new LogViewerForm(logDir), outDir, log, check);

            // 会话录制中心（P1-2）：灌一段真录制再开，锁"列表有行 + 回放引擎真的把字节画出来了"。
            // 只断言"窗开了"是骗自己——回放链（读文件→建渲染器→喂帧）断了照样是空白舞台。
            string recDir = Path.Combine(tmp, "recordings");
            Directory.CreateDirectory(recDir);
            string recPath = Path.Combine(recDir, "smoke_prod_20260101_000000.gtr");
            using (var w = new Gdterm.Core.Recording.SessionRecordingWriter(recPath,
                new Gdterm.Core.Recording.SessionRecordingHeader
                {
                    Columns = 80,
                    Rows = 24,
                    Host = "10.0.0.11",
                    ConnectionName = "smoke",
                    StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }))
            {
                w.Append(Gdterm.Core.Recording.SessionFrameKind.Output,
                    System.Text.Encoding.UTF8.GetBytes("$ echo smoke-replay\r\n"));
                w.Append(Gdterm.Core.Recording.SessionFrameKind.Input,
                    System.Text.Encoding.UTF8.GetBytes("echo smoke-replay\r"));
                w.Append(Gdterm.Core.Recording.SessionFrameKind.Output,
                    System.Text.Encoding.UTF8.GetBytes("smoke-replay\r\n$ "));
            }
            Show("session-recordings", () => (Form)new SessionRecordingsForm(recDir), outDir, log, check, f =>
            {
                var t = f.GetType();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var list = t.GetField("_list", flags).GetValue(f) as ListView;
                check(list != null && list.Items.Count == 1, "session-recordings-list-one=" +
                    (list == null ? "nulllist" : list.Items.Count.ToString()) + " (==1)");
                if (list != null && list.Items.Count > 0) list.SelectedIndices.Add(0);
                Application.DoEvents();
                Thread.Sleep(200);
                Application.DoEvents();
                t.GetMethod("StartPlayback", flags).Invoke(f, new object[] { true });
                Thread.Sleep(900);
                Application.DoEvents();
                var renderer = t.GetField("_renderer", flags).GetValue(f);
                check(renderer != null, "session-recordings-renderer");
                string screen = string.Empty;
                if (renderer != null)
                {
                    var lines = (string[])renderer.GetType().GetMethod("GetRecentLines").Invoke(renderer, new object[] { 24 });
                    screen = string.Join("\n", lines);
                }
                check(screen.Contains("smoke-replay"),
                    "session-recordings-replayed-text (回放的内容必须真出现在网格里)");
            });

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
                        try { UiSmokeRunner.Shot(f, Path.Combine(outDir, name + ".png")); }
                        catch (Exception exShot) { log("SHOT_FAIL " + name + " " + exShot.Message); }
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

        private static void Show(string name, Func<Form> make, string outDir, Action<string> log, Action<bool, string> check,
            Action<Form> after = null)
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
                    if (after != null) after(f);
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
                    // 截图与 dump 成对：R8（截图白块）只能判有图的页面。
                    // 之前 50 个 dump 里 23 个对话框没图，等于这批页面在像素层完全没证据。
                    try { UiSmokeRunner.Shot(f, Path.Combine(outDir, name + ".png")); }
                    catch (Exception exShot) { log("SHOT_FAIL " + name + " " + exShot.Message); }
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
