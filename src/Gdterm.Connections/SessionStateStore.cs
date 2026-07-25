using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 会话状态持久化存储 —— JSON 文件读写
    /// </summary>
    public class SessionStateStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();

        public SessionStateStore(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>
        /// 加载上次保存的会话状态（不存在返回 null）
        /// </summary>
        public SessionState Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                    return null;

                try
                {
                    var json = File.ReadAllText(_filePath, Encoding.UTF8);
                    return ParseSessionState(json);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 保存当前会话状态
        /// </summary>
        public void Save(SessionState state)
        {
            if (state == null) return;

            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                state.SavedAt = DateTime.UtcNow;
                var json = SerializeSessionState(state);
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
        }

        /// <summary>
        /// 删除保存的会话状态
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
        }

        // ===== 手写 JSON 序列化 =====

        private string SerializeSessionState(SessionState s)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"windowX\": {s.WindowX},\n");
            sb.Append($"  \"windowY\": {s.WindowY},\n");
            sb.Append($"  \"windowWidth\": {s.WindowWidth},\n");
            sb.Append($"  \"windowHeight\": {s.WindowHeight},\n");
            sb.Append($"  \"windowState\": {Escape(s.WindowState)},\n");
            sb.Append($"  \"viewMode\": {Escape(s.ViewMode)},\n");
            sb.Append($"  \"connectionPanelWidth\": {s.ConnectionPanelWidth},\n");
            sb.Append($"  \"activeTabIndex\": {s.ActiveTabIndex},\n");
            sb.Append($"  \"savedAt\": {Escape(s.SavedAt.ToString("o"))},\n");
            sb.Append("  \"openTabs\": [");

            for (int i = 0; i < s.OpenTabs.Count; i++)
            {
                var tab = s.OpenTabs[i];
                if (i > 0) sb.Append(",");
                sb.Append("\n    {");
                sb.Append($" \"connectionId\": {Escape(tab.ConnectionId)},");
                sb.Append($" \"title\": {Escape(tab.Title)},");
                sb.Append($" \"protocol\": {Escape(tab.Protocol)},");
                sb.Append($" \"host\": {Escape(tab.Host)},");
                sb.Append($" \"isActive\": {(tab.IsActive ? "true" : "false")} ");
                sb.Append("}");
            }

            if (s.OpenTabs.Count > 0) sb.Append("\n  ");
            sb.Append("]\n}");
            return sb.ToString();
        }

        private SessionState ParseSessionState(string json)
        {
            var s = new SessionState();
            s.WindowX = ExtractInt(json, "windowX");
            s.WindowY = ExtractInt(json, "windowY");
            s.WindowWidth = ExtractInt(json, "windowWidth") > 0 ? ExtractInt(json, "windowWidth") : 1200;
            s.WindowHeight = ExtractInt(json, "windowHeight") > 0 ? ExtractInt(json, "windowHeight") : 800;
            s.WindowState = ExtractString(json, "windowState") ?? "Normal";
            s.ViewMode = ExtractString(json, "viewMode") ?? "Standard";
            s.ConnectionPanelWidth = ExtractInt(json, "connectionPanelWidth");
            if (s.ConnectionPanelWidth <= 0) s.ConnectionPanelWidth = 250;
            s.ActiveTabIndex = ExtractInt(json, "activeTabIndex");

            var dateStr = ExtractString(json, "savedAt");
            if (!string.IsNullOrEmpty(dateStr))
                DateTime.TryParse(dateStr, out var dt);

            // 解析 openTabs 数组
            var tabsStart = json.IndexOf("\"openTabs\"");
            if (tabsStart >= 0)
            {
                var arrStart = json.IndexOf('[', tabsStart);
                var arrEnd = json.IndexOf(']', arrStart);
                if (arrStart >= 0 && arrEnd > arrStart)
                {
                    var arr = json.Substring(arrStart, arrEnd - arrStart + 1);
                    int pos = 0;
                    while (pos < arr.Length)
                    {
                        int objStart = arr.IndexOf('{', pos);
                        if (objStart < 0) break;
                        int objEnd = FindMatchingBrace(arr, objStart);
                        if (objEnd < 0) break;

                        var obj = arr.Substring(objStart, objEnd - objStart + 1);
                        s.OpenTabs.Add(new OpenTabState
                        {
                            ConnectionId = ExtractString(obj, "connectionId"),
                            Title = ExtractString(obj, "title"),
                            Protocol = ExtractString(obj, "protocol"),
                            Host = ExtractString(obj, "host"),
                            IsActive = ExtractBool(obj, "isActive")
                        });
                        pos = objEnd + 1;
                    }
                }
            }

            return s;
        }

        private static string ExtractString(string json, string key)
        {
            var search = $"\"{key}\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++;
            int end = json.IndexOf('"', idx);
            if (end < 0) return null;
            return json.Substring(idx, end - idx);
        }

        private static int ExtractInt(string json, string key)
        {
            var search = $"\"{key}\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return 0;
            idx += search.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            var start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '-')) idx++;
            if (start == idx) return 0;
            int.TryParse(json.Substring(start, idx - start), out var val);
            return val;
        }

        private static bool ExtractBool(string json, string key)
        {
            var search = $"\"{key}\":";
            int idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return false;
            idx += search.Length;
            while (idx < json.Length && json[idx] == ' ') idx++;
            return json.Substring(idx).StartsWith("true");
        }

        private static int FindMatchingBrace(string json, int start)
        {
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static string Escape(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        }
    }
}
