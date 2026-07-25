using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Gdterm.UI.Diagnostics
{
    /// <summary>
    /// 轻量崩溃/未处理异常落盘——不依赖 AuditLogger 生命周期，
    /// 保证启动早期与进程退出阶段也能写。
    /// 文件：data/logs/crash.jsonl（JSON Lines，手写序列化）
    /// </summary>
    internal static class CrashLog
    {
        private static readonly object _lock = new object();
        private static string _path;
        private static int _written;

        public static void Initialize(string logsDirectory)
        {
            if (string.IsNullOrEmpty(logsDirectory))
                return;
            try
            {
                if (!Directory.Exists(logsDirectory))
                    Directory.CreateDirectory(logsDirectory);
                _path = Path.Combine(logsDirectory, "crash.jsonl");
            }
            catch
            {
                _path = null;
            }
        }

        public static void Write(string source, Exception ex, bool isTerminating = false)
        {
            if (ex == null && string.IsNullOrEmpty(source))
                return;

            try
            {
                var sb = new StringBuilder(512);
                sb.Append("{\"ts\":\"");
                sb.Append(DateTime.UtcNow.ToString("o"));
                sb.Append("\",\"source\":\"");
                sb.Append(Escape(source ?? ""));
                sb.Append("\",\"terminating\":");
                sb.Append(isTerminating ? "true" : "false");
                sb.Append(",\"thread\":");
                sb.Append(Thread.CurrentThread.ManagedThreadId);
                if (ex != null)
                {
                    sb.Append(",\"type\":\"");
                    sb.Append(Escape(ex.GetType().FullName ?? "Exception"));
                    sb.Append("\",\"message\":\"");
                    sb.Append(Escape(ex.Message));
                    sb.Append("\",\"stack\":\"");
                    sb.Append(Escape(Trim(ex.ToString(), 4000)));
                    sb.Append("\"");
                    if (ex.InnerException != null)
                    {
                        sb.Append(",\"inner\":\"");
                        sb.Append(Escape(Trim(ex.InnerException.ToString(), 1000)));
                        sb.Append("\"");
                    }
                }
                sb.Append("}");
                sb.AppendLine();

                lock (_lock)
                {
                    var path = _path;
                    if (string.IsNullOrEmpty(path))
                        return;

                    // 简单体积保护：超过 ~5MB 轮转到 .1
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

                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                    Interlocked.Increment(ref _written);
                }
            }
            catch
            {
                // 最后一道：绝不因日志本身再抛
            }
        }

        public static int WrittenCount => _written;

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
