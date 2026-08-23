using System;

namespace Gdterm.Terminal.Diagnostics
{
    /// <summary>
    /// 终端层诊断日志——静态 sink，由宿主（Gdterm.UI.Program）接线到 CrashLog。
    /// Gdterm.Terminal 不引用 Gdterm.UI（DiagLog 在那边），与 RdpLog 同一模式：
    /// 未接线时静默，绝不抛异常，供 渲染器/会话/引擎 写诊断。
    /// source 约定：直接传 "info:"/"swallowed:" 前缀或裸名，CrashLog 解析为 INFO/WARN。
    /// </summary>
    public static class TerminalLog
    {
        private static Action<string, string> _sink;

        public static void Initialize(Action<string, string> sink)
        {
            _sink = sink;
        }

        public static void Info(string source, string message)
        {
            var s = _sink;
            if (s == null) return;
            try { s("info:" + source, message ?? ""); } catch { }
        }

        public static void Swallowed(string source, Exception ex)
        {
            var s = _sink;
            if (s == null) return;
            try { s("swallowed:" + source, ex != null ? ex.Message : "null"); } catch { }
        }
    }
}
