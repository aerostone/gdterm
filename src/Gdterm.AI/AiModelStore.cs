using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Gdterm.AI.Models;

namespace Gdterm.AI
{
    /// <summary>
    /// AI 模型配置持久化。ApiKey 落盘优先用主密码派生 AES（gdk2:），
    /// 无主密码时退回 gdk1: 固定密钥 XOR；读取兼容明文与 gdk1。
    /// </summary>
    public class AiModelStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private List<AiModelConfig> _models;
        private Func<string> _masterPasswordProvider;

        public AiModelStore(string filePath)
        {
            _filePath = filePath;
            _models = new List<AiModelConfig>();
            Load();
        }

        /// <summary>
        /// 注入主密码提供者（解锁后返回明文；锁定返回 null）。
        /// 有主密码时 Save 使用 gdk2 AES；否则 gdk1 XOR。
        /// </summary>
        public void SetMasterPasswordProvider(Func<string> provider)
        {
            _masterPasswordProvider = provider;
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

        /// <summary>
        /// 若当前能拿到主密码，把仍为 gdk1/明文的 ApiKey 重写为 gdk2。
        /// 主密码变更后调用可升级存量文件。
        /// </summary>
        public int UpgradeSecretsToMasterKey()
        {
            lock (_lock)
            {
                var mp = TryGetMasterPassword();
                if (string.IsNullOrEmpty(mp)) return 0;
                // Save 会用 gdk2 重写全部 ApiKey
                Save();
                return _models.Count(m => !string.IsNullOrEmpty(m.ApiKey));
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

        private string TryGetMasterPassword()
        {
            try
            {
                var p = _masterPasswordProvider != null ? _masterPasswordProvider() : null;
                return string.IsNullOrEmpty(p) ? null : p;
            }
            catch { return null; }
        }

        // gdk2: PBKDF2(主密码) + AES-CBC；gdk1: 固定密钥 XOR；兼容旧明文
        private const string PrefixV2 = "gdk2:";
        private const string PrefixV1 = "gdk1:";
        private static readonly byte[] FixedXorKey = Encoding.UTF8.GetBytes("gdterm-ai-model-key-v1");
        private static readonly byte[] Pbkdf2SaltPurpose = Encoding.UTF8.GetBytes("gdterm-ai-apikey-v2");

        private string ProtectSecret(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (plain.StartsWith(PrefixV2, StringComparison.Ordinal) ||
                plain.StartsWith(PrefixV1, StringComparison.Ordinal))
                return plain;

            var master = TryGetMasterPassword();
            if (!string.IsNullOrEmpty(master))
            {
                try { return PrefixV2 + ProtectAes(plain, master); }
                catch { /* fall through to v1 */ }
            }
            return PrefixV1 + ProtectXor(plain);
        }

        private string UnprotectSecret(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";

            if (stored.StartsWith(PrefixV2, StringComparison.Ordinal))
            {
                var master = TryGetMasterPassword();
                if (string.IsNullOrEmpty(master))
                    return ""; // 锁定时无法还原；内存中不保留密文口令
                try { return UnprotectAes(stored.Substring(PrefixV2.Length), master); }
                catch { return ""; }
            }

            if (stored.StartsWith(PrefixV1, StringComparison.Ordinal))
            {
                try { return UnprotectXor(stored.Substring(PrefixV1.Length)); }
                catch { return ""; }
            }

            // 兼容旧明文
            return stored;
        }

        private static string ProtectXor(string plain)
        {
            var data = Encoding.UTF8.GetBytes(plain);
            var x = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                x[i] = (byte)(data[i] ^ FixedXorKey[i % FixedXorKey.Length]);
            return Convert.ToBase64String(x);
        }

        private static string UnprotectXor(string b64)
        {
            var x = Convert.FromBase64String(b64);
            var data = new byte[x.Length];
            for (int i = 0; i < x.Length; i++)
                data[i] = (byte)(x[i] ^ FixedXorKey[i % FixedXorKey.Length]);
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>payload = salt(16) | iv(16) | cipher</summary>
        private static string ProtectAes(string plain, string masterPassword)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            byte[] key;
            using (var derive = new Rfc2898DeriveBytes(masterPassword, salt, 10000))
                key = derive.GetBytes(32);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.GenerateIV();
                using (var enc = aes.CreateEncryptor())
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plain);
                    var cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    var payload = new byte[16 + 16 + cipher.Length];
                    Buffer.BlockCopy(salt, 0, payload, 0, 16);
                    Buffer.BlockCopy(aes.IV, 0, payload, 16, 16);
                    Buffer.BlockCopy(cipher, 0, payload, 32, cipher.Length);
                    return Convert.ToBase64String(payload);
                }
            }
        }

        private static string UnprotectAes(string b64, string masterPassword)
        {
            var payload = Convert.FromBase64String(b64);
            if (payload.Length < 33) throw new CryptographicException("payload too short");
            var salt = new byte[16];
            var iv = new byte[16];
            Buffer.BlockCopy(payload, 0, salt, 0, 16);
            Buffer.BlockCopy(payload, 16, iv, 0, 16);
            var cipher = new byte[payload.Length - 32];
            Buffer.BlockCopy(payload, 32, cipher, 0, cipher.Length);

            byte[] key;
            using (var derive = new Rfc2898DeriveBytes(masterPassword, salt, 10000))
                key = derive.GetBytes(32);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                {
                    var plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }
    }
}
