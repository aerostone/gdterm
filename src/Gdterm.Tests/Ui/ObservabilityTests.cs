using System;
using System.IO;
using System.Threading.Tasks;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 可观测性修复失败信号测试（change: 2026-10-06-ui-observability-fix）：
    ///   A8=CrashLog 级别三例 / A5=Swallowed 的 ui.log 镜像两例 /
    ///   A4=Toast 级事件进通知中心 / A9=并发写 + FlushSync 排空。
    /// 依赖 Gdterm.UI 的 InternalsVisibleTo("Gdterm.Tests")。
    /// </summary>
    public static class ObservabilityTests
    {
        public static void Run()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-obs-" + Guid.NewGuid().ToString("N"));
            CrashLog.Initialize(dir);

            // ── A8：空消息异常 = ERROR（原恒真死条件让它落 INFO）──
            CrashLog.Write("Test.EmptyMsg", new Exception(""), isTerminating: false);
            Assert.True(CrashLog.FlushSync(5000), "A8 flush drained");
            Assert.Contains(ReadAll(dir, "diag.log"), "[ERROR] Test.EmptyMsg",
                "A8 空消息异常落 ERROR");

            // ── A8：info 前缀保持 INFO（前缀保护不回归）──
            CrashLog.Write("info:Test.InfoKeep", new Exception("m"));
            CrashLog.FlushSync(5000);
            Assert.Contains(ReadAll(dir, "diag.log"), "[INFO] Test.InfoKeep",
                "A8 info 前缀保持 INFO");

            // ── A8：terminating 且 ex 为空 = FATAL（原内层 ex!=null 守卫到不了 FATAL）──
            CrashLog.Write("Test.TermNullEx", null, isTerminating: true);
            CrashLog.FlushSync(5000);
            Assert.Contains(ReadAll(dir, "diag.log"), "[FATAL] Test.TermNullEx",
                "A8 terminating 无异常落 FATAL");

            // ── A5：UI 前缀源 Swallowed 镜像进 ui.log；非 UI 源不镜像 ──
            DiagLog.Swallowed("MainForm.UnitProbe", new Exception("ui-probe"));
            DiagLog.Swallowed("Zzq.OtherProbe", new Exception("non-ui"));
            CrashLog.FlushSync(5000);
            var diag = ReadAll(dir, "diag.log");
            var ui = ReadAll(dir, "ui.log");
            Assert.Contains(diag, "MainForm.UnitProbe", "A5 Swallowed 进 diag.log");
            Assert.Contains(ui, "[WARN] MainForm.UnitProbe", "A5 UI 前缀 Swallowed 镜像 ui.log");
            Assert.NotContains(ui, "Zzq.OtherProbe", "A5 非 UI 源不镜像 ui.log");

            // ── A4：直连 Push 可读回 + ToastNotifier.Show 统一留痕通知中心 ──
            var direct = "probe-direct-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            NotificationCenterPanel.Push("PROBE", direct);
            Assert.True(ContainsLine(NotificationCenterPanel.Snapshot(), direct),
                "A4 Push 可经 Snapshot 读回");

            var viaToast = "probe-toast-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // 可能尝试弹窗（测试进程无消息循环）——Show 内部全路径 catch，留痕在弹窗前已完成
            try { ToastNotifier.Show(viaToast, ToastNotifier.Level.Warning); }
            catch (Exception ex) { Assert.True(true, "A4 toast 异常被 Show 内部吞掉: " + ex.Message); }
            Assert.True(ContainsLine(NotificationCenterPanel.Snapshot(), viaToast),
                "A4 ToastNotifier.Show 留痕通知中心");

            // ── A9：200 条并发写全落盘 + FlushSync 排空 ──
            const int n = 200;
            Parallel.For(0, n, i => CrashLog.Write("Test.Par." + i, null));
            Assert.True(CrashLog.FlushSync(10000), "A9 flush drained");
            var all = ReadAll(dir, "diag.log");
            int hits = 0;
            for (int i = 0; i < n; i++)
                if (all.Contains("Test.Par." + i + " |")) hits++;
            Assert.Equal(n, hits, "A9 并发 200 条全部落盘");
            Assert.Equal(0, CrashLog.DroppedCount, "A9 队列未丢弃");
        }

        private static bool ContainsLine(string[] lines, string needle)
        {
            if (lines == null) return false;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i] != null && lines[i].Contains(needle)) return true;
            return false;
        }

        private static string ReadAll(string dir, string file)
        {
            var p = Path.Combine(dir, file);
            try { return File.Exists(p) ? File.ReadAllText(p) : ""; }
            catch { return ""; }
        }
    }
}
