using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Logging.Models;

namespace Gdterm.Logging
{
    /// <summary>
    /// 审计日志实现——JSON Lines 格式，按大小轮转，支持审计查询
    /// </summary>
    public class AuditLogger : IAuditLogger, IDisposable
    {
        private readonly string _logDirectory;
        private readonly LogRotationConfig _config;
        private readonly object _lock = new object();
        private StreamWriter _writer;
        private string _currentFilePath;
        private long _currentFileSize;
        private bool _disposed;

        /// <param name="logDirectory">日志文件存储目录</param>
        /// <param name="config">轮转配置（null 使用默认值）</param>
        public AuditLogger(string logDirectory, LogRotationConfig config = null)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
            _config = config ?? new LogRotationConfig();

            // 确保目录存在
            Directory.CreateDirectory(_logDirectory);

            // 初始化当前日志文件
            InitializeCurrentFile();
        }

        public void LogConnection(string connectionId, string host, string protocol, ConnectionAction action)
        {
            var detail = $"{{\"host\":\"{EscapeJson(host)}\",\"protocol\":\"{EscapeJson(protocol)}\",\"action\":\"{action}\"}}";
            WriteEntry(connectionId, "Connection", detail);
        }

        public void LogCredentialUse(string connectionId, string credentialRefId, CredentialAction action)
        {
            var detail = $"{{\"credentialRefId\":\"{EscapeJson(credentialRefId)}\",\"action\":\"{action}\"}}";
            WriteEntry(connectionId, "Credential", detail);
        }

        public void LogCommand(string connectionId, string command)
        {
            var detail = $"{{\"command\":\"{EscapeJson(command)}\"}}";
            WriteEntry(connectionId, "Command", detail);
        }

        public void LogAiInteraction(string connectionId, string prompt, string response)
        {
            var detail = $"{{\"prompt\":\"{EscapeJson(prompt)}\",\"response\":\"{EscapeJson(response)}\"}}";
            WriteEntry(connectionId, "AiInteraction", detail);
        }

        public void LogSecurityEvent(SecurityEvent evt, string detail)
        {
            var detailJson = $"{{\"event\":\"{evt}\",\"detail\":\"{EscapeJson(detail)}\"}}";
            WriteEntry(null, "Security", detailJson);
        }

        public IList<AuditEntry> Query(AuditQuery query, int limit = 100)
        {
            if (query == null) query = new AuditQuery();

            var results = new List<AuditEntry>();

            // 获取所有日志文件（按时间倒序）
            var logFiles = GetLogFilesSorted();

            foreach (var file in logFiles)
            {
                if (results.Count >= limit) break;

                try
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8);
                    // 从后往前读（最新在前）
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        if (results.Count >= limit) break;

                        var entry = ParseAuditEntry(lines[i]);
                        if (entry == null) continue;

                        // 应用过滤条件
                        if (MatchesQuery(entry, query))
                        {
                            results.Add(entry);
                        }
                    }
                }
                catch
                {
                    // 文件读取失败，跳过
                }
            }

            return results;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }

        private void WriteEntry(string connectionId, string eventType, string detail)
        {
            var entry = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ConnectionId = connectionId,
                EventType = eventType,
                Detail = detail
            };

            var json = SerializeAuditEntry(entry);

            lock (_lock)
            {
                // 检查是否需要轮转
                if (_currentFileSize + json.Length + Environment.NewLine.Length > _config.MaxFileSizeBytes)
                {
                    RotateFile();
                }

                _writer.WriteLine(json);
                _writer.Flush();
                _currentFileSize += json.Length + Environment.NewLine.Length;
            }
        }

        private void InitializeCurrentFile()
        {
            _currentFilePath = Path.Combine(_logDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
            _writer = new StreamWriter(_currentFilePath, true, Encoding.UTF8);
            _currentFileSize = new FileInfo(_currentFilePath).Length;
        }

        private void RotateFile()
        {
            // 关闭当前文件
            _writer?.Flush();
            _writer?.Dispose();

            // 创建新文件
            InitializeCurrentFile();

            // 删除超限的旧文件
            CleanupOldFiles();
        }

        private void CleanupOldFiles()
        {
            var files = GetLogFilesSorted();

            // 按文件数限制删除
            while (files.Count > _config.MaxFileCount)
            {
                try
                {
                    File.Delete(files[files.Count - 1]);
                    files.RemoveAt(files.Count - 1);
                }
                catch { break; }
            }

            // 按保留天数删除
            var cutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch { /* best-effort */ }
            }
        }

        private List<string> GetLogFilesSorted()
        {
            return Directory.GetFiles(_logDirectory, "audit-*.jsonl")
                .OrderByDescending(f => f)
                .ToList();
        }

        private static string SerializeAuditEntry(AuditEntry entry)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"timestamp\":\"{entry.Timestamp:O}\"");
            sb.Append($",\"connectionId\":\"{EscapeJson(entry.ConnectionId ?? "")}\"");
            sb.Append($",\"eventType\":\"{EscapeJson(entry.EventType)}\"");
            sb.Append($",\"detail\":{entry.Detail}");
            sb.Append('}');
            return sb.ToString();
        }

        private static AuditEntry ParseAuditEntry(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.StartsWith("{"))
                return null;

            try
            {
                var entry = new AuditEntry();

                // 简单 JSON 解析（不依赖外部库）
                var ts = ExtractJsonValue(json, "timestamp");
                if (ts != null && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    entry.Timestamp = dt;

                entry.ConnectionId = ExtractJsonValue(json, "connectionId");
                entry.EventType = ExtractJsonValue(json, "eventType");

                // detail 是嵌套 JSON，提取 "detail": 后面的部分
                var detailIdx = json.IndexOf("\"detail\":");
                if (detailIdx >= 0)
                {
                    entry.Detail = json.Substring(detailIdx + 9).TrimEnd('}');
                    // 移除尾部的 }
                    if (entry.Detail.EndsWith("}"))
                        entry.Detail = entry.Detail.Substring(0, entry.Detail.Length - 1);
                }

                return entry;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            var search = $"\"{key}\":\"";
            var start = json.IndexOf(search);
            if (start < 0) return null;

            start += search.Length;
            var end = json.IndexOf("\"", start);
            if (end < 0) return null;

            return UnescapeJson(json.Substring(start, end - start));
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string UnescapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        private static bool MatchesQuery(AuditEntry entry, AuditQuery query)
        {
            if (query.From.HasValue && entry.Timestamp < query.From.Value)
                return false;

            if (query.To.HasValue && entry.Timestamp > query.To.Value)
                return false;

            if (!string.IsNullOrEmpty(query.ConnectionId) &&
                !string.Equals(entry.ConnectionId, query.ConnectionId, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(query.EventType) &&
                !string.Equals(entry.EventType, query.EventType, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
    }
}
