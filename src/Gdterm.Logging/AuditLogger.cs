using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Gdterm.Logging.Models;

namespace Gdterm.Logging
{
    /// <summary>
    /// 审计日志实现——JSON Lines 格式，支持配置化开关、脱敏、加密、轮转
    /// </summary>
    public class AuditLogger : IAuditLogger, IDisposable
    {
        private readonly string _logDirectory;
        private readonly AuditLogConfig _config;
        private readonly LogSanitizer _sanitizer;
        private readonly object _lock = new object();
        private StreamWriter _writer;
        private string _currentFilePath;
        private long _currentFileSize;
        private bool _disposed;

        public AuditLogger(string logDirectory, AuditLogConfig config = null)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
            _config = config ?? new AuditLogConfig();
            _sanitizer = new LogSanitizer(_config.SanitizeReplacement);

            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);

            CleanupOldFiles();
        }

        public void LogConnection(string connectionId, string host, string protocol, ConnectionAction action)
        {
            if (!_config.LogConnections) return;

            var entry = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ConnectionId = connectionId,
                EventType = "Connection",
                Detail = $"{{\"host\":\"{host}\",\"protocol\":\"{protocol}\",\"action\":\"{action}\"}}"
            };

            WriteEntry(entry);
        }

        public void LogCredentialUse(string connectionId, string credentialRefId, CredentialAction action)
        {
            if (!_config.LogCredentialUsage) return;

            // 脱敏：不记录完整的 credentialRefId，只记录前4位
            var maskedId = !string.IsNullOrEmpty(credentialRefId) && credentialRefId.Length > 4
                ? credentialRefId.Substring(0, 4) + "****"
                : "****";

            var entry = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ConnectionId = connectionId,
                EventType = "Credential",
                Detail = $"{{\"credentialRef\":\"{maskedId}\",\"action\":\"{action}\"}}"
            };

            WriteEntry(entry);
        }

        public void LogCommand(string connectionId, string command)
        {
            if (!_config.LogCommands) return;

            // 脱敏命令内容
            var sanitizedCommand = _config.SanitizeCommands
                ? _sanitizer.Sanitize(command)
                : command;

            var entry = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ConnectionId = connectionId,
                EventType = "Command",
                Detail = $"{{\"command\":\"{EscapeJson(sanitizedCommand)}\"}}"
            };

            WriteEntry(entry);
        }

        public void LogAiInteraction(string connectionId, string prompt, string response)
        {
            if (!_config.LogAiInteractions) return;

            // 脱敏 AI 交互内容
            var sanitizedPrompt = _config.SanitizeAiContent
                ? _sanitizer.Sanitize(prompt)
                : prompt;
            var sanitizedResponse = _config.SanitizeAiContent
                ? _sanitizer.Sanitize(response)
                : response;

            // 截断过长内容
            if (sanitizedPrompt.Length > 500)
                sanitizedPrompt = sanitizedPrompt.Substring(0, 500) + "...";
            if (sanitizedResponse.Length > 500)
                sanitizedResponse = sanitizedResponse.Substring(0, 500) + "...";

            var entry = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ConnectionId = connectionId,
                EventType = "AiInteraction",
                Detail = $"{{\"prompt\":\"{EscapeJson(sanitizedPrompt)}\",\"response\":\"{EscapeJson(sanitizedResponse)}\"}}"
            };

            WriteEntry(entry);
        }

        public void LogSecurityEvent(SecurityEvent evt, string detail)
        {
            if (!_config.LogSecurityEvents) return;

            // 脱敏安全事件详情
            var sanitizedDetail = _config.SanitizeCommands
                ? _sanitizer.Sanitize(detail)
                : detail;

            var entry = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ConnectionId = "",
                EventType = "Security",
                Detail = $"{{\"event\":\"{evt}\",\"detail\":\"{EscapeJson(sanitizedDetail)}\"}}"
            };

            WriteEntry(entry);
        }

        public IList<AuditEntry> Query(AuditQuery query, int limit = 100)
        {
            var result = new List<AuditEntry>();

            lock (_lock)
            {
                var logFiles = GetLogFilesSorted();

                foreach (var file in logFiles)
                {
                    try
                    {
                        var lines = File.ReadAllLines(file, Encoding.UTF8);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var entry = ParseAuditEntry(line);
                            if (entry == null) continue;

                            if (MatchesQuery(entry, query))
                            {
                                result.Add(entry);
                                if (result.Count >= limit) return result;
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }
            }

            return result;
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

        private void WriteEntry(AuditEntry entry)
        {
            lock (_lock)
            {
                if (_writer == null || _currentFileSize > _config.MaxFileSizeMB * 1024 * 1024)
                {
                    RotateFile();
                }

                var json = SerializeAuditEntry(entry);

                if (_config.EncryptLogs)
                {
                    json = EncryptString(json);
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
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            CleanupOldFiles();
            InitializeCurrentFile();
        }

        private void CleanupOldFiles()
        {
            var files = GetLogFilesSorted();

            // 按数量限制
            while (files.Count > _config.MaxFileCount)
            {
                try { File.Delete(files.Last()); files.RemoveAt(files.Count - 1); }
                catch { break; }
            }

            // 按天数限制
            var cutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoff)
                        File.Delete(file);
                }
                catch { }
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
            sb.Append($",\"eventType\":\"{EscapeJson(entry.EventType ?? "")}\"");
            sb.Append($",\"detail\":{entry.Detail ?? "{}"}");
            sb.Append('}');
            return sb.ToString();
        }

        private static AuditEntry ParseAuditEntry(string json)
        {
            try
            {
                var entry = new AuditEntry();
                entry.Timestamp = DateTime.Parse(ExtractJsonValue(json, "timestamp"));
                entry.ConnectionId = ExtractJsonValue(json, "connectionId");
                entry.EventType = ExtractJsonValue(json, "eventType");
                entry.Detail = ExtractJsonDetail(json);
                return entry;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            var keyPattern = $"\"{key}\":\"";
            var startIndex = json.IndexOf(keyPattern, StringComparison.Ordinal);
            if (startIndex < 0) return "";
            startIndex += keyPattern.Length;
            var endIndex = json.IndexOf('"', startIndex);
            if (endIndex < 0) return "";
            return json.Substring(startIndex, endIndex - startIndex);
        }

        private static string ExtractJsonDetail(string json)
        {
            var keyPattern = "\"detail\":";
            var startIndex = json.IndexOf(keyPattern, StringComparison.Ordinal);
            if (startIndex < 0) return "{}";
            startIndex += keyPattern.Length;
            var braceCount = 0;
            var endIndex = startIndex;
            for (int i = startIndex; i < json.Length; i++)
            {
                if (json[i] == '{') braceCount++;
                else if (json[i] == '}') braceCount--;
                if (braceCount == 0)
                {
                    endIndex = i + 1;
                    break;
                }
            }
            return json.Substring(startIndex, endIndex - startIndex);
        }

        private static bool MatchesQuery(AuditEntry entry, AuditQuery query)
        {
            if (query == null) return true;

            if (!string.IsNullOrEmpty(query.ConnectionId) &&
                entry.ConnectionId != query.ConnectionId)
                return false;

            if (!string.IsNullOrEmpty(query.EventType) &&
                entry.EventType != query.EventType)
                return false;

            if (query.From.HasValue && entry.Timestamp < query.From.Value)
                return false;

            if (query.To.HasValue && entry.Timestamp > query.To.Value)
                return false;

            return true;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        /// <summary>
        /// 简单加密（Base64 + XOR，仅用于基本保护，非强加密）
        /// </summary>
        private static string EncryptString(string plainText)
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            // XOR with a simple key (实际应用中应使用 AES)
            var key = Encoding.UTF8.GetBytes("gdterm-audit-log-key");
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= key[i % key.Length];
            }
            return Convert.ToBase64String(bytes);
        }

        private static string DecryptString(string cipherText)
        {
            var bytes = Convert.FromBase64String(cipherText);
            var key = Encoding.UTF8.GetBytes("gdterm-audit-log-key");
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= key[i % key.Length];
            }
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
