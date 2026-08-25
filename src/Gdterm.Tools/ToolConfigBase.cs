using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdterm.Tools
{
    /// <summary>
    /// 工具配置基类——所有工具配置继承此类，自动支持 JSON 文件读写
    /// </summary>
    public abstract class ToolConfigBase
    {
        /// <summary>配置文件路径</summary>
        protected string ConfigFilePath { get; private set; }

        /// <summary>设置配置文件路径（在 LoadFromFile 前调用）</summary>
        public void SetConfigPath(string filePath)
        {
            ConfigFilePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        }

        /// <summary>从文件加载配置</summary>
        public virtual void LoadFromFile()
        {
            if (string.IsNullOrEmpty(ConfigFilePath))
            {
                ResetDefaults();
                return;
            }
            if (!File.Exists(ConfigFilePath)) { ResetDefaults(); SaveToFile(); return; }

            var json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
            LoadFromJson(json);
        }

        /// <summary>保存配置到文件</summary>
        public virtual void SaveToFile()
        {
            if (string.IsNullOrEmpty(ConfigFilePath)) return;
            var json = ToJson();
            File.WriteAllText(ConfigFilePath, json, Encoding.UTF8);
        }

        /// <summary>重置为默认值</summary>
        public abstract void ResetDefaults();

        /// <summary>从 JSON 字符串加载（子类实现）</summary>
        protected abstract void LoadFromJson(string json);

        /// <summary>序列化为 JSON 字符串（子类实现）</summary>
        protected abstract string ToJson();

        // ====== JSON 辅助方法 ======

        protected static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";

        protected static string ExtractString(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return null;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return null;
            var s = json.IndexOf('"', c + 1); if (s < 0) return null;
            var e = s + 1; while (e < json.Length) { if (json[e] == '\\') { e += 2; continue; } if (json[e] == '"') break; e++; }
            return json.Substring(s + 1, e - s - 1).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        protected static int ExtractInt(string json, string key, int defaultValue = 0)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return defaultValue;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return defaultValue;
            var s = c + 1; while (s < json.Length && json[s] == ' ') s++; var e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            int.TryParse(json.Substring(s, e - s), out var v); return v;
        }

        protected static bool ExtractBool(string json, string key, bool defaultValue = false)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return defaultValue;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return defaultValue;
            // finding-10：从值起点（跳过空白）开始比较字面量——旧实现 IndexOf 从字符串头搜索，
            // JSON 更早位置出现过 true/false 字面量时后续布尔键会误判回默认值。
            var s = c + 1; while (s < json.Length && json[s] == ' ') s++;
            if (string.Compare(json, s, "true", 0, 4, StringComparison.Ordinal) == 0) return true;
            if (string.Compare(json, s, "false", 0, 5, StringComparison.Ordinal) == 0) return false;
            return defaultValue;
        }

        protected static List<string> ExtractStringList(string json, string key)
        {
            var result = new List<string>();
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return result;
            var arrStart = json.IndexOf('[', idx); if (arrStart < 0) return result;
            var arrEnd = json.IndexOf(']', arrStart); if (arrEnd < 0) return result;
            var arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            int i = 0;
            while (i < arrContent.Length)
            {
                if (arrContent[i] == '"')
                {
                    var end = arrContent.IndexOf('"', i + 1);
                    if (end < 0) break;
                    result.Add(arrContent.Substring(i + 1, end - i - 1));
                    i = end + 1;
                }
                else i++;
            }
            return result;
        }
    }
}
