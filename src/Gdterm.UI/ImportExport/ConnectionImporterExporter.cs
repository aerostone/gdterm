using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;

namespace Gdterm.UI.ImportExport
{
    /// <summary>
    /// 连接导入/导出——支持 JSON (gdterm 原生) 和 CSV 格式
    /// 兼容 mRemoteNG XML 和 SecureCRT CSV 格式
    /// </summary>
    public static class ConnectionImporterExporter
    {
        // ========== 导出 ==========

        /// <summary>
        /// 导出为 gdterm JSON 格式
        /// </summary>
        public static void ExportAsJson(IEnumerable<ConnectionConfig> connections, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");
            var first = true;
            foreach (var c in connections)
            {
                if (!first) sb.AppendLine(",");
                first = false;
                sb.Append("  {");
                sb.AppendFormat("\n    \"id\": \"{0}\",", EscapeJson(c.Id));
                sb.AppendFormat("\n    \"name\": \"{0}\",", EscapeJson(c.Name));
                sb.AppendFormat("\n    \"protocol\": \"{0}\",", c.Protocol);
                sb.AppendFormat("\n    \"host\": \"{0}\",", EscapeJson(c.Host));
                sb.AppendFormat("\n    \"port\": {0},", c.Port);
                sb.AppendFormat("\n    \"username\": \"{0}\",", EscapeJson(c.Username ?? ""));
                sb.AppendFormat("\n    \"domain\": \"{0}\",", EscapeJson(c.Domain ?? ""));
                sb.AppendFormat("\n    \"groupPath\": \"{0}\"", EscapeJson(c.GroupPath ?? ""));
                sb.Append("\n  }");
            }
            sb.AppendLine("\n]");
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// 导出为 CSV 格式（兼容 Excel 和 mRemoteNG）
        /// </summary>
        public static void ExportAsCsv(IEnumerable<ConnectionConfig> connections, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Protocol,Host,Port,Username,Domain,GroupPath,Notes");
            foreach (var c in connections)
            {
                sb.AppendFormat("{0},{1},{2},{3},{4},{5},{6},{7}",
                    CsvEscape(c.Name),
                    c.Protocol,
                    CsvEscape(c.Host),
                    c.Port,
                    CsvEscape(c.Username ?? ""),
                    CsvEscape(c.Domain ?? ""),
                    CsvEscape(c.GroupPath ?? ""),
                    CsvEscape(GetNotes(c)));
                sb.AppendLine();
            }
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        // ========== 导入 ==========

        /// <summary>
        /// 从文件导入连接（自动识别 JSON/CSV）
        /// </summary>
        public static List<ConnectionConfig> ImportFromFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            switch (ext)
            {
                case ".json": return ImportFromJson(filePath);
                case ".csv": return ImportFromCsv(filePath);
                case ".xml": return ImportFromMRemoteNgXml(filePath);
                default:
                    // 尝试按 JSON 解析
                    try { return ImportFromJson(filePath); }
                    catch { return ImportFromCsv(filePath); }
            }
        }

        /// <summary>
        /// 从 gdterm JSON 导入
        /// </summary>
        public static List<ConnectionConfig> ImportFromJson(string filePath)
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var result = new List<ConnectionConfig>();
            int pos = json.IndexOf('[');
            if (pos < 0) return result;

            // 简单逐对象解析
            int objStart = json.IndexOf('{', pos);
            while (objStart >= 0)
            {
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;
                var obj = json.Substring(objStart, objEnd - objStart + 1);
                var config = ParseJsonObject(obj);
                if (config != null) result.Add(config);
                objStart = json.IndexOf('{', objEnd + 1);
            }
            return result;
        }

