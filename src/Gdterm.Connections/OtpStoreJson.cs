using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Security.Otp;

namespace Gdterm.Connections
{
    /// <summary>OTP 条目存储（接口，UI 依赖它做依赖注入）。</summary>
    public interface IOtpStore
    {
        IList<OtpEntry> LoadAll();
        void SaveAll(IList<OtpEntry> entries);
        OtpEntry Add(OtpEntry entry);
        void Delete(string id);
    }

    /// <summary>
    /// OTP 条目 JSON 文件存储（otp-entries.json，与 ConnectionStoreJson 同款手写 JSON，零外部依赖）。
    /// Secret 为真机密：落盘走 protector（Program 接线：主密码 AES，gdo1: 前缀）。
    /// 未解锁时 LoadAll 返回的 Secret 保持 gdo1: 密文（OtpEntry.CodeAt 返回 null → UI 显示锁定态）。
    /// </summary>
    public class OtpStoreJson : IOtpStore
    {
        private readonly string _filePath;
        private static readonly object _fileLock = new object();

        /// <summary>Secret 落盘/读回保护（主密码派生 AES）。null 时明文（仅 smoke/测试隔离场景）。</summary>
        private static Func<string, string> _protectSecret;
        private static Func<string, string> _unprotectSecret;

        public static void SetSecretProtector(Func<string, string> protect, Func<string, string> unprotect)
        {
            _protectSecret = protect;
            _unprotectSecret = unprotect;
        }

        private static string ProtectField(string value)
        {
            if (string.IsNullOrEmpty(value) || _protectSecret == null) return value;
            try { return _protectSecret(value); } catch { return value; }
        }

        private static string UnprotectField(string value)
        {
            if (string.IsNullOrEmpty(value) || _unprotectSecret == null) return value;
            try { return _unprotectSecret(value); } catch { return value; }
        }

        public OtpStoreJson(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        public IList<OtpEntry> LoadAll()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_filePath)) return new List<OtpEntry>();
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new List<OtpEntry>();
                return Deserialize(json);
            }
        }

        public void SaveAll(IList<OtpEntry> entries)
        {
            lock (_fileLock)
            {
                var json = Serialize(entries);
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
        }

        public OtpEntry Add(OtpEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id))
                entry.Id = Guid.NewGuid().ToString();
            var list = LoadAll();
            list.Add(entry);
            SaveAll(list);
            return entry;
        }

        public void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Id 不能为空", nameof(id));
            var list = LoadAll();
            var index = list.ToList().FindIndex(e => e.Id == id);
            if (index < 0) throw new KeyNotFoundException("未找到 Id 为 " + id + " 的 OTP 条目");
            list.RemoveAt(index);
            SaveAll(list);
        }

        // ---- JSON 序列化（手动实现，不依赖外部库；与 ConnectionStoreJson 同风格） ----

        private static string Serialize(IList<OtpEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"entries\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("    {");
                sb.Append("\"id\": \"" + Escape(e.Id) + "\", ");
                sb.Append("\"issuer\": \"" + Escape(e.Issuer) + "\", ");
                sb.Append("\"account\": \"" + Escape(e.Account) + "\", ");
                sb.Append("\"secret\": \"" + Escape(ProtectField(e.Secret)) + "\", ");
                sb.Append("\"digits\": " + e.Digits + ", ");
                sb.Append("\"period\": " + e.Period + ", ");
                sb.Append("\"algorithm\": \"" + e.Algorithm + "\", ");
                sb.Append("\"totp\": " + (e.IsTotp ? "true" : "false") + ", ");
                sb.Append("\"counter\": " + e.Counter);
                sb.Append(i < entries.Count - 1 ? "}," : "}");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static IList<OtpEntry> Deserialize(string json)
        {
            var result = new List<OtpEntry>();
            var start = json.IndexOf("\"entries\"");
            if (start < 0) return result;
            var arrayStart = json.IndexOf('[', start);
            var arrayEnd = FindMatchingBracket(json, arrayStart);
            if (arrayStart < 0 || arrayEnd < 0) return result;
            var objects = SplitJsonObjects(json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1));

            foreach (var obj in objects)
            {
                var digits = ExtractInt(obj, "digits");
                var period = ExtractInt(obj, "period");
                var totp = ExtractString(obj, "totp") != "false";
                result.Add(new OtpEntry
                {
                    Id = ExtractString(obj, "id"),
                    Issuer = ExtractString(obj, "issuer"),
                    Account = ExtractString(obj, "account"),
                    Secret = UnprotectField(ExtractString(obj, "secret")),
                    Digits = digits >= 6 && digits <= 8 ? digits : 6,
                    Period = period > 0 ? period : 30,
                    Algorithm = OtpGenerator.ParseAlgorithm(ExtractString(obj, "algorithm")),
                    IsTotp = totp,
                    Counter = ExtractInt(obj, "counter")
                });
            }
            return result;
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Unescape(string value)
        {
            if (value == null) return null;
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string ExtractString(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;
            var quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;
            var quoteEnd = quoteStart + 1;
            while (quoteEnd < json.Length)
            {
                if (json[quoteEnd] == '"' && json[quoteEnd - 1] != '\\')
                    break;
                quoteEnd++;
            }
            return Unescape(json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1));
        }

        private static int ExtractInt(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return 0;
            var colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return 0;
            var numStart = colonIdx + 1;
            while (numStart < json.Length && (json[numStart] == ' ' || json[numStart] == '\t'))
                numStart++;
            var numEnd = numStart;
            while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-'))
                numEnd++;
            int result;
            if (int.TryParse(json.Substring(numStart, numEnd - numStart), out result))
                return result;
            return 0;
        }

        private static List<string> SplitJsonObjects(string arrayContent)
        {
            var result = new List<string>();
            int depth = 0;
            int start = -1;
            for (int i = 0; i < arrayContent.Length; i++)
            {
                if (arrayContent[i] == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (arrayContent[i] == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(arrayContent.Substring(start + 1, i - start - 1));
                        start = -1;
                    }
                }
            }
            return result;
        }

        private static int FindMatchingBracket(string json, int openIndex)
        {
            if (openIndex < 0 || openIndex >= json.Length) return -1;
            var open = json[openIndex];
            var close = open == '[' ? ']' : '}';
            int depth = 0;
            for (int i = openIndex; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                else if (json[i] == close) depth--;
                if (depth == 0) return i;
            }
            return -1;
        }
    }
}
