using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 失败态冒烟（docs/UI-UX-VERIFICATION.md §4.1 缺口③：失败态此前只有人工 L4）。
    ///
    /// 状态轴（<see cref="UiStateSmoke"/>）覆盖的是"空/禁用/忙碌/窄窗"；**失败**没进去过：
    /// 网络断、密码错、文件读坏、扫描炸。这类状态有共同特征——代码里都有 catch，
    /// 但 catch 之后往往只写日志/弹一个 2 秒就消失的 toast，用户回来看到的是"和没数据一样"的界面。
    /// 于是本文件把每个失败态都钉成三件事：
    ///   1) 界面上**留下**可见文案（不是一闪而过的 toast）；
    ///   2) 文案有**具体原因**（不是"操作失败"）；
    ///   3) 文案用**语义色**渲染（数像素，不读属性——AntdUI 的 ForeColor 取值器不返回绘制色）。
    /// 并且断言失败态**可恢复**：错误条件消失后，界面必须回到可用态（不能卡死）。
    ///
    /// 与 UiStateSmoke 的分工：那边只认"前置条件不满足"，这边认"动作真的失败了"。
    /// </summary>
    public static class UiFailureSmoke
    {
        public static void Run(string outDir, Action<string> log, Action<bool, string> check)
        {
            var errs = new List<string>();
            Section("conn-validation", log, errs, () => ConnectionValidation(outDir, log, check));
            Section("unlock-wrong-password", log, errs, () => UnlockWrongPassword(outDir, log, check));
            Section("history-load-error", log, errs, () => HistoryLoadError(outDir, log, check));
            Section("scan-error", log, errs, () => ScanError(outDir, log, check));
            if (errs.Count > 0)
                throw new Exception("UiFailureSmoke 分节失败 " + errs.Count + " 节: " + string.Join(" | ", errs));
        }

        private static void Section(string name, Action<string> log, List<string> errs, Action body)
        {
            UiSmokeRunner.Progress("failure:" + name);
            log("section: " + name);
            try
            {
                body();
                log("section done: " + name);
            }
            catch (Exception ex)
            {
                string m = name + " -> " + UiSmokeRunner.FlatEx(ex);
                log("section FAILED: " + m);
                errs.Add(m);
            }
        }

        private static void Dump(Form f, string outDir, string name, Action<string> log)
        {
            UiSmokeRunner.Shot(f, Path.Combine(outDir, name + ".png"));
            File.WriteAllText(Path.Combine(outDir, name + ".json"),
                UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
            log("dump: " + name + ".json");
        }

        // ── ① 新建连接：主机留空点「创建」必须拦下（此前直接保存成功）──────────────
        private static void ConnectionValidation(string outDir, Action<string> log, Action<bool, string> check)
        {
            string hostRequired = L10n.T("主机不能为空");
            using (var f = new ConnectionDialog((ConnectionConfig)null, new FakeKeePassService()))
            {
                f.Show();
                f.BringToFront();
                Application.DoEvents();
                Thread.Sleep(400);
                Application.DoEvents();

                var hostBox = Field(f, "_hostBox") as Control;
                var okBtn = f.AcceptButton as AntdUI.Button;
                check(hostBox != null, "fail-conn: 主机输入框可定位");
                check(okBtn != null, "fail-conn: 主按钮可定位（Enter 可提交）");

                // 空主机 → 点主按钮：必须**不关窗**、必须给原因
                if (okBtn != null) okBtn.PerformClick();
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();

                Dump(f, outDir, "fail-conn-emptyhost", log);

                check(!f.IsDisposed, "fail-conn: 空主机点「创建」后窗口不能关掉（此前会直接保存成功）");
                check(f.DialogResult != DialogResult.OK,
                    "fail-conn: 空主机不应产生 OK 结果（实际=" + f.DialogResult + "）");

                Control errLabel = Descendant(f, "ConnDialogErrorLabel");
                check(errLabel != null, "fail-conn: 校验错误行在树上（Name=ConnDialogErrorLabel）");
                if (errLabel != null)
                {
                    check(errLabel.Visible, "fail-conn: 错误行可见");
                    string errText = LabelText(errLabel);
                    check(errText == hostRequired,
                        "fail-conn: 错误文案给出具体原因（期望「" + hostRequired + "」实际「" + errText + "」）");
                    int danger = UiStateSmoke.CountColorPixels(Path.Combine(outDir, "fail-conn-emptyhost.png"),
                        UiStateSmoke.ClientRect(f, errLabel), GdtermColorTable.Danger, 24);
                    check(danger >= 20,
                        "fail-conn: 错误文案渲染成 Danger 色（像素=" + danger + "/≥20）");
                }
                // 焦点回到被判错的字段：键盘用户不该自己去猜哪一格错了
                check(hostBox != null && (f.ActiveControl == hostBox || hostBox.Focused),
                    "fail-conn: 焦点落在主机框（实际=" + (f.ActiveControl == null ? "null" : f.ActiveControl.Name) + "）");

                // 改对 → 错误必须收回（失败态不能粘在界面上），再点才允许关窗
                if (hostBox != null)
                {
                    hostBox.Text = "10.0.0.11";
                    Application.DoEvents();
                    Thread.Sleep(200);
                    Application.DoEvents();
                }
                Dump(f, outDir, "fail-conn-recovered", log);
                if (errLabel != null)
                    check(!errLabel.Visible, "fail-conn: 补上主机后错误行收回（残留在屏上=失败态粘住）");
                if (okBtn != null) okBtn.PerformClick();
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();
                check(f.DialogResult == DialogResult.OK || f.IsDisposed,
                    "fail-conn: 校验通过后主按钮真的提交（DialogResult=" + f.DialogResult + "）");
            }
        }

        // ── ② 解锁：密码错必须常驻提示 + 清空密码 + 不解锁 ────────────────────────
        private static void UnlockWrongPassword(string outDir, Action<string> log, Action<bool, string> check)
        {
            var svc = new FakeKeePassService { UnlockSucceeds = false };
            string wrong = L10n.T("密码错误或密码库文件损坏");
            using (var f = new KeePassUnlockForm(svc))
            {
                f.Show();
                f.BringToFront();
                Application.DoEvents();
                Thread.Sleep(300);
                Application.DoEvents();

                var box = Field(f, "_passwordBox") as Control;
                var btn = f.AcceptButton as AntdUI.Button;
                check(box != null && btn != null, "fail-unlock: 密码框/主按钮可定位");
                if (box != null) box.Text = "definitely-wrong";
                Application.DoEvents();
                Thread.Sleep(120);
                Application.DoEvents();
                if (btn != null) btn.PerformClick();
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }

                Dump(f, outDir, "fail-unlock-wrongpwd", log);

                check(!f.IsUnlocked, "fail-unlock: 密码错时不能置 IsUnlocked");
                check(!f.IsDisposed, "fail-unlock: 密码错时窗口不能关（否则用户连重试入口都没有）");
                var status = Descendant(f, "UnlockStatusLabel") as Control;
                check(status != null, "fail-unlock: 状态行可定位（Name=UnlockStatusLabel）");
                if (status != null)
                {
                    check(status.Visible, "fail-unlock: 失败文案可见");
                    string txt = LabelText(status);
                    check((txt ?? "") == wrong,
                        "fail-unlock: 失败原因是常驻文案而不是 toast（期望「" + wrong + "」实际「" + txt + "」）");
                    int danger = UiStateSmoke.CountColorPixels(Path.Combine(outDir, "fail-unlock-wrongpwd.png"),
                        UiStateSmoke.ClientRect(f, status), GdtermColorTable.Danger, 24);
                    check(danger >= 20, "fail-unlock: 失败文案渲染成 Danger 色（像素=" + danger + "/≥20）");
                }
                if (box != null)
                    check(string.IsNullOrEmpty(box.Text), "fail-unlock: 失败后清空密码框（实际「" + box.Text + "」）");
                if (btn != null)
                    check(!btn.Enabled, "fail-unlock: 清空后主按钮回到置灰（不会拿旧密码重复提交）");

                // 恢复：重新输入 → 错误收回、按钮可用
                if (box != null) box.Text = "correct-horse";
                Application.DoEvents();
                Thread.Sleep(150);
                Application.DoEvents();
                Dump(f, outDir, "fail-unlock-recovered", log);
                if (status != null)
                {
                    string txt = LabelText(status);
                    check((txt ?? "") != wrong, "fail-unlock: 重新输入后旧错误收回（实际「" + txt + "」）");
                }
                if (btn != null) check(btn.Enabled, "fail-unlock: 重新输入后主按钮可用（失败态不是死的）");
            }
        }

        // ── ③ 命令历史：存储读坏时必须说"读失败"，而不是"还没有记录" ──────────────
        private static void HistoryLoadError(string outDir, Action<string> log, Action<bool, string> check)
        {
            string title = L10n.T("命令历史读取失败");
            string retry = L10n.T("重试");
            string dir = Path.Combine(Path.GetTempPath(), "gdterm-failload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new ThrowingHistoryStore(dir);
            try
            {
                using (var host = new Form())
                {
                    host.Text = "fail-history-host";
                    host.ClientSize = new Size(360, 480);
                    host.BackColor = GdtermColorTable.Background;
                    host.StartPosition = FormStartPosition.Manual;
                    host.Location = new Point(0, 0);

                    var panel = new CommandHistoryPanel(store);
                    panel.Dock = DockStyle.Fill;
                    host.Controls.Add(panel);
                    panel.Visible = true;
                    host.Show();
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();

                    Dump(host, outDir, "fail-history-load", log);

                    var empty = UiSmokeRunner.FindByType(panel, "EmptyStateView") as Control;
                    check(empty != null, "fail-history: 空态层在树上（读取失败也走同一套引导层）");
                    check(empty != null && empty.Visible, "fail-history: 读取失败时引导层可见（此前静默当空列表）");
                    if (empty != null)
                    {
                        string acc = empty.AccessibleName ?? "";
                        check(acc == title, "fail-history: 标题说明是「读取失败」而不是「还没有记录」（实际「" + acc + "」）");
                        int danger = UiStateSmoke.CountColorPixels(Path.Combine(outDir, "fail-history-load.png"),
                            UiStateSmoke.ClientRect(host, empty), GdtermColorTable.Danger, 24);
                        check(danger >= 20, "fail-history: 失败标题渲染成 Danger 色（像素=" + danger + "/≥20）");
                        var action = UiStateSmoke.FindLabelByText(empty, retry);
                        check(action != null, "fail-history: 主按钮改文案为「" + retry + "」（实际=" +
                            UiStateSmoke.DescribeLabels(empty) + "）");
                    }
                    // 重试必须是真按钮动作：点一次会再查一遍，仍然失败则错误态保持（不崩、不白屏）
                    var btn = UiSmokeRunner.FindByType(empty, "Button") as AntdUI.Button;
                    int before = store.QueryCount;
                    if (btn != null) btn.PerformClick();
                    Application.DoEvents();
                    Thread.Sleep(250);
                    Application.DoEvents();
                    check(store.QueryCount > before,
                        "fail-history: 「重试」真的重新查询（调用次数 " + before + "→" + store.QueryCount + "）");
                    check(empty != null && empty.Visible, "fail-history: 仍失败时错误态保持可见");
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        // ── ④ 密钥扫描：扫描出错时状态行必须变红（此前用中性灰写「错误: …」）────────
        private static void ScanError(string outDir, Action<string> log, Action<bool, string> check)
        {
            string reason = "磁盘不可读（冒烟注入）";
            using (var scanner = new Gdterm.Security.SecretScanner(Gdterm.Security.SecretScanConfig.GetDefault()))
            using (var host = new Form())
            {
                host.Text = "fail-scan-host";
                host.ClientSize = new Size(360, 480);
                host.BackColor = GdtermColorTable.Background;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(0, 0);
                var panel = new SecretScanPanel(scanner, null);
                panel.Dock = DockStyle.Fill;
                host.Controls.Add(panel);
                panel.Visible = true;
                host.Show();
                Application.DoEvents();
                Thread.Sleep(350);
                Application.DoEvents();

                // 触发生产路径：扫描器的 ErrorOccurred → OnError（SecretScanPanel.cs 里已订阅）。
                // 事件本身由扫描器在 IO/权限异常时抛，构造一个"一定会炸的扫描"不稳定，
                // 这里直接调订阅端处理函数，验的是"失败在界面上长什么样"这一段契约。
                var onError = typeof(SecretScanPanel).GetMethod("OnError",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                check(onError != null, "fail-scan: 失败处理函数存在（ErrorOccurred 的订阅端）");
                if (onError != null) onError.Invoke(panel, new object[] { reason });
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();

                Dump(host, outDir, "fail-scan-error", log);

                var status = Field(panel, "_lblStatus") as Control;
                check(status != null, "fail-scan: 状态行可定位");
                if (status != null)
                {
                    string txt = LabelText(status);
                    check((txt ?? "").Contains(reason),
                        "fail-scan: 状态行写出具体原因（实际「" + txt + "」）");
                    int danger = UiStateSmoke.CountColorPixels(Path.Combine(outDir, "fail-scan-error.png"),
                        UiStateSmoke.ClientRect(host, status), GdtermColorTable.Danger, 24);
                    check(danger >= 15, "fail-scan: 错误状态行渲染成 Danger 色（像素=" + danger + "/≥15）");
                }
                var scanBtn = Field(panel, "_btnScan") as Control;
                check(scanBtn != null && scanBtn.Enabled, "fail-scan: 失败后「开始扫描」回到可用（可重试）");
            }
        }

        // ── 工具 ────────────────────────────────────────────────────────────────
        private static string LabelText(Control c)
        {
            if (c == null) return null;
            var al = c as AntdUI.Label;
            return al != null ? al.Text : c.Text;
        }

        private static object Field(object target, string name)
        {
            if (target == null) return null;
            var f = target.GetType().GetField(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return f == null ? null : f.GetValue(target);
        }

        private static Control Descendant(Control root, string name)
        {
            foreach (var c in UiSmokeRunner.AllControls(root))
                if (c.Name == name) return c;
            return null;
        }

        /// <summary>文件读坏替身：Query 直接抛，模拟"存储目录不可读/句柄坏"。</summary>
        private sealed class ThrowingHistoryStore : Gdterm.Logging.CommandHistoryStore
        {
            public ThrowingHistoryStore(string dir) : base(dir) { }

            public int QueryCount { get; private set; }

            public override IList<CommandHistoryEntry> Query(CommandHistoryQuery query = null)
            {
                QueryCount++;
                throw new IOException("smoke: 命令历史存储不可读");
            }
        }
    }
}