        /// <summary>
        /// 从 CSV 导入（兼容多种格式）
        /// </summary>
        public static List<ConnectionConfig> ImportFromCsv(string filePath)
        {
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var result = new List<ConnectionConfig>();
            if (lines.Length < 2) return result;

            // 解析表头，确定列索引
            var headers = ParseCsvLine(lines[0]);
            var nameIdx = FindColumn(headers, "Name", "DisplayName", "连接名称");
            var protoIdx = FindColumn(headers, "Protocol", "ProtocolType", "协议");
            var hostIdx = FindColumn(headers, "Host", "Hostname", "主机", "Server");
            var portIdx = FindColumn(headers, "Port", "端口");
            var userIdx = FindColumn(headers, "Username", "User", "用户名");
            var groupIdx = FindColumn(headers, "GroupPath", "Folder", "分组", "FolderName");
            var domainIdx = FindColumn(headers, "Domain", "域名");

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cols = ParseCsvLine(lines[i]);
                var config = new ConnectionConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = GetCsvCol(cols, nameIdx, $"Connection {i}"),
                    Protocol = ParseProtocol(GetCsvCol(cols, protoIdx, "SSH")),
                    Host = GetCsvCol(cols, hostIdx, ""),
                    Port = ParsePort(GetCsvCol(cols, portIdx, ""), ParseProtocol(GetCsvCol(cols, protoIdx, "SSH"))),
                    Username = GetCsvCol(cols, userIdx, ""),
                    Domain = GetCsvCol(cols, domainIdx, ""),
                    GroupPath = GetCsvCol(cols, groupIdx, "")
                };
                if (!string.IsNullOrEmpty(config.Host))
                    result.Add(config);
            }
            return result;
        }

        /// <summary>
        /// 从 mRemoteNG XML 导入
        /// </summary>
        public static List<ConnectionConfig> ImportFromMRemoteNgXml(string filePath)
        {
            var xml = File.ReadAllText(filePath, Encoding.UTF8);
            var result = new List<ConnectionConfig>();
            // mRemoteNG 格式: <Node Name="..." Type="RDP" Hostname="..." Port="..." Username="..." />
            int nodeStart = xml.IndexOf("<Node ", StringComparison.OrdinalIgnoreCase);
            while (nodeStart >= 0)
            {
                int nodeEnd = xml.IndexOf("/>", nodeStart);
                if (nodeEnd < 0) nodeEnd = xml.IndexOf(">", nodeStart);
                if (nodeEnd < 0) break;
                var nodeXml = xml.Substring(nodeStart, nodeEnd - nodeStart + 2);

                var config = new ConnectionConfig
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = ExtractXmlAttribute(nodeXml, "Name"),
                    Protocol = ParseProtocol(ExtractXmlAttribute(nodeXml, "Type")),
                    Host = ExtractXmlAttribute(nodeXml, "Hostname"),
                    Username = ExtractXmlAttribute(nodeXml, "Username"),
                    Domain = ExtractXmlAttribute(nodeXml, "Domain")
                };
                var portStr = ExtractXmlAttribute(nodeXml, "Port");
                config.Port = string.IsNullOrEmpty(portStr) ? (config.Protocol == ProtocolType.Rdp ? 3389 : 22) : int.Parse(portStr);
                var folder = ExtractXmlAttribute(nodeXml, "Folder");
                config.GroupPath = folder ?? "";

                if (!string.IsNullOrEmpty(config.Host))
                    result.Add(config);

                nodeStart = xml.IndexOf("<Node ", nodeEnd, StringComparison.OrdinalIgnoreCase);
            }
            return result;
        }

        // ========== 合并策略 ==========

        /// <summary>
        /// 合并导入的连接到现有列表（按 Host+Port+Protocol 去重）
        /// </summary>
        public static MergeResult MergeConnections(
            IEnumerable<ConnectionConfig> existing,
            IEnumerable<ConnectionConfig> imported)
        {
            var result = new MergeResult();
            var existingSet = new HashSet<string>(
                existing.Select(c => $"{c.Host}:{c.Port}:{c.Protocol}"),
                StringComparer.OrdinalIgnoreCase);

            foreach (var conn in imported)
            {
                var key = $"{conn.Host}:{conn.Port}:{conn.Protocol}";
                if (existingSet.Contains(key))
                {
                    result.Duplicates.Add(conn);
                }
                else
                {
                    result.NewConnections.Add(conn);
                    existingSet.Add(key);
                }
            }
            return result;
        }

        // ========== 辅助方法 ==========

        private static ConnectionConfig ParseJsonObject(string obj)
        {
            var config = new ConnectionConfig
            {
                Id = ExtractJsonString(obj, "id") ?? Guid.NewGuid().ToString("N"),
                Name = ExtractJsonString(obj, "name"),
                Host = ExtractJsonString(obj, "host"),
                Username = ExtractJsonString(obj, "username"),
                Domain = ExtractJsonString(obj, "domain"),
                GroupPath = ExtractJsonString(obj, "groupPath")
            };
            var protoStr = ExtractJsonString(obj, "protocol");
            config.Protocol = ParseProtocol(protoStr);
            var portStr = ExtractJsonString(obj, "port");
            config.Port = string.IsNullOrEmpty(portStr) ? (config.Protocol == ProtocolType.Rdp ? 3389 : 22) : int.Parse(portStr);
            return config;
        }

        private static ProtocolType ParseProtocol(string s)
        {
            if (string.IsNullOrEmpty(s)) return ProtocolType.Ssh;
            s = s.Trim().ToUpperInvariant();
            if (s.Contains("RDP") || s.Contains("RDP")) return ProtocolType.Rdp;
            if (s.Contains("SERIAL") || s.Contains("COM")) return ProtocolType.Serial;
            return ProtocolType.Ssh;
        }

        private static int ParsePort(string s, ProtocolType proto)
        {
            if (int.TryParse(s, out int port)) return port;
            return proto == ProtocolType.Rdp ? 3389 : proto == ProtocolType.Ssh ? 22 : 9600;
        }

        private static string ExtractJsonString(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int quoteStart = json.IndexOf('"', colon + 1);
            if (quoteStart < 0) return null;
            int quoteEnd = quoteStart + 1;
            while (quoteEnd < json.Length)
            {
                if (json[quoteEnd] == '"' && json[quoteEnd - 1] != '\\') break;
                quoteEnd++;
            }
            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Replace("\\\"", "\"");
        }

        private static string ExtractXmlAttribute(string xml, string attrName)
        {
            var pattern = attrName + "=\"";
            int idx = xml.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int start = idx + pattern.Length;
            int end = xml.IndexOf('"', start);
            if (end < 0) return null;
            return xml.Substring(start, end - start);
        }

        private static int FindMatchingBrace(string json, int openPos)
        {
            int depth = 0;
            for (int i = openPos; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                if (depth == 0) return i;
            }
            return -1;
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();
            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            result.Add(current.ToString());
            return result.ToArray();
        }

        private static int FindColumn(string[] headers, params string[] names)
        {
            for (int i = 0; i < headers.Length; i++)
                foreach (var name in names)
                    if (headers[i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return i;
            return -1;
        }

        private static string GetCsvCol(string[] cols, int idx, string defaultVal)
        {
            if (idx < 0 || idx >= cols.Length) return defaultVal;
            var val = cols[idx].Trim();
            return string.IsNullOrEmpty(val) ? defaultVal : val;
        }

        private static string GetNotes(ConnectionConfig c)
        {
            if (c.Metadata != null && c.Metadata.ContainsKey("notes"))
                return c.Metadata["notes"];
            return "";
        }

        private static string CsvEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }

    /// <summary>
    /// 合并结果
    /// </summary>
    public class MergeResult
    {
        public List<ConnectionConfig> NewConnections { get; set; } = new List<ConnectionConfig>();
        public List<ConnectionConfig> Duplicates { get; set; } = new List<ConnectionConfig>();
    }
}
