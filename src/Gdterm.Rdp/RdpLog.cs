using System;

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 诊断日志——静态 sink 模式：Gdterm.UI 启动时把 sink 接到 DiagLog/CrashLog，
    /// RdpClient 全链路（CLSID 探测/OCX 实例化/属性设置/事件）写入 data/logs/diag.log。
    /// 未接线时静默丢弃（比如单元测试环境），绝不抛出。
    /// </summary>
    public static class RdpLog
    {
        private static volatile Action<string, string> _sink;

        /// <summary>UI 启动时接线；传 null 断开。</summary>
        public static void Initialize(Action<string, string> sink)
        {
            _sink = sink;
        }

        /// <summary>信息日志。source 建议 "RdpClient.Method:step"。</summary>
        public static void Info(string source, string message)
        {
            try { _sink?.Invoke("info:" + (source ?? "unknown"), message); } catch { }
        }

        /// <summary>记录被吞掉的异常（不改变控制流）。</summary>
        public static void Swallowed(string source, Exception ex)
        {
            try { _sink?.Invoke("swallowed:" + (source ?? "unknown"), ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message); } catch { }
        }
    }
}
