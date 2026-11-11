using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Models;
using Gdterm.Core;

namespace Gdterm.Connections
{
    /// <summary>
    /// IConnectionStore 的 JSON 文件实现
    /// </summary>
    public class ConnectionStoreJson : IConnectionStore
    {
        private readonly string _filePath;
        private static readonly object _fileLock = new object();

        /// <summary>
        /// 可选：主机/用户名落盘保护（主密码派生）。null 时明文（兼容旧文件）。
        /// protect(plain) / unprotect(stored) 返回处理后的字符串。
        /// </summary>
        private static Func<string, string> _protectHost;
        private static Func<string, string> _unprotectHost;

        public static void SetHostProtector(Func<string, string> protect, Func<string, string> unprotect)
        {
            _protectHost = protect;
            _unprotectHost = unprotect;
        }

        private static string ProtectField(string value)
        {
            if (string.IsNullOrEmpty(value) || _protectHost == null) return value;
            try { return _protectHost(value); } catch { return value; }
        }

        private static string UnprotectField(string value)
        {
            if (string.IsNullOrEmpty(value) || _unprotectHost == null) return value;
            try { return _unprotectHost(value); } catch { return value; }
        }

        /// <summary>
        /// 创建连接存储实例
        /// </summary>
        /// <param name="filePath">connections.json 文件路径</param>
        public ConnectionStoreJson(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        }

        /// <inheritdoc/>
        public IList<ConnectionConfig> LoadAll()
        {
            lock (_fileLock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<ConnectionConfig>();
                }

                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<ConnectionConfig>();
                }

