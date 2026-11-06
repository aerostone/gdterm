using System;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// 非致命诊断日志——把原先空 catch 的异常写到 crash.jsonl，
    /// 不改变控制流、绝不抛出。供 dispose / 关签 / 关闭路径使用。
    /// </summary>
    internal static class DiagLog
    {
        /// <summary>
        /// 记录被吞掉的异常。source 建议 "Class.Method:step"。
        /// </summary>
        public static void Swallowed(string source, Exception ex)
        {
            try
            {
                // A5：UI 前缀源的被吞异常同样镜像到 logs/ui.log（与 Info 同规则）
                CrashLog.Write("swallowed:" + (source ?? "unknown"), ex, isTerminating: false,
                    uiFile: IsUiSource(source));
            }
            catch
            {
                // 诊断本身失败时保持静默
            }
        }

        /// <summary>
        /// UI 相关 source 前缀：命中时 Info 与 Swallowed 额外镜像一份到 logs/ui.log（主日志 diag.log 仍全量保留）。
        /// 覆盖对话框布局/字体缩放/标签页切换等界面排查场景。
        /// </summary>
        private static readonly string[] UiSourcePrefixes =
        {
            "ConnDialog",
            "MainForm",
            "FormFontPolicy",
            "UIFont",
            "KeePassEntryPicker",
            "TerminalControl.Appearance",
            "TerminalControl.FontMetrics",
            "TerminalControl.ResumeRendering",
            "TabSelect.",
            "TabContainer."
        };

        private static bool IsUiSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            for (int i = 0; i < UiSourcePrefixes.Length; i++)
                if (source.StartsWith(UiSourcePrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// 失败日志：功能没能完成（列表加载失败、采样失败、导出失败）。
        /// 与 <see cref="Swallowed"/> 的区别：Swallowed 是「可以不管的边角」，
        /// Error 是「用户本来期待成功的事没成功」，必须留痕到 crash.jsonl + ui.log。
        /// 可视化反馈由调用方另做（Toast / 状态行 / Message.error）——
        /// 日志只是兜底证据，不能当反馈用。
        /// </summary>
        public static void Error(string source, Exception ex)
        {
            try
            {
                CrashLog.Write("error:" + (source ?? "unknown"), ex, isTerminating: false,
                    uiFile: true);
            }
            catch
            {
                // 诊断本身失败时保持静默
            }
        }

        /// <summary>失败日志（无异常对象：远端返回错误码、接口返回失败等）。</summary>
        public static void Error(string source, string message)
        {
            try
            {
                CrashLog.Write("error:" + (source ?? "unknown"),
                    new Exception(message ?? ""), isTerminating: false, uiFile: true);
            }
            catch { }
        }

        /// <summary>
        /// 试运行阶段信息日志——写入 crash.jsonl（source 前缀 info:），永不抛出。
        /// UI 相关 source 会同时镜像到 logs/ui.log。
        /// </summary>
        public static void Info(string source, string message)
        {
            try
            {
                CrashLog.Write(
                    "info:" + (source ?? "unknown"),
                    new Exception(message ?? ""),
                    isTerminating: false,
                    uiFile: IsUiSource(source));
            }
            catch { }
        }

        /// <summary>
        /// 执行 action；异常写入 DiagLog 后吞掉（best-effort dispose 模式）。
        /// </summary>
        public static void Try(string source, Action action)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Swallowed(source, ex);
            }
        }
    }
}
