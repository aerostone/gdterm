using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdterm.Connections
{
    using Gdterm.Core.Models;

    /// <summary>
    /// 命令模板——跨会话命令模板库，支持变量替换
    /// </summary>
    public class CommandTemplate
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Command { get; set; }
        public string Category { get; set; }
        public List<string> Tags { get; set; }
        public DateTime CreatedAt { get; set; }
        public int UseCount { get; set; }

        public CommandTemplate()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            Tags = new List<string>();
            CreatedAt = DateTime.Now;
        }

        /// <summary>执行变量替换</summary>
        public string ResolveCommand(CommandTemplateContext context)
        {
            if (context == null || string.IsNullOrEmpty(Command)) return Command;

            var result = Command;
            result = result.Replace("{host}", context.HostName ?? "");
            result = result.Replace("{user}", context.UserName ?? "");
            result = result.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"));
            result = result.Replace("{time}", DateTime.Now.ToString("HH:mm:ss"));
            result = result.Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            result = result.Replace("{prompt}", context.Prompt ?? "");
            result = result.Replace("{port}", context.Port.ToString());
            result = result.Replace("{os}", context.OsType ?? "");

            // 环境变量替换 {env:VAR_NAME}
            if (context.EnvironmentVars != null)
            {
                foreach (var kvp in context.EnvironmentVars)
                {
                    result = result.Replace("{env:" + kvp.Key + "}", kvp.Value);
                }
            }

            return result;
        }
    }

    /// <summary>命令模板上下文（用于变量替换）</summary>
    public class CommandTemplateContext
    {
        public string HostName { get; set; }
        public string UserName { get; set; }
        public int Port { get; set; }
        public string OsType { get; set; }
        public string Prompt { get; set; }
        public Dictionary<string, string> EnvironmentVars { get; set; }
    }

    /// <summary>命令模板存储</summary>
    public class CommandTemplateStore : IDisposable
    {
        private readonly string _filePath;
        private List<CommandTemplate> _templates;

        public CommandTemplateStore(string filePath)
        {
            _filePath = filePath;
            _templates = new List<CommandTemplate>();
        }

        public void Load()
        {
            if (!File.Exists(_filePath))
            {
                _templates = GetBuiltInTemplates();
                Save();
                return;
            }

            _templates = new List<CommandTemplate>();
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var objects = SplitJsonObjects(ExtractArray(json, "templates"));
            foreach (var obj in objects)
            {
                var t = new CommandTemplate
                {
                    Id = ExtractString(obj, "id") ?? "",
                    Name = ExtractString(obj, "name") ?? "",
                    Description = ExtractString(obj, "description") ?? "",
                    Command = ExtractString(obj, "command") ?? "",
                    Category = ExtractString(obj, "category") ?? "通用",
                    UseCount = ExtractInt(obj, "useCount")
                };
                var tags = ExtractArray(obj, "tags");
                if (!string.IsNullOrEmpty(tags))
                {
                    foreach (var tag in SplitJsonStrings(tags))
                        t.Tags.Add(tag);
                }
                _templates.Add(t);
            }

            if (_templates.Count == 0)
            {
                _templates = GetBuiltInTemplates();
                Save();
            }
        }

        public void Save()
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append("{\"templates\":[");
            for (int i = 0; i < _templates.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var t = _templates[i];
                sb.Append("{\"id\":\"").Append(Esc(t.Id)).Append('"');
                sb.Append(",\"name\":\"").Append(Esc(t.Name)).Append('"');
                sb.Append(",\"description\":\"").Append(Esc(t.Description)).Append('"');
                sb.Append(",\"command\":\"").Append(Esc(t.Command)).Append('"');
                sb.Append(",\"category\":\"").Append(Esc(t.Category)).Append('"');
                sb.Append(",\"useCount\":").Append(t.UseCount);
                sb.Append(",\"tags\":[");
                for (int j = 0; j < t.Tags.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(t.Tags[j])).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            File.WriteAllText(_filePath, sb.ToString(), Encoding.UTF8);
        }

        public IList<CommandTemplate> GetAll() { return _templates.AsReadOnly(); }

        public CommandTemplate GetById(string id)
        {
            return _templates.Find(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public IList<CommandTemplate> GetByCategory(string category)
        {
            return _templates.FindAll(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)).AsReadOnly();
        }

        public IList<string> GetCategories()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _templates) set.Add(t.Category);
            return new List<string>(set);
        }

        public void Add(CommandTemplate template)
        {
            _templates.Add(template);
            Save();
        }

        public void Update(CommandTemplate template)
        {
            var idx = _templates.FindIndex(t => string.Equals(t.Id, template.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { _templates[idx] = template; Save(); }
        }

        public void Delete(string id)
        {
            _templates.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        /// <summary>从历史命令创建模板</summary>
        public CommandTemplate CreateFromHistory(string command, string name = null)
        {
            return new CommandTemplate
            {
                Name = name ?? command.Substring(0, Math.Min(command.Length, 40)),
                Command = command,
                Category = "从历史创建"
            };
        }

        private static List<CommandTemplate> GetBuiltInTemplates()
        {
            return new List<CommandTemplate>
            {
                new CommandTemplate { Name = "磁盘空间检查", Command = "df -h", Category = "系统巡检", Description = "检查磁盘使用率", Tags = { "巡检", "磁盘" } },
                new CommandTemplate { Name = "内存使用检查", Command = "free -h && cat /proc/meminfo | head -5", Category = "系统巡检", Description = "检查内存使用", Tags = { "巡检", "内存" } },
                new CommandTemplate { Name = "系统负载检查", Command = "uptime && top -bn1 | head -15", Category = "系统巡检", Description = "检查系统负载和进程", Tags = { "巡检", "负载" } },
                new CommandTemplate { Name = "网络连接检查", Command = "ss -tlnp && echo '---' && ip addr show", Category = "网络", Description = "检查监听端口和IP", Tags = { "网络", "端口" } },
                new CommandTemplate { Name = "防火墙规则查看", Command = "iptables -L -n --line-numbers 2>/dev/null || firewall-cmd --list-all 2>/dev/null", Category = "安全", Description = "查看防火墙规则", Tags = { "安全", "防火墙" } },
                new CommandTemplate { Name = "系统信息概览", Command = "echo '=== {host} ===' && uname -a && echo && cat /etc/os-release 2>/dev/null | head -3 && echo && uptime && echo && df -h /", Category = "系统巡检", Description = "一次性查看系统关键信息", Tags = { "巡检", "概览" } },
                new CommandTemplate { Name = "Docker容器状态", Command = "docker ps -a --format 'table {{.Names}}\\t{{.Status}}\\t{{.Ports}}'", Category = "Docker", Description = "查看所有Docker容器状态", Tags = { "Docker" } },
                new CommandTemplate { Name = "日志查看(最近100行)", Command = "tail -100 /var/log/syslog 2>/dev/null || tail -100 /var/log/messages", Category = "日志", Description = "查看系统日志最近100行", Tags = { "日志" } },
                new CommandTemplate { Name = "备份目录(带日期)", Command = "tar czf /tmp/backup_{date}.tar.gz {prompt}", Category = "备份", Description = "压缩备份指定目录", Tags = { "备份", "压缩" } },
                new CommandTemplate { Name = "进程查找", Command = "ps aux | grep -i '{prompt}' | grep -v grep", Category = "进程", Description = "按关键词查找进程", Tags = { "进程" } }
            };
        }

        // ── JSON 辅助（复用无外部库约束） ──
        private static string Esc(string s) { return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") ?? ""; }

        private static string ExtractString(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return null;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return null;
            var s = json.IndexOf('"', c + 1); if (s < 0) return null;
            var e = s + 1; while (e < json.Length) { if (json[e] == '\\') { e += 2; continue; } if (json[e] == '"') break; e++; }
            return json.Substring(s + 1, e - s - 1).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static int ExtractInt(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return 0;
            var c = json.IndexOf(':', idx + p.Length); if (c < 0) return 0;
            var s = c + 1; while (s < json.Length && json[s] == ' ') s++; var e = s;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
            int v; int.TryParse(json.Substring(s, e - s), out v); return v;
        }

        private static string ExtractArray(string json, string key)
        {
            var p = "\"" + key + "\""; var idx = json.IndexOf(p); if (idx < 0) return "";
            var s = json.IndexOf('[', idx); if (s < 0) return "";
            int depth = 0; int end = s;
            for (int i = s; i < json.Length; i++) { if (json[i] == '[') depth++; else if (json[i] == ']') { depth--; if (depth == 0) { end = i; break; } } }
            return json.Substring(s + 1, end - s - 1);
        }

        private static List<string> SplitJsonObjects(string content)
        {
            var result = new List<string>();
            int depth = 0; int start = -1;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '{') { if (depth == 0) start = i; depth++; }
                else if (content[i] == '}') { depth--; if (depth == 0 && start >= 0) { result.Add(content.Substring(start, i - start + 1)); start = -1; } }
            }
            return result;
        }

        private static List<string> SplitJsonStrings(string content)
        {
            var result = new List<string>();
            int i = 0;
            while (i < content.Length)
            {
                if (content[i] == '"')
                {
                    var end = content.IndexOf('"', i + 1);
                    if (end < 0) break;
                    result.Add(content.Substring(i + 1, end - i - 1));
                    i = end + 1;
                }
                else i++;
            }
            return result;
        }

        public void Dispose() { _templates.Clear(); }
    }
}
