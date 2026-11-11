using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.AI.Models;

namespace Gdterm.AI
{
    /// <summary>
    /// AI 对话历史持久化（data/ai-chat-history.json），按连接键控（F13）。
    /// 文件 v2 格式：{"version":2,"conversations":{"&lt;连接键&gt;":[{"role","content"}...]}}；
    /// v1 旧格式（全局 "messages" 数组）读取时自动迁入 "global" 键，不丢历史。
    /// 只存用户消息与 AI 回复（_history 语义），不含连接上下文注入内容。
    /// 明文 JSON（与 connections.json 同目录同级——历史文本本身非凭据）。
    /// 文件损坏时返回空，不抛异常；单文件整表写回（每键有 40 条硬顶，文件保持小）。
    /// </summary>
    public sealed class ChatHistoryStore
    {
        /// <summary>无连接身份（本地终端/未连接/键为空）时的默认键；v1 文件也迁入此键。</summary>
        public const string DefaultKey = "global";

        private readonly string _filePath;
        private static readonly object _fileLock = new object();
        private Dictionary<string, List<ChatMessage>> _conversations; // null = 尚未读盘

        public ChatHistoryStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <summary>读取指定连接键的历史；文件不存在/损坏/无此键时返回空列表（不抛异常）。</summary>
        public IList<ChatMessage> Load(string key)
        {
            lock (_fileLock)
            {
                EnsureLoaded();
                List<ChatMessage> list;
                if (_conversations.TryGetValue(NormalizeKey(key), out list))
                    return new List<ChatMessage>(list);
                return new List<ChatMessage>();
            }
        }

        /// <summary>保存指定连接键的历史（整表写回）；messages 为 null/空 = 删除该键。</summary>
        public void Save(string key, IList<ChatMessage> messages)
        {
            lock (_fileLock)
            {
                EnsureLoaded();
                var k = NormalizeKey(key);
                if (messages == null || messages.Count == 0)
                {
                    _conversations.Remove(k);
                }
                else
                {
                    var list = new List<ChatMessage>();
                    foreach (var m in messages)
                        if (m != null) list.Add(m);
                    _conversations[k] = list;
                }
                WriteAll();
            }
        }

        private static string NormalizeKey(string key)
        {
            return string.IsNullOrEmpty(key) ? DefaultKey : key;
        }

        // ---- 读盘（v2 conversations 表；v1 messages 数组 → DefaultKey 迁移） ----

        private void EnsureLoaded()
        {
            if (_conversations != null) return;
            _conversations = new Dictionary<string, List<ChatMessage>>();
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath, new UTF8Encoding(false));

                // v2：{"version":2,"conversations":{"key":[...],...}}
                // 「键名后紧跟 { 」才算命中——防聊天文本里恰好出现 "conversations" 字面量误判
                var cIdx = FindKeyWithValue(json, "conversations", '{');
                if (cIdx >= 0)
                {
                    var oEnd = FindMatchingBrace(json, cIdx);
                    if (oEnd > cIdx)
                    {
                        var content = json.Substring(cIdx + 1, oEnd - cIdx - 1);
                        var entries = SplitMapEntries(content);
                        if (entries.Count > 0 || content.Trim().Length == 0)
                        {
                            foreach (var entry in entries)
                            {
                                var k = Unescape(entry.Key);
                                if (string.IsNullOrEmpty(k)) continue;
                                _conversations[k] = ParseMessages(entry.Value);
                            }
                            return;
                        }
                    }
                }

