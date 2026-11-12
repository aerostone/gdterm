using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 状态轴冒烟（docs/UI-UX-VERIFICATION.md §4.1 遗留缺口①）。
    ///
    /// 背景：L1 之前只 dump "满/默认态"。于是"空"以外的状态——禁用、错误、忙碌、窄窗——
    /// 一次都没进过任何门。用户真报的 bug 恰恰在这一层：按钮能重复点、点完才发现密码为空、
    /// 忙碌时毫无反馈、窄侧栏下文字被裁。
    ///
    /// 本文件把四个状态变成**可断言的对象**，且断言的是语义而不是像素：
    ///   - 禁用态：不满足前置条件的动作必须 Enabled=false（预防式校验，不是点完再弹框）；
    ///   - 错误态：错误文案必须可见、非空、且用 Danger 语义色（不是灰字混在正文里）；
    ///   - 忙碌态：主操作必须禁用 + 文案改为进行中（防重复提交），结束后必须复位；
    ///   - 窄窗：240px 侧栏下仍能构造、无停靠遮挡、关键动作控件仍有非零面积。
    /// 每个状态同时落一份 dump，几何规则（R1/R5/R6/越界/重叠）由 tools/ui-tree-check.py 复用。
    /// </summary>
    public static class UiStateSmoke
    {
        public static void Run(string outDir, Action<string> log, Action<bool, string> check)
        {
            // 分节跑 + 逐节留痕：某节硬崩时后面几节仍会产证据，崩溃原因也进日志
            // （CI 里"整个 case 只报一行 FAIL、dump 一张没有"是最难查的形态）。
            var errs = new List<string>();
            Section("unlock-states", log, errs, () => UnlockFormStates(outDir, log, check));
            Section("busy-scope", log, errs, () => BusyScopeContract(log, check));
            Section("narrow-panels", log, errs, () => NarrowPanels(outDir, log, check));
            if (errs.Count > 0)
                throw new Exception("UiStateSmoke 分节失败 " + errs.Count + " 节: " + string.Join(" | ", errs));
        }

        private static void Section(string name, Action<string> log, List<string> errs, Action body)
        {
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

        // ── ① 解锁框的三种状态：禁用 → 错误 → 可用 → 忙碌 → 复位 ─────────────────
        private static void UnlockFormStates(string outDir, Action<string> log, Action<bool, string> check)
        {
            var svc = new FakeKeePassService();
            log("  unlock: ctor");
            using (var f = new KeePassUnlockForm(svc))
            {
                log("  unlock: show");
                f.Show();
                f.BringToFront();
                Application.DoEvents();
                Thread.Sleep(400);
                Application.DoEvents();
                log("  unlock: shown");

                IButtonControl unlockBtn = f.AcceptButton;
                var unlock = unlockBtn as Control;
                check(unlockBtn != null && unlock != null, "state-unlock: AcceptButton 可定位（Enter 可提交）");

                // —— 禁用态 + 错误态（同一瞬间：空密码 → 键灰 + 红字说明）——
                check(unlock != null && !unlock.Enabled, "state-unlock: 空密码时「解锁」置灰（预防式校验）");
                var danger = FirstDangerLabel(f);
                check(danger != null, "state-unlock: 错误态用 Danger 语义色文案（不是无声置灰）");
                if (danger != null)
                {
                    check(danger.Visible, "state-unlock: 错误文案可见");
                    check(!string.IsNullOrWhiteSpace(danger.Text), "state-unlock: 错误文案非空=" + danger.Text);
                    check(danger.Width > 0 && danger.Height > 0,
                        "state-unlock: 错误文案有面积=" + danger.Width + "x" + danger.Height);
                }
                UiSmokeRunner.Shot(f, Path.Combine(outDir, "state-unlock-disabled.png"));
                File.WriteAllText(Path.Combine(outDir, "state-unlock-disabled.json"),
                    UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                log("dump: state-unlock-disabled.json");

                // —— 状态迁移：输入后必须转可用，且红字收回 ——
                var input = UiSmokeRunner.FindByType(f, "Input") as Control;
                check(input != null, "state-unlock: 密码输入框可定位");
                if (input != null)
                {
                    input.Text = "smoke-master-password";
                    Application.DoEvents();
                    Thread.Sleep(150);
                    Application.DoEvents();
                }
                check(unlock != null && unlock.Enabled, "state-unlock: 输入密码后「解锁」转可用（禁用态不是死的）");
                var stillDanger = FirstDangerLabel(f);
                check(stillDanger == null, "state-unlock: 校验通过后错误色收回 (残留=" +
                    (stillDanger == null ? 0 : 1) + ")");

                // —— 忙碌态：闸门挂起服务调用，抓"进行中"这一帧 ——
                var gate = new TaskCompletionSource<bool>();
                svc.UnlockGate = gate;
                if (unlockBtn != null)
                {
                    unlockBtn.PerformClick();
                    Application.DoEvents();
                    Thread.Sleep(200);
                    Application.DoEvents();
                }
                check(unlock != null && !unlock.Enabled, "state-unlock: 忙碌中主操作禁用（防重复提交）");
                check(unlock != null && (unlock.Text ?? "").Contains("解锁中"),
                    "state-unlock: 忙碌中文案可见=" + (unlock == null ? "?" : unlock.Text));
                UiSmokeRunner.Shot(f, Path.Combine(outDir, "state-unlock-busy.png"));
                File.WriteAllText(Path.Combine(outDir, "state-unlock-busy.json"),
                    UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
                log("dump: state-unlock-busy.json");

                // —— 复位契约：闸门放行后 busy 标记与按钮都必须回到初始态 ——
                gate.SetResult(true);
                for (int i = 0; i < 20 && !f.IsDisposed; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                check(f.IsUnlocked, "state-unlock: 成功解锁后 IsUnlocked=true");
                check(svc.LastPassword == "smoke-master-password",
                    "state-unlock: 密码原样送达服务（未被清洗/截断）=" + (svc.LastPassword ?? "null"));
            }
        }

        // ── ② BusyScope 契约：状态文案真的可见 + 重入不误伤外层 + finally 复位 ────
        private static void BusyScopeContract(Action<string> log, Action<bool, string> check)
        {
            string status = L10n.T("正在读取快捷键绑定…");
            using (var host = new Form())
            {
                host.ClientSize = new Size(360, 240);
                var panel = new Panel { Dock = DockStyle.Fill, Name = "busyHost" };
                var btn = new Button { Text = "run", Name = "busyBtn" };
                panel.Controls.Add(btn);
                host.Controls.Add(panel);
                host.Show();
                Application.DoEvents();
                Thread.Sleep(150);
                Application.DoEvents();

                var scope = BusyScope.Begin(panel, status, false, btn);
                check(BusyScope.IsBusy(panel), "busy-scope: 进入后 IsBusy=true");
                check(BusyScope.StatusOf(panel) == status,
                    "busy-scope: 状态文案可读回（此前 status 是死参数，用户看不到任何进度文案）");
                check(panel.AccessibleDescription == status, "busy-scope: 状态文案对读屏可见");
                check(!btn.Enabled, "busy-scope: 指定主操作被禁用");
                check(panel.Cursor == Cursors.WaitCursor, "busy-scope: 光标变沙漏");

                var reenter = BusyScope.Begin(panel, status);
                check(reenter.AlreadyBusy, "busy-scope: 重入返回 AlreadyBusy（不静默丢弃）");
                reenter.Dispose();
                check(BusyScope.IsBusy(panel), "busy-scope: 重入作用域 Dispose 不得关掉外层");

                scope.Dispose();
                check(!BusyScope.IsBusy(panel), "busy-scope: Dispose 后 IsBusy=false");
                check(btn.Enabled, "busy-scope: Dispose 后按钮复位（finally 契约）");
                check(panel.Cursor != Cursors.WaitCursor, "busy-scope: Dispose 后光标复位");
                check(panel.AccessibleDescription != status, "busy-scope: Dispose 后状态文案收回");
                log("busy-scope done");
            }
        }

        // ── ③ 窄窗：240px 侧栏下 dump（几何规则复用 L1），只断言"还活着且没重叠" ──
        private static void NarrowPanels(string outDir, Action<string> log, Action<bool, string> check)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-narrow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var portManager = new Gdterm.Tunnel.PortForwardManager();
            var registry = new Gdterm.Tools.ToolRegistry();
            try
            {
                // 文件名沿用面板名前缀：ui-tree-check 的 R3（关闭语义）按前缀对侧栏面板免判，
                // 换成 state-narrow-* 会让"面板没有 Close 钮"变成假红。
                DumpNarrow(outDir, log, check, "command-history-narrow",
                    () => new CommandHistoryPanel(new Gdterm.Logging.CommandHistoryStore(Path.Combine(tmp, "cmdhist"))));
                DumpNarrow(outDir, log, check, "port-forward-narrow",
                    () => new PortForwardPanel(portManager));
                DumpNarrow(outDir, log, check, "toolbox-narrow",
                    () => new ToolboxPanel(registry));
            }
            finally
            {
                try { registry.Dispose(); } catch { }
                try { portManager.Dispose(); } catch { }
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        /// <summary>
        /// 240×420 宿主窗（窄侧栏真实下限）里 Show + dump 单个面板。
        /// 断言口径故意保守：窄窗本来就会裁/省略，值得判的是"面板还能构造、还能布局、
        /// 主操作控件没被压成 0"——文字裁切由 ui-tree-check R6 出告警，不在这里猜阈值。
        /// </summary>
        private static void DumpNarrow(string outDir, Action<string> log, Action<bool, string> check,
            string name, Func<Control> create)
        {
            Control panel = null;
            try
            {
                using (var host = new Form())
                {
                    host.Text = name;
                    host.ClientSize = new Size(240, 420);
                    host.BackColor = GdtermColorTable.Background;
                    host.StartPosition = FormStartPosition.Manual;
                    panel = create();
                    panel.Dock = DockStyle.Fill;
                    panel.Visible = true;
                    host.Controls.Add(panel);
                    host.Show();
                    host.BringToFront();
                    Application.DoEvents();
                    Thread.Sleep(350);
                    Application.DoEvents();

                    UiSmokeRunner.AssertNoDockOverlap(host, name);
                    int n = UiSmokeRunner.CountControls(panel);
                    check(n > 3, name + ": 控件数=" + n + " (>3)");
                    var leaf = FirstInteractiveLeaf(panel);
                    check(leaf != null && leaf.Visible && leaf.Width > 0 && leaf.Height > 0,
                        name + ": 主操作控件在窄窗下仍可见且有面积=" +
                        (leaf == null ? "none" : leaf.Name + " " + leaf.Width + "x" + leaf.Height));
                    // 横向不被裁：面板内最右可见控件不得越过宿主客户区（越界由 Python 侧再判一次）
                    check(leaf == null || leaf.Right <= host.ClientSize.Width + 2,
                        name + ": 主操作控件右缘=" + (leaf == null ? -1 : leaf.Right) +
                        " 未越出窄窗宽=" + host.ClientSize.Width);

                    UiSmokeRunner.Shot(host, Path.Combine(outDir, name + ".png"));
                    File.WriteAllText(Path.Combine(outDir, name + ".json"),
                        UiTreeDumper.Dump(host), System.Text.Encoding.UTF8);
                    log("dump: " + name + ".json");
                }
            }
            finally
            {
                try { if (panel != null) panel.Dispose(); } catch { }
            }
        }

        /// <summary>第一个 ForeColor 为 Danger 的可见标签（错误态的语义色证据）。</summary>
        private static Control FirstDangerLabel(Control root)
        {
            foreach (Control c in UiSmokeRunner.AllControls(root))
            {
                if (!c.Visible) continue;
                if (c.GetType().Name.IndexOf("Label", StringComparison.Ordinal) < 0) continue;
                try { if (c.ForeColor == GdtermColorTable.Danger) return c; }
                catch { }
            }
            return null;
        }

        /// <summary>第一个可见且 Enabled 的交互叶（按钮/输入/下拉），用来判"窄窗下主操作还在"。</summary>
        private static Control FirstInteractiveLeaf(Control root)
        {
            foreach (Control c in UiSmokeRunner.AllControls(root))
            {
                if (!c.Visible || !c.Enabled) continue;
                string t = c.GetType().Name;
                if (t.IndexOf("Button", StringComparison.Ordinal) < 0
                    && t.IndexOf("Input", StringComparison.Ordinal) < 0
                    && t.IndexOf("Select", StringComparison.Ordinal) < 0) continue;
                if (c.Dock == DockStyle.Fill) continue; // 容器型控件不算"动作"
                if (c.Width <= 0 || c.Height <= 0) continue;
                return c;
            }
            return null;
        }
    }
}
