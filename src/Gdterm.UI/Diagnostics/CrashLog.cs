using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// 轻量诊断/崩溃落盘——不依赖 AuditLogger 生命周期。
    /// 人可读文本：logs/diag.log（过程审计仍可另写 jsonl）。
    /// </summary>
    internal static class CrashLog
    {
        private static readonly object _lock = new object();
        private static string _path;
        private static string _uiPath;
        private static int _written;

        public static void Initialize(string logsDirectory)
        {
            if (string.IsNullOrEmpty(logsDirectory))
                return;
            try
            {
                if (!Directory.Exists(logsDirectory))
                    Directory.CreateDirectory(logsDirectory);
                // 人可读主日志；旧 crash.jsonl 不再写入
                _path = Path.Combine(logsDirectory, "diag.log");
                // UI 专项日志（界面/对话框/字体缩放），与主日志内容重叠，方便单独排查界面问题
                _uiPath = Path.Combine(logsDirectory, "ui.log");
            }
            catch
            {
                _path = null;
                _uiPath = null;
            }
        }

        /// <param name="uiFile">同时镜像到 logs/ui.log（UI 专项排查）。</param>
        public static void Write(string source, Exception ex, bool isTerminating = false, bool uiFile = false)
        {
            if (ex == null && string.IsNullOrEmpty(source))
                return;

            try
            {
                var sb = new StringBuilder(256);
                // 2026-07-26 17:29:05 [INFO] source | message
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.Append(' ');
                var level = "INFO";
                var src = source ?? "";
                if (src.StartsWith("info:", StringComparison.OrdinalIgnoreCase))
                {
                    level = "INFO";
                    src = src.Substring(5);
                }
                else if (src.StartsWith("swallowed:", StringComparison.OrdinalIgnoreCase))
                {
                    level = "WARN";
                    src = src.Substring(10);
                }
                else if (isTerminating || (ex != null && !(ex is Exception && string.IsNullOrEmpty(ex.Message))))
                {
                    if (ex != null && !src.StartsWith("info", StringComparison.OrdinalIgnoreCase))
                        level = isTerminating ? "FATAL" : "ERROR";
                }
                if (ex != null && level == "INFO" && !string.IsNullOrEmpty(ex.Message)
                    && (src.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                        || src.IndexOf("swallowed", StringComparison.OrdinalIgnoreCase) >= 0))
                    level = "ERROR";

                sb.Append('[').Append(level).Append("] ");
                sb.Append(src);
                sb.Append(" | thr=").Append(Thread.CurrentThread.ManagedThreadId);
                if (ex != null)
                {
                    // DiagLog.Info 用假 Exception 只带 message
                    var msg = ex.Message ?? "";
                    if (ex.GetType() == typeof(Exception) && string.IsNullOrEmpty(ex.StackTrace)
                        && (level == "INFO" || src.Length > 0))
                    {
                        sb.Append(" | ").Append(msg);
                    }
                    else
                    {
                        sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(msg);
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            sb.AppendLine();
                            sb.Append(Trim(ex.StackTrace, 2000));
                        }
                        if (ex.InnerException != null)
                        {
                            sb.AppendLine();
                            sb.Append("  inner: ").Append(Trim(ex.InnerException.ToString(), 800));
                        }
                    }
                }
                sb.AppendLine();

                lock (_lock)
                {
                    Interlocked.Increment(ref _written);
                    AppendWithRotate(_path, sb.ToString());
                    if (uiFile)
                        AppendWithRotate(_uiPath, sb.ToString());
                }
            }
            catch
            {
                // 绝不因日志本身再抛
            }
        }

        /// <summary>单文件追加 + 超 5MB 轮转（path 为 null/空时静默跳过）。</summary>
        private static void AppendWithRotate(string path, string text)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    var bak = path + ".1";
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                    try { File.Move(path, bak); } catch { }
                }
            }
            catch { }

            try { File.AppendAllText(path, text, Encoding.UTF8); } catch { }
        }

        public static int WrittenCount => _written;

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
