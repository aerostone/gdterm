using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.AI.Models;

namespace Gdterm.AI
{
    /// <summary>
    /// AI 对话历史持久化（data/ai-chat-history.json）。
    /// 只存用户消息与 AI 回复（_history 语义），不含连接上下文注入内容。
    /// 明文 JSON（与 connections.json 同目录同级——历史文本本身非凭据）；
    /// 「清空」按钮/重启后由服务层控制读写。文件损坏时返回空列表，不抛异常。
    /// </summary>
    public sealed class ChatHistoryStore
    {
        private readonly string _filePath;
        private static readonly object _fileLock = new object();

        public ChatHistoryStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>保存历史（截断写入；消息为 null/空时写空历史）。</summary>
        public void Save(IList<ChatMessage> messages)
        {
            lock (_fileLock)
            {
                var sb = new StringBuilder();
                sb.Append("{\"version\": 1, \"messages\": [");
                if (messages != null)
                {
                    for (int i = 0; i < messages.Count; i++)
                    {
                        var m = messages[i];
                        if (m == null) continue;
                        if (i > 0) sb.Append(", ");
                        sb.Append("{\"role\": \"").Append(Escape(m.Role)).Append("\", \"content\": \"")
                          .Append(Escape(m.Content)).Append("\"}");
                    }
                }
                sb.Append("]}");
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, sb.ToString(), new UTF8Encoding(false));
            }
        }

        /// <summary>读取历史；文件不存在/损坏时返回空列表（不抛异常）。</summary>
        public IList<ChatMessage> Load()
        {
            lock (_fileLock)
            {
                var result = new List<ChatMessage>();
                try
                {
                    if (!File.Exists(_filePath)) return result;
                    var json = File.ReadAllText(_filePath, new UTF8Encoding(false));
                    var key = json.IndexOf("\"messages\"");
                    if (key < 0) return result;
                    var arrStart = json.IndexOf('[', key);
                    var arrEnd = FindMatchingBracket(json, arrStart);
                    if (arrStart < 0 || arrEnd <= arrStart) return result;
                    var content = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                    foreach (var obj in SplitObjects(content))
                    {
                        var role = ExtractString(obj, "role");
                        var text = ExtractString(obj, "content");
                        if (string.IsNullOrEmpty(role) || text == null) continue;
                        result.Add(new ChatMessage(role, text));
                    }
                }
                catch
                {
                    // 损坏文件 → 空历史（宁可丢历史，不阻断 AI 功能）
                }
                return result;
            }
        }

        // ---- 手写 JSON（与 ConnectionStoreJson 同风格；额外转义换行/控制字符，聊天文本多行） ----

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length + 8);
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Unescape(string value)
        {
            if (value == null) return null;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length) { sb.Append(ch); continue; }
                var nx = value[++i];
                switch (nx)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        // i 指向 'u'；后需 4 位 hex：i+1..i+4 均在界内
                        if (i + 4 < value.Length)
                        {
                            var hex = value.Substring(i + 1, 4);
                            int code;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out code) && code > 0)
                            {
                                i += 4;
                                sb.Append((char)code);
                            }
                            else
                            {
                                sb.Append('\\').Append(nx);
                            }
                        }
                        else
                        {
                            sb.Append('\\').Append(nx);
                        }
                        break;
                    default:
                        sb.Append(nx);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>找与 arrStart 处 '[' 配对的 ']'（跳过字符串字面量）。</summary>
        private static int FindMatchingBracket(string json, int arrStart)
        {
            if (arrStart < 0 || arrStart >= json.Length) return -1;
            int depth = 0;
            bool inStr = false;
            for (int i = arrStart; i < json.Length; i++)
            {
                var ch = json[i];
                if (inStr)
                {
                    if (ch == '\\') i++;
                    else if (ch == '"') inStr = false;
                }
                else
                {
                    if (ch == '"') inStr = true;
                    else if (ch == '[') depth++;
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth == 0) return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>把对象数组内容按顶层 ',' 拆分（跳过字符串/嵌套花括号）。</summary>
        private static List<string> SplitObjects(string arrayContent)
        {
            var result = new List<string>();
            int depth = 0;
            bool inStr = false;
            int start = -1;
            for (int i = 0; i < arrayContent.Length; i++)
            {
                var ch = arrayContent[i];
                if (inStr)
                {
                    if (ch == '\\') i++;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') inStr = true;
                else if (ch == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(arrayContent.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return result;
        }

        /// <summary>从扁平对象 JSON 里取字符串字段（值本身不含嵌套花括号）。</summary>
        private static string ExtractString(string objJson, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = objJson.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = objJson.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var qStart = objJson.IndexOf('"', colon + 1);
            if (qStart < 0) return null;
            // 从 qStart+1 起找未转义的结束引号
            int i = qStart + 1;
            var sb = new StringBuilder();
            for (; i < objJson.Length; i++)
            {
                var ch = objJson[i];
                if (ch == '\\')
                {
                    if (i + 1 < objJson.Length)
                    {
                        sb.Append(ch).Append(objJson[++i]);
                    }
                }
                else if (ch == '"')
                {
                    return Unescape(sb.ToString());
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return Unescape(sb.ToString());
        }
    }
}
