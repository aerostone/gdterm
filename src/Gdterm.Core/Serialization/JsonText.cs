using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gdterm.Core
{
    /// <summary>
    /// 手写 JSON 的转义 / 反转义 / 字符串取值唯一实现（零外部依赖，符合"不引第三方 JSON 库"约束）。
    ///
    /// 存在理由（2026-11-11 架构审计 F1）：13 个 store 各写一份 Escape/Extract，转义集合互不相同、
    /// 有的只转义不还原，导致 <c>data/config/highlight.json</c> 里的正则在第二次启动后被逐次加倍转义、
    /// 内置高亮规则与触发器全部静默失效。<b>凡是手写 JSON 的 store，Escape 必须走这里，
    /// 读取必须走 <see cref="ExtractString"/>，不得再写第三份替身。</b>
    ///
    /// 契约：
    ///   Escape(Unescape(x)) == x   对所有 UTF-16 字符串成立（含 \ : " \n \r \t 控制符）
    ///   ExtractString 只把未被转义的 " 当结束符
    /// </summary>
    public static class JsonText
    {
        /// <summary>把任意字符串转成可安全放进 JSON 双引号内的文本。</summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>还原 <see cref="Escape"/> 产生的文本；未知转义保留字符本身（不吞数据）。</summary>
        public static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length) { sb.Append(c); continue; }
                char n = value[++i];
                switch (n)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        int code;
                        if (i + 4 < value.Length && int.TryParse(
                                value.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            sb.Append('\\').Append(n);
                        }
                        break;
                    default:
                        // 未知转义：保留原字符，宁可多一个反斜杠也不静默丢字
                        sb.Append(n);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从 openIndex 处的 <c>[</c> 或 <c>{</c> 开始找配对的结束符下标；字符串内部的括号不计数。
        /// 找不到（或 openIndex 不是括号）返回 -1。
        /// </summary>
        public static int FindMatchingBracket(string json, int openIndex)
        {
            if (string.IsNullOrEmpty(json) || openIndex < 0 || openIndex >= json.Length) return -1;
            char open = json[openIndex];
            char close = open == '[' ? ']' : open == '{' ? '}' : open == '(' ? ')' : '\0';
            if (close == '\0') return -1;
            int depth = 0;
            for (int i = openIndex; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') { i = SkipString(json, i); continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>跳过引号字符串，返回结束引号的下标（未闭合则返回最后一个字符下标）。</summary>
        public static int SkipString(string json, int quoteIndex)
        {
            if (string.IsNullOrEmpty(json)) return quoteIndex;
            for (int i = quoteIndex + 1; i < json.Length; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') return i;
            }
            return json.Length - 1;
        }

        /// <summary>
        /// 取 <c>"key": "value"</c> 的字符串值（键后的空格/冒号无要求）。
        /// 结束引号必须是未被转义的那个；键不存在或<b>值不是字符串</b>（true/数字/对象/数组）时返回 null。
        ///
        /// 「值不是字符串就返回 null」是硬要求：调用方普遍写
        /// <c>JsonText.ExtractString(...) ?? 本地裸标量扫描</c>，若这里对 <c>"k":true</c> 顺手返回
        /// 后面那个字符串的值，裸布尔/数字就会被悄悄读成别的字段（审计 F1 的姊妹坑）。
        /// </summary>
        public static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            var token = "\"" + key + "\"";
            var idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + token.Length);
            if (colon < 0) return null;
            // 冒号后必须紧跟（允许空白）一个引号，否则说明是裸标量/嵌套结构
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\r' || json[start] == '\n')) start++;
            if (start >= json.Length || json[start] != '"') return null;
            int end = SkipString(json, start);
            if (end <= start || end >= json.Length || json[end] != '"') return null;
            return Unescape(json.Substring(start + 1, end - start - 1));
        }

        /// <summary>
        /// 把一个 JSON 数组的**内容**（不含首尾方括号）切成若干顶层对象文本，每个都带自己的首尾花括号。
        ///
        /// 存在理由（审计 F1 姊妹坑，CI 43060434586 实证）：各 store 自己写
        /// <c>for(...){ if (c=='{') depth++; else if (c=='}') depth--; ... }</c> 切对象，
        /// **数括号时不管字符串**。于是一个值里出现 '[' / '{' / '}' 就把整块切坏或切不到：
        /// 终端快捷键 <c>"value":"\u001b[A"</c>（Esc+[）会让 customBindings 整段解析失败，
        /// store 静默回落默认配置——用户自定义绑定全丢，且没有任何报错。
        /// 现在切分统一走 <see cref="FindMatchingBracket"/>（字符串内容不计数）。
        /// </summary>
        public static List<string> SplitObjects(string arrayContent)
        {
            var items = new List<string>();
            if (string.IsNullOrEmpty(arrayContent)) return items;
            for (int i = 0; i < arrayContent.Length; i++)
            {
                if (arrayContent[i] != '{') continue;
                int end = FindMatchingBracket(arrayContent, i);
                if (end < 0) break;   // 截断的尾巴：不猜，已解析到的照常返回
                items.Add(arrayContent.Substring(i, end - i + 1));
                i = end;
            }
            return items;
        }



        /// <summary>
        /// 把 JSON 字符串数组的内容切成若干元素（已反转义）。转义感知：
        /// "\"" 不再被当成结束引号（旧的自写实现用 IndexOf('"')，含引号的元素会被截断）。
        /// </summary>
        public static List<string> SplitStringArray(string arrayContent)
        {
            var items = new List<string>();
            if (string.IsNullOrEmpty(arrayContent)) return items;
            int i = 0;
            while (i < arrayContent.Length)
            {
                if (arrayContent[i] == '"')
                {
                    int end = SkipString(arrayContent, i);
                    if (end <= i || end >= arrayContent.Length) break;
                    items.Add(Unescape(arrayContent.Substring(i + 1, end - i - 1)));
                    i = end + 1;
                }
                else i++;
            }
            return items;
        }
    }
}