                return DeserializeConnections(json);
            }
        }

        /// <inheritdoc/>
        public void SaveAll(IList<ConnectionConfig> connections)
        {
            lock (_fileLock)
            {
                var json = SerializeConnections(connections);
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
        }

        /// <inheritdoc/>
        public ConnectionConfig Add(ConnectionConfig connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            // 自动生成 Id
            if (string.IsNullOrEmpty(connection.Id))
            {
                connection.Id = Guid.NewGuid().ToString();
            }

            var list = LoadAll();
            list.Add(connection);
            SaveAll(list);
            return connection;
        }

        /// <inheritdoc/>
        public void Update(ConnectionConfig connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (string.IsNullOrEmpty(connection.Id))
                throw new ArgumentException(L10n.T("连接 Id 不能为空"), nameof(connection));

            var list = LoadAll();
            var index = list.ToList().FindIndex(c => c.Id == connection.Id);
            if (index < 0)
                throw new KeyNotFoundException(L10n.Tf("未找到 Id 为 {0} 的连接", connection.Id));

            list[index] = connection;
            SaveAll(list);
        }

        /// <inheritdoc/>
        public void Delete(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                throw new ArgumentException(L10n.T("连接 Id 不能为空"), nameof(connectionId));

            var list = LoadAll();
            var index = list.ToList().FindIndex(c => c.Id == connectionId);
            if (index < 0)
                throw new KeyNotFoundException(L10n.Tf("未找到 Id 为 {0} 的连接", connectionId));

            list.RemoveAt(index);
            SaveAll(list);
        }

        /// <inheritdoc/>
        public ConnectionConfig GetById(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                throw new ArgumentException(L10n.T("连接 Id 不能为空"), nameof(connectionId));

            var list = LoadAll();
            return list.FirstOrDefault(c => c.Id == connectionId);
        }

        /// <inheritdoc/>
        public IList<GroupNode> GetGroupTree()
        {
            var connections = LoadAll();
            var root = new GroupNode { Name = "", FullPath = "" };

            foreach (var conn in connections)
            {
                var groupPath = conn.GroupPath ?? "";
                var parts = groupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var current = root;

                // 遍历路径层级，逐级找到或创建子节点
                var currentPath = "";
                foreach (var part in parts)
                {
                    currentPath = string.IsNullOrEmpty(currentPath) ? part : currentPath + "/" + part;
                    var child = current.Children.FirstOrDefault(c => c.Name == part);
                    if (child == null)
                    {
                        child = new GroupNode { Name = part, FullPath = currentPath };
                        current.Children.Add(child);
                    }
                    current = child;
                }

                current.Connections.Add(conn);
            }

            return root.Children;
        }

        // ---- JSON 序列化（手动实现，不依赖外部库） ----

        private string SerializeConnections(IList<ConnectionConfig> connections)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"connections\": [");

            for (int i = 0; i < connections.Count; i++)
            {
                var c = connections[i];
                sb.Append("    {");
                sb.Append($"\"id\": \"{Escape(c.Id)}\", ");
                sb.Append($"\"name\": \"{Escape(c.Name)}\", ");
                sb.Append($"\"protocol\": \"{c.Protocol}\", ");
                sb.Append($"\"host\": \"{Escape(ProtectField(c.Host))}\", ");
                sb.Append($"\"port\": {c.Port}, ");
                sb.Append($"\"username\": \"{Escape(ProtectField(c.Username))}\", ");
                sb.Append($"\"domain\": \"{Escape(c.Domain)}\", ");
                sb.Append($"\"groupPath\": \"{Escape(c.GroupPath)}\", ");
                sb.Append($"\"credentialRefId\": \"{Escape(c.CredentialRefId)}\"");

                if (c.JumpChain != null)
                {
                    sb.Append(", \"jumpChain\": ");
                    SerializeJumpChain(sb, c.JumpChain);
                }

                if (c.Tunnel != null)
                {
                    sb.Append(", \"tunnel\": ");
                    SerializeTunnel(sb, c.Tunnel);
                }

                if (c.Proxy != null && c.Proxy.Kind != Gdterm.Core.Models.ProxyKind.None)
                {
                    sb.Append(", \"proxy\": ");
                    SerializeProxy(sb, c.Proxy);
                }

                if (c.Serial != null)
                {
                    sb.Append(", \"serial\": ");
                    SerializeSerial(sb, c.Serial);
                }

                if (c.Metadata != null && c.Metadata.Count > 0)
                {
                    sb.Append(", \"metadata\": {");
                    var first = true;
                    foreach (var kv in c.Metadata)
                    {
                        if (!first) sb.Append(", ");
                        sb.Append($"\"{Escape(kv.Key)}\": \"{Escape(kv.Value)}\"");
                        first = false;
                    }
                    sb.Append("}");
                }

                sb.Append(i < connections.Count - 1 ? "}," : "}");
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private void SerializeJumpChain(StringBuilder sb, JumpChainConfig chain)
        {
            sb.Append("{\"hops\": [");
            if (chain.Hops != null)
            {
                for (int i = 0; i < chain.Hops.Count; i++)
                {
                    var h = chain.Hops[i];
                    sb.Append($"{{\"host\": \"{Escape(ProtectField(h.Host))}\", \"port\": {h.Port}, \"username\": \"{Escape(ProtectField(h.Username))}\", \"credentialRefId\": \"{Escape(h.CredentialRefId)}\"}}");
                    if (i < chain.Hops.Count - 1) sb.Append(", ");
                }
            }
            sb.Append("]}");
        }

        private void SerializeProxy(StringBuilder sb, Gdterm.Core.Models.ProxyConfig proxy)
        {
            sb.Append("{");
            sb.Append("\"kind\": \"" + proxy.Kind + "\", ");
            sb.Append("\"host\": \"" + Escape(ProtectField(proxy.Host)) + "\", ");
            sb.Append("\"port\": " + proxy.Port + ", ");
            sb.Append("\"username\": \"" + Escape(ProtectField(proxy.Username)) + "\", ");
            sb.Append("\"password\": \"" + Escape(ProtectField(proxy.Password)) + "\"");
            sb.Append("}");
        }

        private void SerializeTunnel(StringBuilder sb, TunnelConfig tunnel)
        {
            sb.Append($"{{\"type\": \"{tunnel.Type}\", \"localPort\": {tunnel.LocalPort}, \"remoteHost\": \"{Escape(tunnel.RemoteHost)}\", \"remotePort\": {tunnel.RemotePort}}}");
        }

        private void SerializeSerial(StringBuilder sb, SerialConfig serial)
        {
            sb.Append("{");
            sb.Append($"\"portName\": \"{Escape(serial.PortName)}\", ");
            sb.Append($"\"baudRate\": {serial.BaudRate}, ");
            sb.Append($"\"dataBits\": {serial.DataBits}, ");
            sb.Append($"\"stopBits\": \"{serial.StopBits}\", ");
            sb.Append($"\"parity\": \"{serial.Parity}\", ");
            sb.Append($"\"handshake\": \"{serial.Handshake}\"");
            sb.Append("}");
        }

        private string Escape(string value)
        {
            if (value == null) return "";
            // 审计 F1：统一转义契约（含 \n \r \t 与其余控制符），不再只转 \\ 和 "
            return Gdterm.Core.JsonText.Escape(value);
        }

        // ---- JSON 反序列化（简单实现，不依赖外部库） ----

        private IList<ConnectionConfig> DeserializeConnections(string json)
        {
            var result = new List<ConnectionConfig>();
            var connectionsStart = json.IndexOf("\"connections\"");
            if (connectionsStart < 0) return result;

            var arrayStart = json.IndexOf('[', connectionsStart);
            var arrayEnd = FindMatchingBracket(json, arrayStart);
            if (arrayStart < 0 || arrayEnd < 0) return result;

            var arrayContent = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
            var objects = SplitJsonObjects(arrayContent);

            foreach (var obj in objects)
            {
                var conn = new ConnectionConfig
                {
                    Id = ExtractString(obj, "id"),
                    Name = ExtractString(obj, "name"),
                    Protocol = ParseEnum<Gdterm.Core.Enums.ProtocolType>(ExtractString(obj, "protocol")),
                    Host = UnprotectField(ExtractString(obj, "host")),
                    Port = ExtractInt(obj, "port"),
                    Username = UnprotectField(ExtractString(obj, "username")),
                    Domain = ExtractString(obj, "domain"),
                    GroupPath = ExtractString(obj, "groupPath"),
                    CredentialRefId = ExtractString(obj, "credentialRefId")
                };

                // 跳板链
                var jumpChainStr = ExtractObject(obj, "jumpChain");
                if (jumpChainStr != null)
                {
                    var hopsStr = ExtractArray(jumpChainStr, "hops");
                    if (hopsStr != null)
                    {
                        var hops = new List<JumpHop>();
                        var hopObjects = SplitJsonObjects(hopsStr);
                        foreach (var hopObj in hopObjects)
                        {
                            hops.Add(new JumpHop
                            {
                                Host = UnprotectField(ExtractString(hopObj, "host")),
                                Port = ExtractInt(hopObj, "port"),
                                Username = UnprotectField(ExtractString(hopObj, "username")),
                                CredentialRefId = ExtractString(hopObj, "credentialRefId")
                            });
                        }
                        conn.JumpChain = new JumpChainConfig { Hops = hops };
                    }
                }

                // 隧道
                var tunnelStr = ExtractObject(obj, "tunnel");
                if (tunnelStr != null)
                {
                    conn.Tunnel = new TunnelConfig
                    {
                        Type = ParseEnum<Gdterm.Core.Enums.TunnelType>(ExtractString(tunnelStr, "type")),
                        LocalPort = ExtractInt(tunnelStr, "localPort"),
                        RemoteHost = ExtractString(tunnelStr, "remoteHost"),
                        RemotePort = ExtractInt(tunnelStr, "remotePort")
                    };
                }

                // 代理（缺省 null = 直连，兼容旧文件）
                var proxyStr = ExtractObject(obj, "proxy");
                if (proxyStr != null)
                {
                    conn.Proxy = new Gdterm.Core.Models.ProxyConfig
                    {
                        Kind = ParseEnum<Gdterm.Core.Models.ProxyKind>(ExtractString(proxyStr, "kind")),
                        Host = UnprotectField(ExtractString(proxyStr, "host")),
                        Port = ExtractInt(proxyStr, "port"),
                        Username = UnprotectField(ExtractString(proxyStr, "username")),
                        Password = UnprotectField(ExtractString(proxyStr, "password"))
                    };
                    if (conn.Proxy.Kind == Gdterm.Core.Models.ProxyKind.None) conn.Proxy = null;
                    else if (conn.Proxy.Port <= 0)
                        conn.Proxy.Port = Gdterm.Core.Models.ProxyConfig.DefaultPort(conn.Proxy.Kind);
                }

                // 串口参数（0.1.119 前从未落盘，缺失时保持 null）
                var serialStr = ExtractObject(obj, "serial");
                if (serialStr != null)
                {
                    conn.Serial = new SerialConfig
                    {
                        PortName = ExtractString(serialStr, "portName") ?? "COM1",
                        BaudRate = ExtractInt(serialStr, "baudRate"),
                        DataBits = ExtractInt(serialStr, "dataBits"),
                        StopBits = ParseEnum<System.IO.Ports.StopBits>(ExtractString(serialStr, "stopBits")),
                        Parity = ParseEnum<System.IO.Ports.Parity>(ExtractString(serialStr, "parity")),
                        Handshake = ParseEnum<System.IO.Ports.Handshake>(ExtractString(serialStr, "handshake"))
                    };
                    if (conn.Serial.BaudRate <= 0) conn.Serial.BaudRate = 9600;
                    if (conn.Serial.DataBits < 5 || conn.Serial.DataBits > 8) conn.Serial.DataBits = 8;
                }

                // 扩展字段（notes/RDP 选项等）——0.1.119 前写入后从不读回，导致“保存无效”
                var metadataStr = ExtractObject(obj, "metadata");
                if (!string.IsNullOrEmpty(metadataStr))
                {
                    conn.Metadata = ParseFlatStringObject(metadataStr);
                }

                result.Add(conn);
            }

            return result;
        }

        private T ParseEnum<T>(string value) where T : struct
        {
            if (string.IsNullOrEmpty(value)) return default(T);
            T result;
            if (Enum.TryParse(value, true, out result))
                return result;
            return default(T);
        }

        /// <summary>
        /// 解析扁平字符串对象内容（无嵌套，形如 "k1": "v1", "k2": "v2"），用于 metadata。
        /// </summary>
        private static Dictionary<string, string> ParseFlatStringObject(string objectContent)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(objectContent)) return dict;

            int i = 0;
            int len = objectContent.Length;
            while (i < len)
            {
                // 跳过分隔符与空白，定位键的起始引号
                while (i < len && (char.IsWhiteSpace(objectContent[i]) || objectContent[i] == ',')) i++;
                if (i >= len || objectContent[i] != '"') break;

                var key = ReadQuotedString(objectContent, ref i);

                // 定位值
                while (i < len && (char.IsWhiteSpace(objectContent[i]) || objectContent[i] == ':')) i++;
                if (i >= len || objectContent[i] != '"') break;

                var val = ReadQuotedString(objectContent, ref i);
                if (!string.IsNullOrEmpty(key)) dict[key] = val ?? "";
            }

            return dict;
        }

        /// <summary>从 index 处的引号读取转义字符串，index 推进到结束引号之后。</summary>
        private static string ReadQuotedString(string json, ref int index)
        {
            // 进入时 json[index] 应为 '"'
            if (json == null || index < 0 || index >= json.Length || json[index] != '"') return null;
            // 审计 F1：反转义必须与 Escape 成对（原来只认 \" 和 \\，\n \r \t \uXXXX 会被错读成字面 n/r/t）
            var end = Gdterm.Core.JsonText.SkipString(json, index);
            var raw = json.Substring(index + 1, end - index - 1);
            index = end + 1;
            return Gdterm.Core.JsonText.Unescape(raw);
        }

        private string ExtractString(string json, string key)
        {
            // 审计 F1：结束引号必须跳过转义（原实现用 json[end-1] != '\\' 判定，
            // 在值为 …\\" 时会多扫到下一个键），反转义也统一到 JsonText。
            return Gdterm.Core.JsonText.ExtractString(json, key);
        }

        private string Unescape(string value)
        {
            if (value == null) return null;
            return Gdterm.Core.JsonText.Unescape(value);
        }

        private int ExtractInt(string json, string key)
        {
            var pattern = $"\"{key}\"";
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

        private string ExtractObject(string json, string key)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return null;

            var braceIdx = json.IndexOf('{', idx + pattern.Length);
            if (braceIdx < 0) return null;

            var end = FindMatchingBracket(json, braceIdx);
            if (end < 0) return null;

            return json.Substring(braceIdx + 1, end - braceIdx - 1);
        }

        private string ExtractArray(string json, string key)
        {
            var pattern = $"\"{key}\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return null;

            var bracketIdx = json.IndexOf('[', idx + pattern.Length);
            if (bracketIdx < 0) return null;

            var end = FindMatchingBracket(json, bracketIdx);
            if (end < 0) return null;

            return json.Substring(bracketIdx + 1, end - bracketIdx - 1);
        }

        private List<string> SplitJsonObjects(string arrayContent)
        {
            var result = new List<string>();
            // 审计 F1：切对象走 JsonText（括号计数跳过字符串）。返回值约定与前实现一致：不含首尾花括号。
            foreach (var obj in Gdterm.Core.JsonText.SplitObjects(arrayContent))
                result.Add(obj.Substring(1, obj.Length - 2));
            return result;
        }

        private int FindMatchingBracket(string json, int openIndex)
        {
            // 审计 F1：原实现不跳过字符串，值里出现 } / ] 就会提前截断；统一走 JsonText
            return Gdterm.Core.JsonText.FindMatchingBracket(json, openIndex);
        }
    }
}
