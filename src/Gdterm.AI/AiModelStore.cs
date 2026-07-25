using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.AI.Models;

namespace Gdterm.AI
{
    public class AiModelStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private List<AiModelConfig> _models;

        public AiModelStore(string filePath)
        {
            _filePath = filePath;
            _models = new List<AiModelConfig>();
            Load();
        }

        public IReadOnlyList<AiModelConfig> Models
        {
            get { lock (_lock) return _models.ToList().AsReadOnly(); }
        }

        public AiModelConfig GetDefault()
        {
            lock (_lock) return _models.FirstOrDefault(m => m.IsDefault) ?? _models.FirstOrDefault();
        }

        public AiModelConfig GetById(string id)
        {
            lock (_lock) return _models.FirstOrDefault(m => m.Id == id);
        }

        public void SetDefault(string id)
        {
            lock (_lock)
            {
                foreach (var m in _models) m.IsDefault = m.Id == id;
                Save();
            }
        }

        public void Add(AiModelConfig config)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(config.Id))
                    config.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                if (!_models.Any())
                    config.IsDefault = true;
                _models.Add(config);
                Save();
            }
        }

        public void Update(AiModelConfig config)
        {
            lock (_lock)
            {
                var existing = _models.FirstOrDefault(m => m.Id == config.Id);
                if (existing != null)
                {
                    var idx = _models.IndexOf(existing);
                    _models[idx] = config;
                    Save();
                }
            }
        }

        public void Remove(string id)
        {
            lock (_lock)
            {
                _models.RemoveAll(m => m.Id == id);
                if (_models.Any() && !_models.Any(m => m.IsDefault))
                    _models[0].IsDefault = true;
                Save();
            }
        }

        public void RecordUsage(string id, long tokens)
        {
            lock (_lock)
            {
                var m = _models.FirstOrDefault(x => x.Id == id);
                if (m != null)
                {
                    m.TotalTokensUsed += tokens;
                    m.LastUsedAt = DateTime.Now;
                    Save();
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) { _models = new List<AiModelConfig>(); return; }
                _models = ParseModels(File.ReadAllText(_filePath, Encoding.UTF8));
            }
            catch
            {
                _models = new List<AiModelConfig>();
            }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, SerializeModels(_models), Encoding.UTF8);
            }
            catch { }
        }

        private string SerializeModels(List<AiModelConfig> models)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < models.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.AppendFormat("\"Id\":\"{0}\"", Escape(models[i].Id));
                sb.AppendFormat(",\"Name\":\"{0}\"", Escape(models[i].Name));
                sb.AppendFormat(",\"Endpoint\":\"{0}\"", Escape(models[i].Endpoint));
                sb.AppendFormat(",\"ApiKey\":\"{0}\"", Escape(ProtectSecret(models[i].ApiKey)));
                sb.AppendFormat(",\"Model\":\"{0}\"", Escape(models[i].Model));
                if (models[i].MaxTokens.HasValue) sb.AppendFormat(",\"MaxTokens\":{0}", models[i].MaxTokens.Value);
                if (models[i].Temperature.HasValue) sb.AppendFormat(",\"Temperature\":{0}", models[i].Temperature.Value.ToString("F1"));
                sb.AppendFormat(",\"IsDefault\":{0}", models[i].IsDefault ? "true" : "false");
                sb.AppendFormat(",\"LastUsedAt\":\"{0}\"", models[i].LastUsedAt.ToString("o"));
                sb.AppendFormat(",\"TotalTokensUsed\":{0}", models[i].TotalTokensUsed);
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private List<AiModelConfig> ParseModels(string json)
        {
            var result = new List<AiModelConfig>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            json = json.Trim();
            if (!json.StartsWith("[")) return result;
            int depth = 0; int objStart = -1;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{') { if (depth == 0) objStart = i; depth++; }
                else if (json[i] == '}') { depth--; if (depth == 0 && objStart >= 0) { result.Add(ParseModel(json.Substring(objStart, i - objStart + 1))); objStart = -1; } }
            }
            return result;
        }

        private AiModelConfig ParseModel(string obj)
        {
            var m = new AiModelConfig();
            m.Id = ExtractString(obj, "Id") ?? "";
            m.Name = ExtractString(obj, "Name") ?? "";
            m.Endpoint = ExtractString(obj, "Endpoint") ?? "";
            m.ApiKey = UnprotectSecret(ExtractString(obj, "ApiKey") ?? "");
            m.Model = ExtractString(obj, "Model") ?? "";
            var maxTok = ExtractRawValue(obj, "MaxTokens"); if (maxTok != null) m.MaxTokens = int.Parse(maxTok);
            var temp = ExtractRawValue(obj, "Temperature"); if (temp != null) m.Temperature = double.Parse(temp);
            m.IsDefault = ExtractRawValue(obj, "IsDefault") == "true";
            m.TotalTokensUsed = long.TryParse(ExtractRawValue(obj, "TotalTokensUsed"), out var t) ? t : 0;
            return m;
        }

        private static string ExtractString(string json, string key)
        {
            var pattern = "\"" + key + "\":\"";
            int start = json.IndexOf(pattern);
            if (start < 0) return null;
            start += pattern.Length;
            int end = json.IndexOf("\"", start);
            if (end < 0) return null;
            return json.Substring(start, end - start).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string ExtractRawValue(string json, string key)
        {
            var pattern = "\"" + key + "\":";
            int start = json.IndexOf(pattern);
            if (start < 0) return null;
            start += pattern.Length;
            while (start < json.Length && json[start] == ' ') start++;
            int end = start;
            if (end < json.Length && json[end] == '"') { end++; end = json.IndexOf("\"", end); return json.Substring(start + 1, end - start - 1); }
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ' ') end++;
            return json.Substring(start, end - start);
        }

        private static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        // 轻量可逆混淆：打开 data/config 时不直接看见 ApiKey 明文。
        // 非 DPAPI；完整拷走 data/ 仍可还原。长期应迁入 KeePass。
        private const string SecretPrefix = "gdk1:";
        private static readonly byte[] SecretKey = Encoding.UTF8.GetBytes("gdterm-ai-model-key-v1");

        private static string ProtectSecret(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (plain.StartsWith(SecretPrefix, StringComparison.Ordinal)) return plain;
            var data = Encoding.UTF8.GetBytes(plain);
            var x = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                x[i] = (byte)(data[i] ^ SecretKey[i % SecretKey.Length]);
            return SecretPrefix + Convert.ToBase64String(x);
        }

        private static string UnprotectSecret(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(SecretPrefix, StringComparison.Ordinal))
                return stored; // 兼容旧明文
            try
            {
                var x = Convert.FromBase64String(stored.Substring(SecretPrefix.Length));
                var data = new byte[x.Length];
                for (int i = 0; i < x.Length; i++)
                    data[i] = (byte)(x[i] ^ SecretKey[i % SecretKey.Length]);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
    }
}
