using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using Gdterm.Core.Models;

using Gdterm.Core;
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
                                   L10n.Tf("\n... (共 {0} 行，已截断)", lines.Length);
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
                    catch (Exception ex)
                    {
                        LastDeserializationError = "ReadFile: " + ex.GetType().Name + ": " + ex.Message;
                        System.Diagnostics.Debug.WriteLine("CommandHistoryStore.Query: " + LastDeserializationError);
                    }
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

        /// <summary>
    /// 按 Id 删除指定条目（面板多选批量删除）。返回实际删除的条数。
    /// JSON Lines 无随机删——逐文件重写，跳过无命中的文件。
    /// </summary>
    public int DeleteByIds(System.Collections.Generic.IEnumerable<string> ids)
    {
        if (ids == null) return 0;
        var idSet = new HashSet<string>(ids.Where(x => !string.IsNullOrEmpty(x)));
        if (idSet.Count == 0) return 0;
        int removed = 0;

        lock (_lock)
        {
            try { _writer?.Flush(); } catch (Exception) { }
            foreach (var file in GetLogFilesSorted())
            {
                try
                {
                    var lines = File.ReadAllLines(file, Encoding.UTF8);
                    var keep = new List<string>(lines.Length);
                    int hit = 0;
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = DeserializeEntry(line);
                        if (entry != null && idSet.Contains(entry.Id)) { hit++; continue; }
                        keep.Add(line);
                    }
                    if (hit == 0) continue;
                    File.WriteAllLines(file, keep, Encoding.UTF8);
                    removed += hit;
                }
                catch { }
            }
        }
        return removed;
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

        /// <summary>
        /// 清空全部历史（命令历史面板的「清空」按钮）。返回被删除的文件数：
        /// 之前那个按钮只弹确认框、然后 RefreshHistory()，磁盘上一条没删——用户以为清干净了。
        /// </summary>
        public int ClearAll()
        {
            int removed = 0;
            lock (_lock)
            {
                try
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch (Exception) { }
                _writer = null;

                foreach (var f in GetLogFilesSorted())
                {
                    try { File.Delete(f); removed++; }
                    catch (Exception ex) { try { System.Diagnostics.Debug.WriteLine("ClearAll: " + ex.Message); } catch (Exception) { } }
                }
                // 重新开一个空文件，后续 RecordCommand 继续可用
                InitializeCurrentFile();
            }
            return removed;
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

        // ===== JSON 序列化（手写，2026-11-08 替换 JavaScriptSerializer）=====
        // 背景：JSon 往返在 CI 上 Record 后 Query 恒空（0.1.452 实证）且失败被静默吞掉，
        // 本地无 .NET Framework 运行时无法复现。本代码库 store 的标准做法就是手写 JSON
        // （ConnectionStoreJson / ChatHistoryStore 同模式），JSon 是异类——移除之。
        // 读取器是完整的小型 JSON 解析器，同时兼容旧 JSon 文件格式（其输出是标准 JSON，
        // 唯一特例是 DateTime 写成毫秒裸数字——MiniGetDateTime 两路都认）。

        private static string SerializeEntry(CommandHistoryEntry e)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            AppendJsonString(sb, "Id", e.Id);
            sb.Append(',');
            AppendJsonString(sb, "ConnectionId", e.ConnectionId);
            sb.Append(',');
            AppendJsonString(sb, "Hostname", e.Hostname);
            sb.Append(',');
            AppendJsonString(sb, "Protocol", e.Protocol);
            sb.Append(',');
            AppendJsonString(sb, "Command", e.Command);
            sb.Append(',');
            AppendJsonString(sb, "Output", e.Output);
            sb.Append(",\"ExitCode\":");
            if (e.ExitCode.HasValue) sb.Append(e.ExitCode.Value.ToString(CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(",\"ExecutedAt\":\"");
            sb.Append(e.ExecutedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            sb.Append("\",\"DurationMs\":");
            sb.Append(e.DurationMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"IsBroadcast\":");
            sb.Append(e.IsBroadcast ? "true" : "false");
            sb.Append(",\"BroadcastTargets\":");
            if (e.BroadcastTargets == null) sb.Append("null");
            else
            {
                sb.Append('[');
                for (int i = 0; i < e.BroadcastTargets.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendJsonString(sb, null, e.BroadcastTargets[i]);
                }
                sb.Append(']');
            }
            sb.Append(',');
            AppendJsonString(sb, "Tags", e.Tags);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string key, string value)
        {
            if (key != null)
            {
                // 逗号由调用方管理；key==null 表示数组元素（只写值）
                sb.Append('"').Append(key).Append("\":");
            }
            if (value == null) sb.Append("null");
            else
            {
                sb.Append('"');
                AppendJsonEscaped(sb, value);
                sb.Append('"');
            }
        }

        private static void AppendJsonEscaped(StringBuilder sb, string v)
        {
            foreach (var ch in v)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
        }

        /// <summary>最近一次反序列化失败（null=正常）。静默吞异常曾让数据损坏隐身——
        /// 0.1.452 CI 实证：Record 后 Query 恒空且无任何日志，定位靠它。</summary>
        public static string LastDeserializationError { get; private set; }

        private static CommandHistoryEntry DeserializeEntry(string json)
        {
            try
            {
                int pos = 0;
                var obj = MiniParseObject(json, ref pos);
                if (obj == null) return null;

                var e = new CommandHistoryEntry
                {
                    Id = MiniGetString(obj, "Id"),
                    ConnectionId = MiniGetString(obj, "ConnectionId"),
                    Hostname = MiniGetString(obj, "Hostname"),
                    Protocol = MiniGetString(obj, "Protocol"),
                    Command = MiniGetString(obj, "Command"),
                    Output = MiniGetString(obj, "Output"),
                    ExitCode = MiniGetInt(obj, "ExitCode"),
                    ExecutedAt = MiniGetDateTime(obj, "ExecutedAt"),
                    DurationMs = MiniGetLong(obj, "DurationMs"),
                    IsBroadcast = MiniGetBool(obj, "IsBroadcast"),
                    BroadcastTargets = MiniGetList(obj, "BroadcastTargets"),
                    Tags = MiniGetString(obj, "Tags")
                };
                if (string.IsNullOrEmpty(e.Id))
                    e.Id = Guid.NewGuid().ToString("N"); // 与 RecordCommand 的兜底一致
                return e;
            }
            catch (Exception ex)
            {
                LastDeserializationError = ex.GetType().Name + ": " + ex.Message;
                System.Diagnostics.Debug.WriteLine("CommandHistoryStore.Deserialize: " + LastDeserializationError);
                return null;
            }
        }

        // ===== 迷你 JSON 解析（值：object/array/string/number/true/false/null）=====
        // 够用即可：历史行主要是我们自己写的；对旧 JSon 文件保读取兼容。

        private static Dictionary<string, object> MiniParseObject(string s, ref int pos)
        {
            MiniSkipWs(s, ref pos);
            if (pos >= s.Length || s[pos] != '{') return null;
            pos++;
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            MiniSkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (pos < s.Length)
            {
                MiniSkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') return null;
                var key = MiniParseString(s, ref pos);
                MiniSkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') return null;
                pos++;
                var val = MiniParseValue(s, ref pos);
                dict[key] = val;
                MiniSkipWs(s, ref pos);
                if (pos >= s.Length) return null;
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return dict; }
                return null;
            }
            return null;
        }

        private static object MiniParseValue(string s, ref int pos)
        {
            MiniSkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            var ch = s[pos];
            if (ch == '{')
            {
                return MiniParseObject(s, ref pos);
            }
            if (ch == '[')
            {
                pos++;
                var list = new List<object>();
                MiniSkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return list; }
                while (pos < s.Length)
                {
                    list.Add(MiniParseValue(s, ref pos));
                    MiniSkipWs(s, ref pos);
                    if (pos >= s.Length) break;
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == ']') { pos++; break; }
                    break;
                }
                return list;
            }
            if (ch == '"') return MiniParseString(s, ref pos);
            if (ch == 't' && s.Substring(pos, Math.Min(4, s.Length - pos)) == "true") { pos += 4; return true; }
            if (ch == 'f' && s.Substring(pos, Math.Min(5, s.Length - pos)) == "false") { pos += 5; return false; }
            if (ch == 'n' && s.Substring(pos, Math.Min(4, s.Length - pos)) == "null") { pos += 4; return null; }
            // 数字（含旧 JSon 的 DateTime 毫秒裸数字与小数/指数形式）
            int start = pos;
            if (ch == '-' || ch == '+') pos++;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || s[pos] == '+' || s[pos] == '-')) pos++;
            if (pos == start) return null;
            var num = s.Substring(start, pos - start);
            double dv;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dv))
                return dv;
            return null;
        }

        private static string MiniParseString(string s, ref int pos)
        {
            // 进入时 s[pos] 应为 '"'
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                var ch = s[pos];
                if (ch == '\\' && pos + 1 < s.Length)
                {
                    var next = s[pos + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 5 < s.Length)
                            {
                                int code;
                                if (int.TryParse(s.Substring(pos + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    pos += 6;
                                    continue;
                                }
                            }
                            sb.Append(next);
                            pos += 2;
                            break;
                        default:
                            sb.Append(next);
                            pos += 2;
                            break;
                    }
                    pos += 2;
                    continue;
                }
                if (ch == '"') { pos++; break; }
                sb.Append(ch);
                pos++;
            }
            return sb.ToString();
        }

        private static void MiniSkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        private static string MiniGetString(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj.TryGetValue(key, out v) && v is string ? (string)v : null;
        }

        private static long MiniGetLong(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj.TryGetValue(key, out v) && v is double) return (long)(double)v;
            return 0;
        }

        private static int? MiniGetInt(Dictionary<string, object> obj, string key)
        {
            object v;
            if (obj.TryGetValue(key, out v) && v is double) return (int)(double)v;
            return null;
        }

        private static bool MiniGetBool(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        private static DateTime MiniGetDateTime(Dictionary<string, object> obj, string key)
        {
            object v;
            if (!obj.TryGetValue(key, out v)) return DateTime.UtcNow;
            // 新格式：ISO 8601 字符串
            if (v is string)
            {
                DateTime dt;
                if (DateTime.TryParse((string)v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
                    return dt.ToUniversalTime();
                return DateTime.UtcNow;
            }
            // 旧 JSon 格式：毫秒裸数字
            if (v is double)
            {
                try
                {
                    return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((double)v);
                }
                catch { return DateTime.UtcNow; }
            }
            return DateTime.UtcNow;
        }

        private static List<string> MiniGetList(Dictionary<string, object> obj, string key)
        {
            object v;
            if (!obj.TryGetValue(key, out v) || !(v is List<object>)) return null;
            var list = (List<object>)v;
            var result = new List<string>();
            foreach (var item in list)
            {
                var str = item as string;
                if (str != null) result.Add(str);
            }
            return result;
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
