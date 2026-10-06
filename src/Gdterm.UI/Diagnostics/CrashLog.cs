using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        // A9：有界队列 + 单消费线程批写——调用方（多为 UI 事件链）不再同步开文件。
        // FATAL/ProcessExit 走 FlushSync 同步排空；队列满丢新并计数（_dropped）。
        private const int QueueCapacity = 1000;
        private static BlockingCollection<LogItem> _queue;
        private static int _pending;   // 已入队未落盘
        private static int _dropped;   // 满队列丢弃计数
        private static int _exitHooked;
        private static readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);

        private struct LogItem
        {
            public string Text;
            public bool Ui;
        }

        public static void Initialize(string logsDirectory)
        {
            if (_exitHooked == 0)
            {
                _exitHooked = 1;
                // 进程退出前排空队列（正常退出路径；后台消费线程在 ProcessExit 期间仍存活）
                try { AppDomain.CurrentDomain.ProcessExit += (s, e) => FlushSync(3000); } catch { }
            }
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
                else if (isTerminating)
                {
                    // A8：终止路径恒 FATAL（即使 ex 为空）
                    level = "FATAL";
                }
                else if (ex != null && !src.StartsWith("info", StringComparison.OrdinalIgnoreCase))
                {
                    // A8：带异常一律 ERROR——原恒真死条件使空消息异常落到 INFO
                    level = "ERROR";
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

                Interlocked.Increment(ref _written);
                Enqueue(sb.ToString(), uiFile);
                // A9：FATAL 路径同步排空，确保崩溃记录先落盘再继续（MessageBox/退出）
                if (isTerminating) FlushSync(3000);
            }
            catch
            {
                // 绝不因日志本身再抛
            }
        }

        /// <summary>入队（懒启动消费线程）。队列满丢新并计数，绝不阻塞调用方。</summary>
        private static void Enqueue(string text, bool ui)
        {
            EnsureConsumer();
            var q = _queue;
            if (q == null) return;
            Interlocked.Increment(ref _pending);
            _idle.Reset();
            if (!q.TryAdd(new LogItem { Text = text, Ui = ui }, 0))
            {
                Interlocked.Increment(ref _dropped);
                if (Interlocked.Decrement(ref _pending) == 0) _idle.Set();
            }
        }

        private static void EnsureConsumer()
        {
            if (_queue != null) return;
            lock (_lock)
            {
                if (_queue != null) return;
                var q = new BlockingCollection<LogItem>(QueueCapacity);
                var t = new Thread(() => Consume(q)) { IsBackground = true, Name = "CrashLogWriter" };
                _queue = q;
                t.Start();
            }
        }

        /// <summary>单消费线程：批 ≤64 条合并落盘（轮转检查每批一次）。</summary>
        private static void Consume(BlockingCollection<LogItem> q)
        {
            var batch = new List<LogItem>(64);
            while (true)
            {
                batch.Clear();
                LogItem item;
                try
                {
                    if (!q.TryTake(out item, 200)) continue;
                }
                catch (InvalidOperationException) { break; } // 含 ObjectDisposedException（其派生类）——分开写会 CS0160
                batch.Add(item);
                LogItem more;
                while (batch.Count < 64 && q.TryTake(out more, 0)) batch.Add(more);
                try
                {
                    var main = new StringBuilder(batch.Count * 256);
                    StringBuilder uiSb = null;
                    for (int i = 0; i < batch.Count; i++)
                    {
                        main.Append(batch[i].Text);
                        if (batch[i].Ui)
                        {
                            if (uiSb == null) uiSb = new StringBuilder(256);
                            uiSb.Append(batch[i].Text);
                        }
                    }
                    AppendWithRotate(_path, main.ToString());
                    if (uiSb != null) AppendWithRotate(_uiPath, uiSb.ToString());
                }
                catch { }
                finally
                {
                    for (int i = 0; i < batch.Count; i++)
                        if (Interlocked.Decrement(ref _pending) == 0) _idle.Set();
                }
            }
        }

        /// <summary>同步排空队列（FATAL / 测试 / ProcessExit）。返回 true=已排空。</summary>
        public static bool FlushSync(int timeoutMs = 5000)
        {
            if (_queue == null) return true;
            try { return _idle.Wait(Math.Max(0, timeoutMs)); }
            catch { return _pending == 0; }
        }

        /// <summary>满队列丢弃计数（A9 可观测性）。</summary>
        public static int DroppedCount => _dropped;

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
                    try { if (File.Exists(bak)) File.Delete(bak); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CrashLog", exSwallowed); } catch { } }
                    try { File.Move(path, bak); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CrashLog", exSwallowed); } catch { } }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CrashLog", exSwallowed); } catch { } }

            try { File.AppendAllText(path, text, Encoding.UTF8); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CrashLog", exSwallowed); } catch { } }
        }

        public static int WrittenCount => _written;

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
