using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 登录脚本持久化存储
    /// </summary>
    public class LogonScriptStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();

        public LogonScriptStore(string filePath) { _filePath = filePath; }

        public List<LogonScript> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath)) return new List<LogonScript>();
                try { return ParseScripts(File.ReadAllText(_filePath, Encoding.UTF8)); }
                catch { return new List<LogonScript>(); }
            }
        }

        public void Save(List<LogonScript> scripts)
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, Serialize(scripts), Encoding.UTF8);
            }
        }

        public void Add(List<LogonScript> scripts, LogonScript script)
        {
            scripts.Add(script);
            Save(scripts);
        }

        public bool Remove(List<LogonScript> scripts, string id)
        {
            var removed = scripts.RemoveAll(s => s.Id == id);
            if (removed > 0) Save(scripts);
            return removed > 0;
        }

        // ── 序列化 ──
        private string Serialize(List<LogonScript> scripts)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{ \"scripts\": [");
            for (int i = 0; i < scripts.Count; i++)
            {
                var s = scripts[i];
                sb.AppendLine("  {");
                sb.AppendFormat("    \"id\": \"{0}\",\n", Esc(s.Id));
                sb.AppendFormat("    \"name\": \"{0}\",\n", Esc(s.Name));
                sb.AppendFormat("    \"description\": \"{0}\",\n", Esc(s.Description));
                sb.AppendFormat("    \"enabled\": {0},\n", s.Enabled ? "true" : "false");
                sb.AppendFormat("    \"connectionId\": \"{0}\",\n", Esc(s.AssociatedConnectionId));
                sb.AppendLine("    \"steps\": [");
                for (int j = 0; j < s.Steps.Count; j++)
                {
                    var st = s.Steps[j];
                    sb.AppendLine("      {");
                    sb.AppendFormat("        \"type\": \"{0}\",\n", st.Type);
                    sb.AppendFormat("        \"value\": \"{0}\",\n", Esc(st.Value));
                    sb.AppendFormat("        \"timeout\": {0},\n", st.TimeoutMs);
                    sb.AppendFormat("        \"description\": \"{0}\"\n", Esc(st.Description));
                    sb.AppendFormat("      }}{0}\n", j < s.Steps.Count - 1 ? "," : "");
                }
                sb.AppendFormat("    ]\n  }}{0}\n", i < scripts.Count - 1 ? "," : "");
            }
            sb.AppendLine("] }");
            return sb.ToString();
        }

        private List<LogonScript> ParseScripts(string json)
        {
            var scripts = new List<LogonScript>();
            var arrIdx = json.IndexOf("\"scripts\"");
            if (arrIdx < 0) return scripts;
            var arrStart = json.IndexOf("[", arrIdx);
            var arrEnd = FindBracket(json, arrStart, '[', ']');
            if (arrStart < 0 || arrEnd < 0) return scripts;

            int pos = arrStart + 1;
            while (pos < arrEnd)
            {
                var objStart = json.IndexOf("{", pos);
                if (objStart < 0 || objStart >= arrEnd) break;
                // Skip the nested "steps" array — find the outer closing brace
                var objEnd = FindScriptEnd(json, objStart, arrEnd);
                if (objEnd < 0) break;
                var obj = json.Substring(objStart, objEnd - objStart + 1);

                var script = new LogonScript
                {
                    Id = ExStr(obj, "id"),
                    Name = ExStr(obj, "name"),
                    Description = ExStr(obj, "description"),
                    Enabled = obj.Contains("\"enabled\": true") || obj.Contains("\"enabled\":true"),
                    AssociatedConnectionId = ExStr(obj, "connectionId")
                };

                // Parse steps array
                var stepsIdx = obj.IndexOf("\"steps\"");
                if (stepsIdx >= 0)
                {
                    var stepsStart = obj.IndexOf("[", stepsIdx);
                    var stepsEnd = FindBracket(obj, stepsStart, '[', ']');
                    if (stepsStart >= 0 && stepsEnd >= 0)
                    {
                        int sp = stepsStart + 1;
                        while (sp < stepsEnd)
                        {
                            var soStart = obj.IndexOf("{", sp);
                            if (soStart < 0 || soStart >= stepsEnd) break;
                            var soEnd = FindBracket(obj, soStart, '{', '}');
                            if (soEnd < 0) break;
                            var stepObj = obj.Substring(soStart, soEnd - soStart + 1);

                            var typeStr = ExStr(stepObj, "type");
                            LogonStepType stepType;
                            Enum.TryParse(typeStr, true, out stepType);

                            script.Steps.Add(new LogonStep
                            {
                                Type = stepType,
                                Value = ExStr(stepObj, "value"),
                                TimeoutMs = ExInt(stepObj, "timeout"),
                                Description = ExStr(stepObj, "description")
                            });
                            sp = soEnd + 1;
                        }
                    }
                }

                scripts.Add(script);
                pos = objEnd + 1;
            }
            return scripts;
        }

        private static int FindScriptEnd(string json, int start, int limit)
        {
            // 审计 F1 去重：括号配对只留 JsonText 一份实现（limit 即数组终点，配对一定先于它返回）
            return Gdterm.Core.JsonText.FindMatchingBracket(json, start);
        }

        private static int FindBracket(string json, int open, char openChar, char closeChar)
        {
            // 审计 F1 去重：括号配对只留 JsonText 一份实现
            return Gdterm.Core.JsonText.FindMatchingBracket(json, open);
        }

        private static string ExStr(string json, string key)
        {
            // 审计 F1：结束引号必须跳过转义（原实现遇到 \" 就截断），反转义统一走 JsonText
            return Gdterm.Core.JsonText.ExtractString(json, key);
        }

        private static int ExInt(string json, string key)
        {
            var s = "\"" + key + "\"";
            var idx = json.IndexOf(s);
            if (idx < 0) return 0;
            var colon = json.IndexOf(":", idx + s.Length);
            if (colon < 0) return 0;
            int ns = colon + 1;
            while (ns < json.Length && json[ns] == ' ') ns++;
            int ne = ns;
            while (ne < json.Length && (char.IsDigit(json[ne]) || json[ne] == '-')) ne++;
            int v;
            return int.TryParse(json.Substring(ns, ne - ns), out v) ? v : 0;
        }

        private static string Esc(string s) { return Gdterm.Core.JsonText.Escape(s); }
    }
}
