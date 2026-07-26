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
                CrashLog.Write("swallowed:" + (source ?? "unknown"), ex, isTerminating: false);
            }
            catch
            {
                // 诊断本身失败时保持静默
            }
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
