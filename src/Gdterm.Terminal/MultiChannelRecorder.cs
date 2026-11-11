using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 多通道同步录制器——同步录制多会话输入输出，支持时间线回放
    /// </summary>
    public class MultiChannelRecorder : IDisposable
    {
        private readonly List<RecordingEntry> _entries = new List<RecordingEntry>();
        private readonly Dictionary<string, string> _sessionNames = new Dictionary<string, string>();
        private Stopwatch _timer;
        private bool _isRecording;

        public bool IsRecording { get { return _isRecording; } }
        public int EntryCount { get { return _entries.Count; } }
        public TimeSpan Duration { get; private set; }

        /// <summary>开始录制（标记所有已注册会话）</summary>
        public void StartRecording()
        {
            _entries.Clear();
            _timer = Stopwatch.StartNew();
            _isRecording = true;
        }

        /// <summary>注册会话名称</summary>
        public void RegisterSession(string sessionId, string displayName)
        {
            _sessionNames[sessionId] = displayName;
        }

        /// <summary>记录输入事件</summary>
        public void RecordInput(string sessionId, string input)
        {
            if (!_isRecording) return;
            _entries.Add(new RecordingEntry
            {
                TimestampMs = _timer.ElapsedMilliseconds,
                SessionId = sessionId,
                SessionName = GetSessionName(sessionId),
                Direction = EntryDirection.Input,
                Content = input
            });
        }

        /// <summary>记录输出事件</summary>
        public void RecordOutput(string sessionId, string output)
        {
            if (!_isRecording) return;
            _entries.Add(new RecordingEntry
            {
                TimestampMs = _timer.ElapsedMilliseconds,
                SessionId = sessionId,
                SessionName = GetSessionName(sessionId),
                Direction = EntryDirection.Output,
                Content = output
            });
        }

        /// <summary>停止录制</summary>
        public void StopRecording()
        {
            if (!_isRecording) return;
            _timer.Stop();
            Duration = _timer.Elapsed;
            _isRecording = false;
        }

        /// <summary>时间线回放——按时间戳将事件回调给对应会话</summary>
        public async Task ReplayAsync(Dictionary<string, ITerminalSession> sessions, double speed = 1.0,
            Action<ReplayEvent> onEvent = null, CancellationToken ct = default)
        {
            if (_entries.Count == 0) return;

            long lastTimestamp = 0;
            foreach (var entry in _entries)
            {
                ct.ThrowIfCancellationRequested();

                var delay = entry.TimestampMs - lastTimestamp;
                if (delay > 0)
                {
                    var actualDelay = (int)(delay / speed);
                    if (actualDelay > 0) await Task.Delay(actualDelay, ct);
                }
                lastTimestamp = entry.TimestampMs;

                if (entry.Direction == EntryDirection.Input && sessions.ContainsKey(entry.SessionId))
                {
                    sessions[entry.SessionId].SendInput(entry.Content);
                }

                onEvent?.Invoke(new ReplayEvent
                {
                    Entry = entry,
                    Progress = (double)_entries.IndexOf(entry) / _entries.Count
                });
            }
        }

        /// <summary>导出为纯文本报告</summary>
        public string ExportAsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L10n.T("gdterm 多通道录制报告"));
            sb.AppendLine("====================");
            sb.AppendLine(string.Format(L10n.T("时长: {0}"), Duration));
            sb.AppendLine(string.Format(L10n.T("条目: {0}"), _entries.Count));
            sb.AppendLine(L10n.T("会话: ") + _sessionNames.Count);
            sb.AppendLine();

            foreach (var entry in _entries)
            {
                sb.AppendLine(string.Format("[{0:00}:{1:00}.{2:000}] {3} {4}: {5}",
                    entry.TimestampMs / 60000,
                    (entry.TimestampMs / 1000) % 60,
                    entry.TimestampMs % 1000,
                    entry.Direction == EntryDirection.Input ? "←" : "→",
                    entry.SessionName,
                    entry.Content));
            }
            return sb.ToString();
        }

        /// <summary>导出为HTML报告</summary>
        public string ExportAsHtml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine(L10n.T("<title>gdterm 录制报告</title>"));
            sb.AppendLine("<style>body{font-family:monospace;background:#1e1e1e;color:#d4d4d4;padding:20px}");
            sb.AppendLine(".entry{margin:2px 0;padding:4px 8px;border-radius:3px}");
            sb.AppendLine(".input{background:#264f78}.output{background:#1e3a1e}");
            sb.AppendLine(".time{color:#888;margin-right:10px}.session{color:#569cd6;margin-right:10px}</style></head><body>");
            sb.AppendLine(L10n.T("<h2>gdterm 多通道录制报告</h2>"));
            sb.AppendLine(string.Format(L10n.T("<p>时长: {0} | 条目: {1} | 会话: {2}</p>"), Duration, _entries.Count, _sessionNames.Count));
            sb.AppendLine("<pre>");

            foreach (var entry in _entries)
            {
                var cssClass = entry.Direction == EntryDirection.Input ? "input" : "output";
                var arrow = entry.Direction == EntryDirection.Input ? "←" : "→";
                sb.AppendLine(string.Format("<div class='entry {0}'><span class='time'>[{1:00}:{2:00}.{3:000}]</span><span class='session'>{4} {5}</span>{6}</div>",
                    cssClass,
                    entry.TimestampMs / 60000,
                    (entry.TimestampMs / 1000) % 60,
                    entry.TimestampMs % 1000,
                    arrow,
                    entry.SessionName,
                    System.Net.WebUtility.HtmlEncode(entry.Content)));
            }

            sb.AppendLine("</pre></body></html>");
            return sb.ToString();
        }

        /// <summary>保存录制文件</summary>
        public void SaveToFile(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("{\"version\":1,\"duration\":").Append((long)Duration.TotalMilliseconds);
            sb.Append(",\"sessions\":{");
            bool first = true;
            foreach (var kvp in _sessionNames)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Esc(kvp.Key)).Append("\":\"").Append(Esc(kvp.Value)).Append('"');
                first = false;
            }
            sb.Append("},\"entries\":[");
            for (int i = 0; i < _entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = _entries[i];
                sb.Append("{\"t\":").Append(e.TimestampMs);
                sb.Append(",\"s\":\"").Append(Esc(e.SessionId)).Append('"');
                sb.Append(",\"d\":").Append((int)e.Direction);
                sb.Append(",\"c\":\"").Append(Esc(e.Content)).Append("\"}");
            }
            sb.Append("]}");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>从文件加载录制</summary>
        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(L10n.T("录制文件不存在"), filePath);

            _entries.Clear();
            _sessionNames.Clear();
            var json = File.ReadAllText(filePath, Encoding.UTF8);

            // 解析 sessions 对象
            var sessionsIdx = json.IndexOf("\"sessions\"");
            if (sessionsIdx >= 0)
            {
                var objStart = json.IndexOf('{', sessionsIdx);
                var objEnd = json.IndexOf('}', objStart);
                if (objStart >= 0 && objEnd >= 0)
                {
                    var content = json.Substring(objStart + 1, objEnd - objStart - 1);
                    // 简单解析 key:value
                    int i = 0;
                    while (i < content.Length)
                    {
                        if (content[i] == '"')
                        {
                            var keyEnd = content.IndexOf('"', i + 1);
                            var colon = content.IndexOf(':', keyEnd + 1);
                            var valStart = content.IndexOf('"', colon + 1);
                            var valEnd = content.IndexOf('"', valStart + 1);
                            if (keyEnd > 0 && valEnd > 0)
                            {
                                _sessionNames[content.Substring(i + 1, keyEnd - i - 1)] =
                                    content.Substring(valStart + 1, valEnd - valStart - 1);
                                i = valEnd + 1;
                            }
                            else break;
                        }
                        else i++;
                    }
                }
            }

            // 解析 entries 数组
            var entriesIdx = json.IndexOf("\"entries\"");
            if (entriesIdx >= 0)
            {
                var arrStart = json.IndexOf('[', entriesIdx);
                var arrEnd = json.IndexOf(']', arrStart);
                if (arrStart >= 0 && arrEnd >= 0)
                {
                    var content = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                    var objects = SplitJsonObjects(content);
                    foreach (var obj in objects)
                    {
                        _entries.Add(new RecordingEntry
                        {
                            TimestampMs = ExtractLong(obj, "t"),
                            SessionId = ExtractString(obj, "s") ?? "",
                            Direction = (EntryDirection)ExtractInt(obj, "d"),
                            Content = ExtractString(obj, "c") ?? ""
                        });
                    }
                }
            }
        }

        private string GetSessionName(string sessionId)
        {
            return _sessionNames.ContainsKey(sessionId) ? _sessionNames[sessionId] : sessionId;
        }

        private static string Esc(string s) { return Gdterm.Core.JsonText.Escape(s); } // 审计 F1：统一转义契约

        private static string ExtractString(string json, string key)
        {
            return Gdterm.Core.JsonText.ExtractString(json, key); // 审计 F1：统一反转义
        }

        private static long ExtractLong(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return 0;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return 0;
            var s = c + 1; while (s < json.Length && json[s] == ' ') s++; var e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            long v; long.TryParse(json.Substring(s, e - s), out v); return v;
        }

        private static int ExtractInt(string json, string key) { return (int)ExtractLong(json, key); }

        private static List<string> SplitJsonObjects(string content)
        {
            // 审计 F1：统一走 JsonText（旧实现自数括号，内容里的括号会切坏对象）
            return Gdterm.Core.JsonText.SplitObjects(content);
        }

        public void Dispose()
        {
            _timer?.Stop();
            _entries.Clear();
            _sessionNames.Clear();
        }
    }

    /// <summary>录制条目</summary>
    public class RecordingEntry
    {
        public long TimestampMs { get; set; }
        public string SessionId { get; set; }
        public string SessionName { get; set; }
        public EntryDirection Direction { get; set; }
        public string Content { get; set; }
    }

    public enum EntryDirection
    {
        Input = 0,
        Output = 1
    }

    /// <summary>回放事件</summary>
    public class ReplayEvent
    {
        public RecordingEntry Entry { get; set; }
        public double Progress { get; set; }
    }
}
