using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 连接模板持久化存储
    /// </summary>
    public class TemplateStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();

        public TemplateStore(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        }

        public List<ConnectionTemplate> LoadAll()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    var defaults = GetDefaults();
                    SaveAll(defaults);
                    return defaults;
                }
                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                return ParseTemplates(json);
            }
        }

        public void SaveAll(List<ConnectionTemplate> templates)
        {
            lock (_lock)
            {
                var json = SerializeTemplates(templates);
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
        }

        public void Add(ConnectionTemplate template)
        {
            var all = LoadAll();
            all.Add(template);
            SaveAll(all);
        }

        public bool Delete(string id)
        {
            var all = LoadAll();
            var item = all.FirstOrDefault(t => t.Id == id);
            if (item == null || item.IsBuiltIn) return false;
            all.Remove(item);
            SaveAll(all);
            return true;
        }

        public bool Update(ConnectionTemplate template)
        {
            var all = LoadAll();
            var index = all.FindIndex(t => t.Id == template.Id);
            if (index < 0) return false;
            all[index] = template;
            SaveAll(all);
            return true;
        }

        /// <summary>通过模板快速创建 ConnectionConfig</summary>
        public ConnectionConfig CreateConnection(ConnectionTemplate template, string host, string name = null)
        {
            return new ConnectionConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name ?? $"{template.Name} - {host}",
                Host = host,
                Port = template.DefaultPort,
                Protocol = template.Protocol,
                Username = template.DefaultUsername,
                GroupPath = template.DefaultGroupPath,
                Tunnel = template.TunnelTemplate != null ? new TunnelConfig
                {
                    JumpChain = template.TunnelTemplate.JumpChain?.Select(h => new JumpHop
                    {
                        Host = h.Host,
                        Port = h.Port,
                        Username = h.Username
                    }).ToList()
                } : null
            };
        }

        // ====== 内置模板 ======

        private List<ConnectionTemplate> GetDefaults()
        {
            return new List<ConnectionTemplate>
            {
                new ConnectionTemplate { Id = "tpl-ssh-linux", Name = "Linux SSH", Description = "标准 Linux SSH 连接", Protocol = ProtocolType.SSH, DefaultPort = 22, DefaultUsername = "root", OsType = "Linux", Icon = "server-linux", IsBuiltIn = true },
                new ConnectionTemplate { Id = "tpl-ssh-jump", Name = "跳板机 SSH", Description = "通过跳板机的 SSH 连接", Protocol = ProtocolType.SSH, DefaultPort = 22, DefaultUsername = "root", RequiresTunnel = true, Icon = "server-jump", IsBuiltIn = true },
                new ConnectionTemplate { Id = "tpl-rdp-windows", Name = "Windows RDP", Description = "Windows 远程桌面", Protocol = ProtocolType.RDP, DefaultPort = 3389, DefaultUsername = "Administrator", Icon = "monitor", IsBuiltIn = true },
                new ConnectionTemplate { Id = "tpl-serial", Name = "串口设备", Description = "串口终端连接", Protocol = ProtocolType.Serial, DefaultPort = 0, Icon = "serial", IsBuiltIn = true },
                new ConnectionTemplate { Id = "tpl-ssh-centos", Name = "CentOS SSH", Description = "CentOS/RHEL SSH", Protocol = ProtocolType.SSH, DefaultPort = 22, DefaultUsername = "root", OsType = "CentOS", Icon = "server-linux", IsBuiltIn = true },
                new ConnectionTemplate { Id = "tpl-ssh-ubuntu", Name = "Ubuntu SSH", Description = "Ubuntu/Debian SSH", Protocol = ProtocolType.SSH, DefaultPort = 22, DefaultUsername = "ubuntu", OsType = "Ubuntu", Icon = "server-linux", IsBuiltIn = true }
            };
        }

        // ====== JSON 序列化 ======

        private string SerializeTemplates(List<ConnectionTemplate> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{\"templates\":[");
            for (int i = 0; i < items.Count; i++)
            {
                var t = items[i];
                if (i > 0) sb.AppendLine(",");
                sb.Append("  {");
                sb.AppendFormat("\"id\":\"{0}\"", Esc(t.Id));
                sb.AppendFormat(",\"name\":\"{0}\"", Esc(t.Name));
                sb.AppendFormat(",\"description\":\"{0}\"", Esc(t.Description ?? ""));
                sb.AppendFormat(",\"protocol\":{0}", (int)t.Protocol);
                sb.AppendFormat(",\"defaultPort\":{0}", t.DefaultPort);
                sb.AppendFormat(",\"defaultUsername\":\"{0}\"", Esc(t.DefaultUsername ?? ""));
                sb.AppendFormat(",\"defaultGroupPath\":\"{0}\"", Esc(t.DefaultGroupPath ?? ""));
                sb.AppendFormat(",\"requiresTunnel\":{0}", t.RequiresTunnel ? "true" : "false");
                if (!string.IsNullOrEmpty(t.OsType))
                    sb.AppendFormat(",\"osType\":\"{0}\"", Esc(t.OsType));
                if (!string.IsNullOrEmpty(t.Icon))
                    sb.AppendFormat(",\"icon\":\"{0}\"", Esc(t.Icon));
                sb.AppendFormat(",\"isBuiltIn\":{0}", t.IsBuiltIn ? "true" : "false");
                sb.Append("}");
            }
            sb.AppendLine("\n]}");
            return sb.ToString();
        }

        private List<ConnectionTemplate> ParseTemplates(string json)
        {
            var result = new List<ConnectionTemplate>();
            var start = json.IndexOf("\"templates\"");
            if (start < 0) return result;
            var arr = json.IndexOf('[', start);
            if (arr < 0) return result;
            int depth = 0; int objStart = -1;
            for (int i = arr; i < json.Length; i++)
            {
                if (json[i] == '{') { if (depth == 0) objStart = i; depth++; }
                else if (json[i] == '}') { depth--; if (depth == 0) { var obj = json.Substring(objStart, i - objStart + 1); result.Add(ParseTemplate(obj)); } }
            }
            return result;
        }

        private ConnectionTemplate ParseTemplate(string obj)
        {
            var protocolStr = ExtractJsonString(obj, "protocol");
            int.TryParse(protocolStr, out var protocolInt);
            return new ConnectionTemplate
            {
                Id = ExtractJsonString(obj, "id"),
                Name = ExtractJsonString(obj, "name"),
                Description = ExtractJsonString(obj, "description"),
                Protocol = (ProtocolType)protocolInt,
                DefaultPort = ExtractJsonInt(obj, "defaultPort"),
                DefaultUsername = ExtractJsonString(obj, "defaultUsername"),
                DefaultGroupPath = ExtractJsonString(obj, "defaultGroupPath"),
                RequiresTunnel = ExtractJsonBool(obj, "requiresTunnel"),
                OsType = ExtractJsonString(obj, "osType"),
                Icon = ExtractJsonString(obj, "icon"),
                IsBuiltIn = ExtractJsonBool(obj, "isBuiltIn")
            };
        }

        private string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? "";
        private string ExtractJsonString(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return null;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return null;
            var s = json.IndexOf('"', c + 1); if (s < 0) return null;
            var e = s + 1; while (e < json.Length) { if (json[e] == '\\') { e += 2; continue; } if (json[e] == '"') break; e++; }
            return json.Substring(s + 1, e - s - 1).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        private int ExtractJsonInt(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return 0;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return 0;
            var s = c + 1; while (s < json.Length && json[s] == ' ') s++; var e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            int.TryParse(json.Substring(s, e - s), out var v); return v;
        }
        private bool ExtractJsonBool(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return false;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return false;
            return json.IndexOf("true", c) == c + 1;
        }
    }
}