                // v1 兼容：{"version":1,"messages":[...]} → 迁入 DefaultKey
                var mIdx = FindKeyWithValue(json, "messages", '[');
                if (mIdx >= 0)
                {
                    var arrEnd = FindMatchingBracket(json, mIdx);
                    if (arrEnd > mIdx)
                        _conversations[DefaultKey] = ParseMessages(json.Substring(mIdx + 1, arrEnd - mIdx - 1));
                }
            }
            catch
            {
                // 损坏文件 → 空历史（宁可丢历史，不阻断 AI 功能）
                _conversations = new Dictionary<string, List<ChatMessage>>();
            }
        }

        /// <summary>找 "key" 字段且其值以 valueOpener 开头的位置（跳过空白）；未命中返回 -1。</summary>
        private static int FindKeyWithValue(string json, string keyName, char valueOpener)
        {
            var pattern = "\"" + keyName + "\"";
            int idx = 0;
            while (true)
            {
                idx = json.IndexOf(pattern, idx, StringComparison.Ordinal);
                if (idx < 0) return -1;
                int j = idx + pattern.Length;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j < json.Length && json[j] == ':')
                {
                    j++;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (j < json.Length && json[j] == valueOpener) return j;
                }
                idx += pattern.Length;
            }
        }

        // ---- 写盘（恒 v2 格式） ----

        private void WriteAll()
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\": 2, \"conversations\": {");
            bool firstKey = true;
            foreach (var kv in _conversations)
            {
                if (!firstKey) sb.Append(", ");
                firstKey = false;
                sb.Append("\"").Append(Escape(kv.Key)).Append("\": [");
                bool firstMsg = true;
                foreach (var m in kv.Value)
                {
                    if (m == null) continue;
                    if (!firstMsg) sb.Append(", ");
                    firstMsg = false;
                    sb.Append("{\"role\": \"").Append(Escape(m.Role)).Append("\", \"content\": \"")
                      .Append(Escape(m.Content)).Append("\"}");
                }
                sb.Append("]");
            }
            sb.Append("}}");
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, sb.ToString(), new UTF8Encoding(false));
        }

        // ---- 手写 JSON（与 ConnectionStoreJson 同风格；额外转义换行/控制字符，聊天文本多行） ----

        private static string Escape(string value)
        {
            return Gdterm.Core.JsonText.Escape(value); // 审计 F1：统一转义契约
        }

        private static string Unescape(string value)
        {
            return Gdterm.Core.JsonText.Unescape(value); // 审计 F1：统一反转义
        }

        /// <summary>解析消息对象数组内容（数组 '[' 与 ']' 之间的文本）。</summary>
        private static List<ChatMessage> ParseMessages(string arrayContent)
        {
            var result = new List<ChatMessage>();
            foreach (var obj in SplitObjects(arrayContent))
            {
                var role = ExtractString(obj, "role");
                var text = ExtractString(obj, "content");
                if (string.IsNullOrEmpty(role) || text == null) continue;
                result.Add(new ChatMessage(role, text));
            }
            return result;
        }

        /// <summary>把顶层 "key": [ ... ] 条目拆成 (原始 key, 数组内容) 对（跳过字符串/嵌套括号）。</summary>
        private static List<KeyValuePair<string, string>> SplitMapEntries(string objContent)
        {
            var result = new List<KeyValuePair<string, string>>();
            int i = 0;
            int n = objContent.Length;
            while (i < n)
            {
                // 跳过空白与顶层分隔逗号（漏逗号→第 2 个及以后的键全部丢失，多连接历史随重启蒸发——GHA 实证）
                while (i < n && (char.IsWhiteSpace(objContent[i]) || objContent[i] == ',')) i++;
                if (i >= n || objContent[i] != '"') break;
                // 读 key 字符串（尊重转义）
                int keyStart = i + 1;
                int keyEnd = -1;
                for (int j = keyStart; j < n; j++)
                {
                    if (objContent[j] == '\\' && j + 1 < n) j++;
                    else if (objContent[j] == '"') { keyEnd = j; break; }
                }
                if (keyEnd < 0) break;
                var key = objContent.Substring(keyStart, keyEnd - keyStart);
                // 找 ':' 后紧跟的 '['
                int j2 = keyEnd + 1;
                while (j2 < n && objContent[j2] != ':')
                {
                    if (objContent[j2] == '}' || objContent[j2] == ',') break;
                    j2++;
                }
                if (j2 >= n || objContent[j2] != ':') break;
                j2++;
                while (j2 < n && objContent[j2] != '[')
                {
                    if (objContent[j2] == '}' || objContent[j2] == ',') break;
                    j2++;
                }
                if (j2 >= n || objContent[j2] != '[') break;
                int arrEnd = FindMatchingBracket(objContent, j2);
                if (arrEnd <= j2) break;
                result.Add(new KeyValuePair<string, string>(key, objContent.Substring(j2 + 1, arrEnd - j2 - 1)));
                i = arrEnd + 1;
            }
            return result;
        }

        /// <summary>找与 pos 处 '[' 配对的 ']'（跳过字符串字面量）。</summary>
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

        /// <summary>找与 pos 处 '{' 配对的 '}'（跳过字符串字面量与嵌套数组）。</summary>
        private static int FindMatchingBrace(string json, int braceStart)
        {
            if (braceStart < 0 || braceStart >= json.Length) return -1;
            int depth = 0;
            bool inStr = false;
            for (int i = braceStart; i < json.Length; i++)
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
                    else if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0 && ch == '}') return i;
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
            return Gdterm.Core.JsonText.ExtractString(objJson, key); // 审计 F1：统一取值实现
        }
    }
}
