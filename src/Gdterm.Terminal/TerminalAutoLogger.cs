using System;
using System.IO;
using System.Text;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端自动日志记录器——连接即录，按主机+时间命名，自动轮转
    /// </summary>
    public class TerminalAutoLogger : IDisposable
    {
        private StreamWriter _writer;
        private readonly object _lock = new object();
        private bool _disposed;
        private int _bytesWritten;
        private string _currentPath;

        public bool IsRecording => _writer != null;
        public string CurrentLogPath => _currentPath;
        public long BytesWritten => _bytesWritten;

        // ── 配置 ──
        public string LogDirectory { get; set; }
        // 默认收紧：10MB x 3，避免便携环境磁盘膨胀（可按需调大）
        public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10MB
        public int MaxFileCount { get; set; } = 3;
        public bool EnableTimestamp { get; set; } = true;
        public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

        /// <summary>当日志文件切换时触发</summary>
        public event Action<string> LogFileRotated;

        /// <summary>当有新内容写入时触发（用于 UI 统计）</summary>
        public event Action<int> DataWritten;

        public TerminalAutoLogger(string logDirectory)
        {
            LogDirectory = logDirectory;
        }

        /// <summary>开始记录（连接时调用）</summary>
        public void StartRecording(string hostName, string connectionName = null)
        {
            lock (_lock)
            {
                if (_writer != null) StopRecording();

                if (!Directory.Exists(LogDirectory))
                    Directory.CreateDirectory(LogDirectory);

                var safeHost = SanitizeFileName(hostName);
                var safeName = SanitizeFileName(connectionName ?? hostName);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentPath = Path.Combine(LogDirectory, string.Format("{0}_{1}_{2}.log", safeName, safeHost, timestamp));

                _writer = new StreamWriter(_currentPath, false, Encoding.UTF8) { AutoFlush = true };
                _bytesWritten = 0;

                // 写头部
                _writer.WriteLine("# gdterm terminal log");
                _writer.WriteLine("# Host: {0}", hostName);
                _writer.WriteLine("# Started: {0}", DateTime.Now.ToString(DateFormat));
                _writer.WriteLine("# ─────────────────────────────────────");
                _writer.Flush();
            }
        }

        /// <summary>记录终端输出（每行调用）</summary>
        public void LogOutput(string line)
        {
            lock (_lock)
            {
                if (_writer == null || _disposed) return;

                try
                {
                    if (EnableTimestamp)
                    {
                        var ts = DateTime.Now.ToString(DateFormat);
                        _writer.Write("[{0}] ", ts);
                    }

                    _writer.WriteLine(line);
                    _bytesWritten += (line?.Length ?? 0) + 20;

                    // 检查轮转
                    if (_bytesWritten >= MaxFileSizeBytes)
                        RotateLog();

                    DataWritten?.Invoke(line?.Length ?? 0);
                }
                catch { }
            }
        }

        /// <summary>停止记录（断开时调用）</summary>
        public void StopRecording()
        {
            lock (_lock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.WriteLine();
                    _writer.WriteLine("# ─────────────────────────────────────");
                    _writer.WriteLine("# Ended: {0}", DateTime.Now.ToString(DateFormat));
                    _writer.Flush();
                    _writer.Close();
                }
                catch { }
                _writer = null;
                _currentPath = null;
            }
        }

        private void RotateLog()
        {
            try
            {
                _writer.Flush();
                _writer.Close();
                _writer = null;

                // 清理旧文件
                CleanOldFiles();

                // 创建新文件
                var dir = Path.GetDirectoryName(_currentPath);
                var name = Path.GetFileNameWithoutExtension(_currentPath);
                var ext = Path.GetExtension(_currentPath);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentPath = Path.Combine(dir, string.Format("{0}_{1}{2}", name, timestamp, ext));
                _writer = new StreamWriter(_currentPath, false, Encoding.UTF8) { AutoFlush = true };
                _bytesWritten = 0;

                LogFileRotated?.Invoke(_currentPath);
            }
            catch { }
        }

        private void CleanOldFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;
                var files = Directory.GetFiles(LogDirectory, "*.log");
                if (files.Length <= MaxFileCount) return;
                Array.Sort(files);
                for (int i = 0; i < files.Length - MaxFileCount; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(':', '_').Replace(' ', '_');
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                StopRecording();
            }
        }
    }
}
