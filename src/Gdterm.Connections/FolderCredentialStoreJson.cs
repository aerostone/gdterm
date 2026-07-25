using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 基于 JSON 文件的文件夹凭据存储
    /// </summary>
    public class FolderCredentialStoreJson : IFolderCredentialStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();

        public FolderCredentialStoreJson(string filePath)
        {
            _filePath = filePath;
        }

        public IList<FolderCredentialEntry> LoadAll()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                    return new List<FolderCredentialEntry>();

                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                return ParseEntries(json);
            }
        }

        public void SaveAll(IList<FolderCredentialEntry> entries)
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = SerializeEntries(entries);
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
        }

        public void SetCredential(string groupPath, string credentialRefId)
        {
            if (string.IsNullOrEmpty(groupPath))
                throw new ArgumentNullException(nameof(groupPath));

            var entries = LoadAll().ToList();
            var existing = entries.FirstOrDefault(e =>
                string.Equals(e.GroupPath, groupPath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.CredentialRefId = credentialRefId;
            }
            else
            {
                entries.Add(new FolderCredentialEntry
                {
                    GroupPath = groupPath,
                    CredentialRefId = credentialRefId,
                    CreatedAt = DateTime.UtcNow
                });
            }

            SaveAll(entries);
        }

        public void RemoveCredential(string groupPath)
        {
            var entries = LoadAll().ToList();
            var removed = entries.RemoveAll(e =>
                string.Equals(e.GroupPath, groupPath, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
                SaveAll(entries);
        }

        public string ResolveByInheritance(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath))
                return null;

            var entries = LoadAll();
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                lookup[entry.GroupPath] = entry.CredentialRefId;

            // 从当前路径逐级向上查找
            var path = groupPath.TrimEnd('/');
            while (!string.IsNullOrEmpty(path))
            {
                if (lookup.ContainsKey(path))
                    return lookup[path];

                var lastSlash = path.LastIndexOf('/');
                if (lastSlash <= 0)
                {
                    // 检查根路径 "/"
                    if (lookup.ContainsKey("/"))
                        return lookup["/"];
                    break;
                }

                path = path.Substring(0, lastSlash);
            }

            return null;
        }

        // ===== 手写 JSON 序列化 =====

        private string SerializeEntries(IList<FolderCredentialEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (i > 0) sb.Append(",\n");
                sb.Append("  {\n");
                sb.Append($"    \"groupPath\": {EscapeJson(e.GroupPath)},\n");
                sb.Append($"    \"credentialRefId\": {EscapeJson(e.CredentialRefId)},\n");
                sb.Append($"    \"createdAt\": {EscapeJson(e.CreatedAt.ToString("o"))},\n");
                sb.Append($"    \"note\": {EscapeJson(e.Note ?? "")}\n");
                sb.Append("  }");
            }
            sb.Append("\n]");
            return sb.ToString();
        }

        private List<FolderCredentialEntry> ParseEntries(string json)
        {
            var result = new List<FolderCredentialEntry>();
            if (string.IsNullOrWhiteSpace(json) || json.TrimStart().StartsWith("[") == false)
                return result;

            int pos = 0;
            while (pos < json.Length)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0) break;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                var obj = json.Substring(objStart, objEnd - objStart + 1);
                var entry = new FolderCredentialEntry
                {
                    GroupPath = ExtractString(obj, "groupPath"),
                    CredentialRefId = ExtractString(obj, "credentialRefId"),
                    Note = ExtractString(obj, "note")
                };

                var dateStr = ExtractString(obj, "createdAt");
                if (!string.IsNullOrEmpty(dateStr))
                    DateTime.TryParse(dateStr, out var dt);

                result.Add(entry);
                pos = objEnd + 1;
            }

            return result;
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

        private static string EscapeJson(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        }
    }
}
