using System;
using System.IO;
using System.Text;

namespace Gdterm.AI
{
    /// <summary>AI 设置导入/导出（F11）——单对象 JSON，手写序列化/解析（代码库 store 标准模式，
    /// 与 AiModelStore 同风格；不引 JavaScriptSerializer）。
    ///
    /// 安全策略：导出时由调用方用 YesNo 问用户是否包含 API Key——
    ///   includeKey=false → ApiKey 写空串（掩码导出），导入端保留本机现有 Key；
    ///   includeKey=true  → 明文写入（文件本身即敏感，确认框已警示）。
    /// 不落库的本地簿记字段（Id/LastUsedAt/TotalTokensUsed）不导出——导入即新建配置。</summary>
    public static class AiSettingsFile
    {
        /// <summary>导出结果（供 UI 提示）。</summary>
        public static void ExportTo(
            string path,
            string name, string endpoint, string model,
            int maxTokens, double temperature,
            string apiKey, bool includeKey)
        {
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("endpoint/model 不能为空（未配置完整前不可导出）", "endpoint");

            var sb = new StringBuilder();
            sb.Append("{");
            AppendString(sb, "Name", name);
            sb.Append(',');
            AppendString(sb, "Endpoint", endpoint);
            sb.Append(',');
            AppendString(sb, "ApiKey", includeKey ? (apiKey ?? "") : "");
            sb.Append(',');
            AppendString(sb, "Model", model);
            sb.AppendFormat(",\"MaxTokens\":{0}", maxTokens);
            sb.AppendFormat(",\"Temperature\":{0}", temperature.ToString("F1"));
            sb.Append("}");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>导入结果。KeyPresent=false 表示文件里 Key 为空（调用方应保留本机现有 Key）。</summary>
        public class ImportResult
        {
            public string Name = "";
            public string Endpoint = "";
            public string ApiKey = "";
            public string Model = "";
            public int? MaxTokens;
            public double? Temperature;
            public bool KeyPresent;
        }

        public static ImportResult ImportFrom(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在", path);
            var json = File.ReadAllText(path, Encoding.UTF8);
            json = json.Trim();
            // 兼容单对象与 AiModelStore 的数组格式（取第一个对象）
            int objStart = json.IndexOf('{');
            if (objStart < 0) throw new FormatException("不是有效的 AI 设置文件（找不到配置对象）");
            int depth = 0, objEnd = -1;
            for (int i = objStart; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { objEnd = i; break; } }
            }
            if (objEnd < 0) throw new FormatException("JSON 括号不配对");
            var obj = json.Substring(objStart, objEnd - objStart + 1);

            var r = new ImportResult
            {
                Name = ExtractString(obj, "Name") ?? "",
                Endpoint = ExtractString(obj, "Endpoint") ?? "",
                ApiKey = ExtractString(obj, "ApiKey") ?? "",
                Model = ExtractString(obj, "Model") ?? "",
            };
            r.KeyPresent = r.ApiKey.Length > 0;
            var mt = ExtractRawValue(obj, "MaxTokens");
            if (mt != null && int.TryParse(mt, out var mtv)) r.MaxTokens = mtv;
            var tp = ExtractRawValue(obj, "Temperature");
            if (tp != null && double.TryParse(tp, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var tpv)) r.Temperature = tpv;

            if (string.IsNullOrWhiteSpace(r.Endpoint) || string.IsNullOrWhiteSpace(r.Model))
                throw new FormatException("文件缺少 Endpoint/Model 字段");
            return r;
        }

        // ===== 手写 JSON（与 AiModelStore.ExtractString/Escape 同口径）=====

        private static void AppendString(StringBuilder sb, string key, string value)
        {
            sb.AppendFormat("\"{0}\":\"", key);
            if (value != null)
            {
                foreach (var ch in value)
                {
                    switch (ch)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch);
                            else sb.Append(ch);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        private static string ExtractString(string json, string key)
        {
            // 审计 F1：原实现 Replace 顺序错（先把 \\ 压成 \ 再替 \n，字面反斜杠+n 会被当成换行）
            return Gdterm.Core.JsonText.ExtractString(json, key);
        }

        private static string ExtractRawValue(string json, string key)
        {
            var pattern = "\"" + key + "\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            while (start < json.Length && json[start] == ' ') start++;
            int end = start;
            if (end < json.Length && json[end] == '"')
            {
                end++;
                end = json.IndexOf('"', end);
                if (end < 0) return null;
                return json.Substring(start + 1, end - start - 1);
            }
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ' ') end++;
            return end > start ? json.Substring(start, end - start) : null;
        }
    }
}
