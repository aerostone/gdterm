using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Logging
{
    /// <summary>
    /// 命令历史存储——JSON Lines 格式，支持按会话/时间/关键字查询
    /// 与审计日志集成，提供完整的命令追踪能力
    /// </summary>
    public class CommandHistoryStore : IDisposable
    {
        private readonly string _logDirectory;
        private readonly object _lock = new object();
        private StreamWriter _writer;
        private string _currentFilePath;
        private long _currentFileSize;
        private bool _disposed;

        private const int MaxFileSizeMB = 10;
        private const int MaxOutputLines = 50; // 每条记录最多保存的输出行数

        public CommandHistoryStore(string logDirectory)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));

            if (!Directory.Exists(_logDirectory))
                Directory.CreateDirectory(_logDirectory);

            InitializeCurrentFile();
        }

        /// <summary>
        /// 记录命令执行
        /// </summary>
        private static readonly LogSanitizer Sanitizer = new LogSanitizer("***");

        public void RecordCommand(CommandHistoryEntry entry)
        {
            if (entry == null) return;
            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = Guid.NewGuid().ToString("N");
            if (entry.ExecutedAt == default)
                entry.ExecutedAt = DateTime.UtcNow;

            // 落盘前脱敏：CLI 位置参数 / token / 连接串等
            if (!string.IsNullOrEmpty(entry.Command))
                entry.Command = Sanitizer.Sanitize(entry.Command);

            // 截断过长输出
            if (!string.IsNullOrEmpty(entry.Output))
            {
                entry.Output = Sanitizer.Sanitize(entry.Output);
                var lines = entry.Output.Split('\n');
                if (lines.Length > MaxOutputLines)
                {
                    entry.Output = string.Join("\n", lines.Take(MaxOutputLines)) +
                                   $"\n... (共 {lines.Length} 行，已截断)";
                }
            }

            lock (_lock)
            {
                if (_currentFileSize > MaxFileSizeMB * 1024 * 1024)
                {
                    RotateFile();
                }

                var json = SerializeEntry(entry);
                _writer.WriteLine(json);
                _writer.Flush();
                _currentFileSize += json.Length + Environment.NewLine.Length;
            }
        }

        /// <summary>
        /// 查询命令历史
        /// </summary>
        public IList<CommandHistoryEntry> Query(CommandHistoryQuery query = null)
        {
            var result = new List<CommandHistoryEntry>();

            lock (_lock)
            {
                _writer?.Flush();

                var files = GetLogFilesSorted();
                foreach (var file in files)
                {
                    try
                    {
                        var lines = File.ReadAllLines(file, Encoding.UTF8);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            var entry = DeserializeEntry(line);
                            if (entry == null) continue;

                            if (MatchesQuery(entry, query))
                            {
                                result.Add(entry);
                                if (result.Count >= (query?.Limit ?? 100))
                                    return query?.NewestFirst != false
                                        ? result.OrderByDescending(e => e.ExecutedAt).ToList()
                                        : result.OrderBy(e => e.ExecutedAt).ToList();
                            }
                        }
                    }
                    catch { }
                }
            }

            return query?.NewestFirst != false
                ? result.OrderByDescending(e => e.ExecutedAt).ToList()
                : result.OrderBy(e => e.ExecutedAt).ToList();
        }

        /// <summary>
        /// 获取常用命令（按使用频率排序）
        /// </summary>
        public IList<CommandFrequency> GetFrequentCommands(int limit = 20)
        {
            var all = Query(new CommandHistoryQuery { Limit = 10000 });
            return all
                .GroupBy(e => e.Command)
                .Select(g => new CommandFrequency
                {
                    Command = g.Key,
                    Count = g.Count(),
                    LastUsed = g.Max(e => e.ExecutedAt),
                    UsedHosts = g.Select(e => e.Hostname).Distinct().ToList()
                })
                .OrderByDescending(c => c.Count)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// 获取最近使用的命令（用于自动补全）
        /// </summary>
        public IList<string> GetRecentCommands(int limit = 50)
        {
            var all = Query(new CommandHistoryQuery { Limit = limit });
            return all.Select(e => e.Command).Distinct().Take(limit).ToList();
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

        private void InitializeCurrentFile()
        {
            _currentFilePath = Path.Combine(_logDirectory, $"commands-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
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
            while (files.Count > 10) // 最多保留 10 个文件
            {
                try { File.Delete(files.Last()); files.RemoveAt(files.Count - 1); }
                catch { break; }
            }
        }

        private List<string> GetLogFilesSorted()
        {
            return Directory.GetFiles(_logDirectory, "commands-*.jsonl")
                .OrderByDescending(f => f)
                .ToList();
        }

        private bool MatchesQuery(CommandHistoryEntry entry, CommandHistoryQuery query)
        {
            if (query == null) return true;

            if (!string.IsNullOrEmpty(query.ConnectionId) &&
                entry.ConnectionId != query.ConnectionId)
                return false;

            if (!string.IsNullOrEmpty(query.Hostname) &&
                entry.Hostname != null &&
                !entry.Hostname.Equals(query.Hostname, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(query.CommandContains) &&
                entry.Command != null &&
                entry.Command.IndexOf(query.CommandContains, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            if (query.From.HasValue && entry.ExecutedAt < query.From.Value)
                return false;

            if (query.To.HasValue && entry.ExecutedAt > query.To.Value)
                return false;

            if (query.IsBroadcast.HasValue && entry.IsBroadcast != query.IsBroadcast.Value)
                return false;

            if (!string.IsNullOrEmpty(query.Tag) &&
                entry.Tags != null &&
                !entry.Tags.Contains(query.Tag))
                return false;

            return true;
        }

        // ===== JSON 序列化 =====

        private static string SerializeEntry(CommandHistoryEntry entry)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"id\":\"{Escape(entry.Id)}\"");
            sb.Append($",\"connectionId\":\"{Escape(entry.ConnectionId ?? "")}\"");
            sb.Append($",\"hostname\":\"{Escape(entry.Hostname ?? "")}\"");
            sb.Append($",\"protocol\":\"{Escape(entry.Protocol ?? "")}\"");
            sb.Append($",\"command\":\"{Escape(entry.Command ?? "")}\"");
            sb.Append($",\"output\":\"{Escape(entry.Output ?? "")}\"");
            if (entry.ExitCode.HasValue)
                sb.Append($",\"exitCode\":{entry.ExitCode.Value}");
            sb.Append($",\"executedAt\":\"{entry.ExecutedAt:O}\"");
            sb.Append($",\"durationMs\":{entry.DurationMs}");
            sb.Append($",\"isBroadcast\":{(entry.IsBroadcast ? "true" : "false")}");
            if (entry.BroadcastTargets != null && entry.BroadcastTargets.Count > 0)
            {
                sb.Append(",\"broadcastTargets\":[");
                for (int i = 0; i < entry.BroadcastTargets.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"\"{Escape(entry.BroadcastTargets[i])}\"");
                }
                sb.Append(']');
            }
            if (!string.IsNullOrEmpty(entry.Tags))
                sb.Append($",\"tags\":\"{Escape(entry.Tags)}\"");
            sb.Append('}');
            return sb.ToString();
        }

        private static CommandHistoryEntry DeserializeEntry(string json)
        {
            try
            {
                return new CommandHistoryEntry
                {
                    Id = ExtractString(json, "id"),
                    ConnectionId = ExtractString(json, "connectionId"),
                    Hostname = ExtractString(json, "hostname"),
                    Protocol = ExtractString(json, "protocol"),
                    Command = ExtractString(json, "command"),
                    Output = ExtractString(json, "output"),
                    ExitCode = TryExtractInt(json, "exitCode"),
                    ExecutedAt = DateTime.TryParse(ExtractString(json, "executedAt"), out var dt) ? dt : DateTime.MinValue,
                    DurationMs = long.TryParse(ExtractString(json, "durationMs"), out var ms) ? ms : 0,
                    IsBroadcast = ExtractString(json, "isBroadcast") == "true",
                    Tags = ExtractString(json, "tags")
                };
            }
            catch { return null; }
        }

        private static string ExtractString(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return "";
            start += pattern.Length;
            int end = start;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return Unescape(json.Substring(start, end - start));
        }

        private static int? TryExtractInt(string json, string key)
        {
            var pattern = $"\"{key}\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            int end = json.IndexOfAny(new[] { ',', '}' }, start);
            if (end < 0) return null;
            if (int.TryParse(json.Substring(start, end - start).Trim(), out int val))
                return val;
            return null;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\t", "\t").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }

    /// <summary>
    /// 命令频率统计
    /// </summary>
    public class CommandFrequency
    {
        public string Command { get; set; }
        public int Count { get; set; }
        public DateTime LastUsed { get; set; }
        public List<string> UsedHosts { get; set; }
    }
}
