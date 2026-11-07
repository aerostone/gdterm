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
    /// 快捷命令持久化存储
    /// </summary>
    public class QuickCommandStore
    {
        private readonly string _filePath;
        private readonly object _lock = new object();

        public QuickCommandStore(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        }

        /// <summary>加载所有快捷命令</summary>
        public List<QuickCommand> LoadAll()
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
                return ParseQuickCommands(json);
            }
        }

        /// <summary>保存所有快捷命令</summary>
        public void SaveAll(List<QuickCommand> commands)
        {
            lock (_lock)
            {
                var json = SerializeQuickCommands(commands);
                File.WriteAllText(_filePath, json, Encoding.UTF8);
            }
        }

        /// <summary>添加命令</summary>
        public void Add(QuickCommand cmd)
        {
            var all = LoadAll();
            all.Add(cmd);
            SaveAll(all);
        }

        /// <summary>删除命令</summary>
        public bool Delete(string id)
        {
            var all = LoadAll();
            var removed = all.RemoveAll(c => c.Id == id);
            if (removed > 0) SaveAll(all);
            return removed > 0;
        }

        /// <summary>更新命令</summary>
        public bool Update(QuickCommand cmd)
        {
            var all = LoadAll();
            var index = all.FindIndex(c => c.Id == cmd.Id);
            if (index < 0) return false;
            all[index] = cmd;
            SaveAll(all);
            return true;
        }

        // ====== 默认命令集 ======

        private List<QuickCommand> GetDefaults()
        {
            return new List<QuickCommand>
            {
                Create(L10n.T("网络"), L10n.T("查看 IP 地址"), "ip addr show", L10n.T("网络配置")),
                Create(L10n.T("网络"), L10n.T("查看路由表"), "ip route show", L10n.T("网络配置")),
                Create(L10n.T("网络"), L10n.T("查看监听端口"), "ss -tlnp", L10n.T("网络诊断")),
                Create(L10n.T("网络"), L10n.T("查看网络连接"), "ss -s", L10n.T("连接统计")),
                Create(L10n.T("网络"), L10n.T("DNS 解析测试"), "nslookup {host}", L10n.T("DNS 诊断")),
                Create(L10n.T("网络"), L10n.T("Ping 测试"), "ping -c 4 {host}", L10n.T("连通性")),
                Create(L10n.T("网络"), "Traceroute", "traceroute {host}", L10n.T("路由追踪")),
                Create(L10n.T("网络"), L10n.T("防火墙规则"), "iptables -L -n", L10n.T("防火墙")),
                Create(L10n.T("磁盘"), L10n.T("磁盘使用"), "df -h", L10n.T("磁盘空间")),
                Create(L10n.T("磁盘"), L10n.T("目录大小"), "du -sh /* | sort -rh | head -20", L10n.T("空间分析")),
                Create(L10n.T("磁盘"), L10n.T("Inode 使用"), "df -i", L10n.T("Inode 检查")),
                Create(L10n.T("磁盘"), L10n.T("挂载信息"), "mount | column -t", L10n.T("挂载点")),
                Create(L10n.T("进程"), L10n.T("进程列表"), "ps aux --sort=-%mem | head -20", L10n.T("内存排序")),
                Create(L10n.T("进程"), L10n.T("CPU 使用 Top"), "top -bn1 | head -20", L10n.T("CPU 监控")),
                Create(L10n.T("进程"), L10n.T("内存使用"), "free -h", L10n.T("内存统计")),
                Create(L10n.T("进程"), L10n.T("系统负载"), "uptime", L10n.T("负载信息")),
                Create(L10n.T("进程"), L10n.T("僵尸进程"), "ps aux | grep -w Z", L10n.T("进程清理")),
                Create(L10n.T("系统"), L10n.T("系统信息"), "uname -a", L10n.T("系统版本")),
                Create(L10n.T("系统"), L10n.T("内核版本"), "cat /etc/os-release", L10n.T("发行版")),
                Create(L10n.T("系统"), L10n.T("登录用户"), "who", L10n.T("会话信息")),
                Create(L10n.T("系统"), L10n.T("系统日志"), "journalctl -n 50 --no-pager", L10n.T("日志查看")),
                Create(L10n.T("系统"), L10n.T("计划任务"), "crontab -l", L10n.T("定时任务")),
                Create(L10n.T("系统"), L10n.T("服务状态"), "systemctl list-units --state=failed", L10n.T("故障服务")),
                Create(L10n.T("安全"), L10n.T("最近登录"), "last -n 20", L10n.T("登录审计")),
                Create(L10n.T("安全"), L10n.T("失败登录"), "lastb -n 20 2>/dev/null || echo '需要 root'", L10n.T("安全审计")),
                Create(L10n.T("安全"), L10n.T("sudo 日志"), "grep sudo /var/log/auth.log | tail -20", L10n.T("权限审计")),
                // 公司脚本封装：时钟同步一键（chrony/ntpdate/timedatectl 链式兜底，与 TimeSyncTool 远程命令同源）
                Create(L10n.T("系统"), L10n.T("时钟同步"), "(chronyc -a 'burst 3/4' && chronyc -a makestep 2>/dev/null) || (ntpdate ntp.aliyun.com 2>/dev/null) || (timedatectl set-ntp true 2>/dev/null) && date '+%Y-%m-%d %H:%M:%S'", L10n.T("时钟同步")),
                Create("Docker", L10n.T("容器列表"), "docker ps -a", L10n.T("容器管理")),
                Create("Docker", L10n.T("镜像列表"), "docker images", L10n.T("镜像管理")),
                Create("Docker", L10n.T("磁盘占用"), "docker system df", L10n.T("存储清理")),
                Create("Docker", L10n.T("容器日志"), "docker logs --tail 50 {container}", L10n.T("日志查看"))
            };
        }

        private QuickCommand Create(string group, string name, string cmd, string desc)
        {
            return new QuickCommand
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Command = cmd,
                Group = group,
                Description = desc,
                SortOrder = 0
            };
        }

        // ====== JSON 序列化 ======

        private string SerializeQuickCommands(List<QuickCommand> commands)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{\"commands\":[");
            for (int i = 0; i < commands.Count; i++)
            {
                var c = commands[i];
                if (i > 0) sb.AppendLine(",");
                sb.Append("  {");
                sb.AppendFormat("\"id\":\"{0}\"", EscapeJson(c.Id));
                sb.AppendFormat(",\"name\":\"{0}\"", EscapeJson(c.Name));
                sb.AppendFormat(",\"command\":\"{0}\"", EscapeJson(c.Command));
                sb.AppendFormat(",\"group\":\"{0}\"", EscapeJson(c.Group ?? ""));
                sb.AppendFormat(",\"sortOrder\":{0}", c.SortOrder);
                sb.AppendFormat(",\"description\":\"{0}\"", EscapeJson(c.Description ?? ""));
                sb.AppendFormat(",\"requiresRoot\":{0}", c.RequiresRoot ? "true" : "false");
                if (!string.IsNullOrEmpty(c.PreCommand))
                    sb.AppendFormat(",\"preCommand\":\"{0}\"", EscapeJson(c.PreCommand));
                if (!string.IsNullOrEmpty(c.PostCommand))
                    sb.AppendFormat(",\"postCommand\":\"{0}\"", EscapeJson(c.PostCommand));
                if (!string.IsNullOrEmpty(c.OsType))
                    sb.AppendFormat(",\"osType\":\"{0}\"", EscapeJson(c.OsType));
                if (!string.IsNullOrEmpty(c.Shortcut))
                    sb.AppendFormat(",\"shortcut\":\"{0}\"", EscapeJson(c.Shortcut));
                sb.Append("}");
            }
            sb.AppendLine("\n]}");
            return sb.ToString();
        }

        private List<QuickCommand> ParseQuickCommands(string json)
        {
            var result = new List<QuickCommand>();
            var commandsStart = json.IndexOf("\"commands\"");
            if (commandsStart < 0) return result;
            var arrStart = json.IndexOf('[', commandsStart);
            if (arrStart < 0) return result;

            int depth = 0; int objStart = -1;
            for (int i = arrStart; i < json.Length; i++)
            {
                if (json[i] == '{') { if (depth == 0) objStart = i; depth++; }
                else if (json[i] == '}') { depth--; if (depth == 0) result.Add(ParseQuickCommand(json.Substring(objStart, i - objStart + 1))); }
            }
            return result;
        }

        private QuickCommand ParseQuickCommand(string obj)
        {
            return new QuickCommand
            {
                Id = ExtractJsonString(obj, "id"),
                Name = ExtractJsonString(obj, "name"),
                Command = ExtractJsonString(obj, "command"),
                Group = ExtractJsonString(obj, "group"),
                SortOrder = ExtractJsonInt(obj, "sortOrder"),
                Description = ExtractJsonString(obj, "description"),
                RequiresRoot = ExtractJsonBool(obj, "requiresRoot"),
                PreCommand = ExtractJsonString(obj, "preCommand"),
                PostCommand = ExtractJsonString(obj, "postCommand"),
                OsType = ExtractJsonString(obj, "osType"),
                Shortcut = ExtractJsonString(obj, "shortcut")
            };
        }

        private string EscapeJson(string s) { return s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") ?? ""; }

        private string ExtractJsonString(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var start = json.IndexOf('"', colon + 1);
            if (start < 0) return null;
            var end = start + 1;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return json.Substring(start + 1, end - start - 1).Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
        }

        private int ExtractJsonInt(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return 0;
            var colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return 0;
            var start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            int.TryParse(json.Substring(start, end - start), out var v);
            return v;
        }

        private bool ExtractJsonBool(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern);
            if (idx < 0) return false;
            var colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return false;
            return json.IndexOf("true", colon) == colon + 1;
        }
    }
}
