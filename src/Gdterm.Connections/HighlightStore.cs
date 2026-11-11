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

        /// <summary>双引号字面量（手写 JSON 用，避免格式串里堆反斜杠）。</summary>
        private static readonly string DQ = ((char)34).ToString();

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
                sb.AppendFormat("      \"sortOrder\": {0},\n", r.SortOrder);
                // P1-7 触发器：动作 / 参数 / 冷却（0=只高亮，老配置读出来就是 None）
                sb.AppendFormat("      \"action\": {0},\n", (int)r.Action);
                sb.Append("      ").Append(DQ).Append("actionArgument").Append(DQ).Append(": ").Append(DQ).Append(Escape(r.ActionArgument)).Append(DQ).Append(",").Append(Environment.NewLine);
                sb.AppendFormat("      \"actionCooldownSeconds\": {0}\n", r.ActionCooldownSeconds);
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
                    SortOrder = ExtractInt(obj, "sortOrder"),
                    Action = (HighlightAction)ExtractInt(obj, "action"),
                    ActionArgument = ExtractString(obj, "actionArgument"),
                    // 老文件没有这个键：缺省按 5 秒冷却，否则 cat 大文件会把同一动作刷满
                    ActionCooldownSeconds = obj.Contains("\"actionCooldownSeconds\"")
                        ? ExtractInt(obj, "actionCooldownSeconds")
                        : 5
                });
                pos = objEnd + 1;
            }
            return config;
        }

        private static string ExtractString(string json, string key)
        {
            // 审计 F1：转义/反转义必须成对。统一走 Gdterm.Core.JsonText，
            // 否则 "pattern" 里的反斜杠读回时不被还原，正则被逐次加倍转义后静默失效。
            return Gdterm.Core.JsonText.ExtractString(json, key);
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
            // 审计 F1：字符串里的 ] / } 不参与配对；统一走 JsonText
            return Gdterm.Core.JsonText.FindMatchingBracket(json, openPos);
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return Gdterm.Core.JsonText.Escape(s);
        }
    }
}
