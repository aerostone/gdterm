using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 高亮规则持久化存储
    /// </summary>
    public class HighlightStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();

        public HighlightStore(string filePath)
        {
            _filePath = filePath;
        }

        public HighlightRuleConfig Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    var def = HighlightRuleConfig.GetDefault();
                    Save(def);
                    return def;
                }
                try
                {
                    var json = File.ReadAllText(_filePath, Encoding.UTF8);
                    return ParseConfig(json);
                }
                catch
                {
                    return HighlightRuleConfig.GetDefault();
                }
            }
        }

        public void Save(HighlightRuleConfig config)
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, SerializeConfig(config), Encoding.UTF8);
            }
        }

        // ── 手写 JSON 序列化 ──

        private string SerializeConfig(HighlightRuleConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"rules\": [");
            for (int i = 0; i < config.Rules.Count; i++)
            {
                var r = config.Rules[i];
                sb.AppendLine("    {");
                sb.AppendFormat("      \"id\": \"{0}\",\n", Escape(r.Id));
                sb.AppendFormat("      \"name\": \"{0}\",\n", Escape(r.Name));
                sb.AppendFormat("      \"pattern\": \"{0}\",\n", Escape(r.Pattern));
                sb.AppendFormat("      \"isRegex\": {0},\n", r.IsRegex ? "true" : "false");
                sb.AppendFormat("      \"caseSensitive\": {0},\n", r.CaseSensitive ? "true" : "false");
                sb.AppendFormat("      \"foregroundColor\": \"{0}\",\n", r.ForegroundColor ?? "");
                sb.AppendFormat("      \"backgroundColor\": \"{0}\",\n", r.BackgroundColor ?? "");
                sb.AppendFormat("      \"bold\": {0},\n", r.Bold ? "true" : "false");
                sb.AppendFormat("      \"enabled\": {0},\n", r.Enabled ? "true" : "false");
                sb.AppendFormat("      \"sortOrder\": {0}\n", r.SortOrder);
                sb.AppendFormat("    }}{0}\n", i < config.Rules.Count - 1 ? "," : "");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private HighlightRuleConfig ParseConfig(string json)
        {
            var config = new HighlightRuleConfig();
            var rulesStart = json.IndexOf("\"rules\"");
            if (rulesStart < 0) return config;
            var arrStart = json.IndexOf("[", rulesStart);
            var arrEnd = FindMatchingBracket(json, arrStart);
            if (arrStart < 0 || arrEnd < 0) return config;

            int pos = arrStart + 1;
            while (pos < arrEnd)
            {
                var objStart = json.IndexOf("{", pos);
                if (objStart < 0 || objStart >= arrEnd) break;
                var objEnd = FindMatchingBracket(json, objStart);
                if (objEnd < 0 || objEnd > arrEnd) break;
                var obj = json.Substring(objStart, objEnd - objStart + 1);

                config.Rules.Add(new HighlightRule
                {
                    Id = ExtractString(obj, "id"),
                    Name = ExtractString(obj, "name"),
                    Pattern = ExtractString(obj, "pattern"),
                    IsRegex = obj.Contains("\"isRegex\": true") || obj.Contains("\"isRegex\":true"),
                    CaseSensitive = obj.Contains("\"caseSensitive\": true") || obj.Contains("\"caseSensitive\":true"),
                    ForegroundColor = ExtractString(obj, "foregroundColor"),
                    BackgroundColor = ExtractString(obj, "backgroundColor"),
                    Bold = obj.Contains("\"bold\": true") || obj.Contains("\"bold\":true"),
                    Enabled = obj.Contains("\"enabled\": true") || obj.Contains("\"enabled\":true"),
                    SortOrder = ExtractInt(obj, "sortOrder")
                });
                pos = objEnd + 1;
            }
            return config;
        }

        private static string ExtractString(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search);
            if (idx < 0) return null;
            var colon = json.IndexOf(":", idx + search.Length);
            if (colon < 0) return null;
            var start = json.IndexOf("\"", colon + 1);
            if (start < 0) return null;
            var end = json.IndexOf("\"", start + 1);
            if (end < 0) return null;
            return json.Substring(start + 1, end - start - 1).Replace("\\\"", "\"").Replace("\\n", "\n");
        }

        private static int ExtractInt(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search);
            if (idx < 0) return 0;
            var colon = json.IndexOf(":", idx + search.Length);
            if (colon < 0) return 0;
            var numStart = colon + 1;
            while (numStart < json.Length && (json[numStart] == ' ' || json[numStart] == '\t')) numStart++;
            var numEnd = numStart;
            while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-')) numEnd++;
            int val;
            return int.TryParse(json.Substring(numStart, numEnd - numStart), out val) ? val : 0;
        }

        private static int FindMatchingBracket(string json, int openPos)
        {
            if (openPos < 0) return -1;
            char open = json[openPos];
            char close = open == '{' ? '}' : open == '[' ? ']' : ')';
            int depth = 0;
            bool inString = false;
            for (int i = openPos; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) { inString = !inString; continue; }
                if (inString) continue;
                if (c == open) depth++;
                else if (c == close) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
    }
}
