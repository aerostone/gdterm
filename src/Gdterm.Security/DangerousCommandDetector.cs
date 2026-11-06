using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Gdterm.Core.Models;

namespace Gdterm.Security
{
    /// <summary>
    /// 危险命令检测器——在命令发送到终端前拦截并要求确认
    /// 
    /// 三级危险等级：
    ///   Medium  → 确认 1 次（黄色警告）
    ///   High    → 确认 2 次（橙色警告）
    ///   Critical → 确认 3 次（红色警告）
    /// 
    /// 支持：
    ///   - 内置 40+ 条危险命令规则
    ///   - 自定义配置文件 (dangerous-commands.json)
    ///   - 白名单豁免
    ///   - 正则/包含/精确三种匹配模式
    ///   - 命令分类（文件系统/磁盘/网络/防火墙/进程/权限等）
    /// </summary>
    public class DangerousCommandDetector : IDisposable
    {
        private DangerousCommandConfig _config;
        private readonly List<CompiledRule> _compiledRules = new List<CompiledRule>();
        private readonly HashSet<string> _whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string _configPath;

        /// <summary>
        /// 命令被拦截事件（用于日志记录）
        /// </summary>
        public event EventHandler<CommandBlockedEventArgs> CommandBlocked;

        public DangerousCommandDetector(string configPath = null)
        {
            _configPath = configPath;

            if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                LoadConfig(configPath);
            }
            else
            {
                _config = DangerousCommandConfig.GetDefault();
            }

            CompileRules();
        }

        /// <summary>
        /// 检测命令是否为危险命令
        /// </summary>
        /// <param name="command">要检测的命令</param>
        /// <returns>检测结果（匹配的最高危险等级规则）</returns>
        public CommandCheckResult Check(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return CommandCheckResult.Safe;

            if (!_config.Enabled)
                return CommandCheckResult.Safe;

            var trimmed = command.Trim();

            // 白名单检查
            if (_whitelist.Contains(trimmed))
                return CommandCheckResult.Safe;

            // 匹配所有规则，返回最危险的
            DangerousCommandRule matched = null;
            DangerLevel maxLevel = DangerLevel.Medium;

            foreach (var rule in _compiledRules)
            {
                if (!rule.Rule.Enabled) continue;

                if (IsMatch(trimmed, rule))
                {
                    if (matched == null || rule.Rule.Level > maxLevel)
                    {
                        matched = rule.Rule;
                        maxLevel = rule.Rule.Level;
                    }
                }
            }

            if (matched == null)
                return CommandCheckResult.Safe;

            var result = new CommandCheckResult
            {
                IsDangerous = true,
                MatchedRule = matched,
                Level = matched.Level,
                ConfirmCount = matched.ConfirmCount,
                Description = matched.Description,
                RuleName = matched.Name,
                Category = matched.Category
            };

            CommandBlocked?.Invoke(this, new CommandBlockedEventArgs
            {
                Command = command,
                Rule = matched,
                Level = matched.Level
            });

            return result;
        }

        /// <summary>
        /// 添加白名单命令。
        /// 2026-11 审计：此前后台只在内存 HashSet 里加，配置文件不动、界面也读不回来——
        /// 用户加的豁免重启即消失（“功能是假的”）。现在同步进 _config.Whitelist 并落盘。
        /// </summary>
        public void AddToWhitelist(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            var cmd = command.Trim();
            if (!_whitelist.Add(cmd)) return;
            if (_config.Whitelist == null) _config.Whitelist = new List<string>();
            if (!_config.Whitelist.Any(w => string.Equals(w, cmd, StringComparison.OrdinalIgnoreCase)))
                _config.Whitelist.Add(cmd);
            SaveConfig();
        }

        /// <summary>移除白名单命令（同样落盘）。</summary>
        public void RemoveFromWhitelist(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            var cmd = command.Trim();
            _whitelist.Remove(cmd);
            if (_config.Whitelist != null)
                _config.Whitelist.RemoveAll(w => string.Equals(w, cmd, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }

        /// <summary>白名单快照（配置界面回填用；此前没有这个入口，界面只能显示一句“功能已就绪”）。</summary>
        public IReadOnlyList<string> GetWhitelist()
        {
            var list = new List<string>(_whitelist);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.AsReadOnly();
        }

        /// <summary>新增规则并落盘（规则热生效，不需要重启）。</summary>
        public void AddRule(DangerousCommandRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Pattern)) return;
            if (string.IsNullOrWhiteSpace(rule.Id))
                rule.Id = "custom-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _config.Rules.Add(rule);
            SaveConfig();
            CompileRules();
        }

        /// <summary>按 Id 覆盖规则内容并落盘。</summary>
        public bool UpdateRule(string id, DangerousCommandRule updated)
        {
            if (string.IsNullOrWhiteSpace(id) || updated == null) return false;
            var idx = _config.Rules.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            updated.Id = id;
            _config.Rules[idx] = updated;
            SaveConfig();
            CompileRules();
            return true;
        }

        /// <summary>按 Id 删除规则并落盘。</summary>
        public bool RemoveRule(string id)
        {
            var idx = _config.Rules.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _config.Rules.RemoveAt(idx);
            SaveConfig();
            CompileRules();
            return true;
        }

        /// <summary>启用/禁用规则并落盘（禁用只影响匹配，不删规则）。</summary>
        public bool SetRuleEnabled(string id, bool enabled)
        {
            var rule = _config.Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (rule == null) return false;
            rule.Enabled = enabled;
            SaveConfig();
            CompileRules();
            return true;
        }

        /// <summary>写回配置文件；路径未配置时静默跳过（内存模式，测试用）。</summary>
        public void SaveConfig()
        {
            if (string.IsNullOrEmpty(_configPath)) return;
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = _configPath + ".tmp";
                File.WriteAllText(tmp, SerializeConfig(_config), Encoding.UTF8);
                if (File.Exists(_configPath)) File.Delete(_configPath);
                File.Move(tmp, _configPath);
            }
            catch (Exception ex)
            {
                // 写不进去必须留痕：调用方据此给用户看失败提示，而不是假装保存成功
                LastSaveError = ex.Message;
            }
        }

        /// <summary>最近一次 SaveConfig 的失败原因（成功时为 null）。</summary>
        public string LastSaveError { get; private set; }

        private static string SerializeConfig(DangerousCommandConfig cfg)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"enabled\": ").Append(cfg.Enabled ? "true" : "false").Append(",\n");
            sb.Append("  \"applyToBroadcast\": ").Append(cfg.ApplyToBroadcast ? "true" : "false").Append(",\n");
            sb.Append("  \"whitelist\": [");
            var wl = cfg.Whitelist ?? new List<string>();
            for (int i = 0; i < wl.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(wl[i]));
            }
            sb.Append("],\n");
            sb.Append("  \"rules\": [");
            for (int i = 0; i < cfg.Rules.Count; i++)
            {
                var r = cfg.Rules[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    {");
                sb.Append("\"id\": ").Append(Quote(r.Id)).Append(", ");
                sb.Append("\"name\": ").Append(Quote(r.Name)).Append(", ");
                sb.Append("\"pattern\": ").Append(Quote(r.Pattern)).Append(", ");
                sb.Append("\"patternType\": ").Append(Quote(r.PatternType.ToString())).Append(", ");
                sb.Append("\"level\": ").Append(Quote(r.Level.ToString())).Append(", ");
                sb.Append("\"confirmCount\": ").Append(r.ConfirmCount.ToString(CultureInfo.InvariantCulture)).Append(", ");
                sb.Append("\"description\": ").Append(Quote(r.Description)).Append(", ");
                sb.Append("\"category\": ").Append(Quote(r.Category)).Append(", ");
                sb.Append("\"enabled\": ").Append(r.Enabled ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 获取所有规则（用于配置界面）
        /// </summary>
        public IReadOnlyList<DangerousCommandRule> GetAllRules()
        {
            return _config.Rules.AsReadOnly();
        }

        /// <summary>
        /// 获取指定等级的规则
        /// </summary>
        public IReadOnlyList<DangerousCommandRule> GetRulesByLevel(DangerLevel level)
        {
            return _config.Rules.FindAll(r => r.Level == level).AsReadOnly();
        }

        /// <summary>
        /// 获取所有分类
        /// </summary>
        public IReadOnlyList<string> GetCategories()
        {
            return _config.Rules
                .Select(r => r.Category)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// 重新加载配置文件
        /// </summary>
        public void ReloadConfig()
        {
            if (!string.IsNullOrEmpty(_configPath) && File.Exists(_configPath))
            {
                LoadConfig(_configPath);
                CompileRules();
            }
        }

        public void Dispose()
        {
            _compiledRules.Clear();
            _whitelist.Clear();
        }

        private void LoadConfig(string path)
        {
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                _config = ParseConfig(json);
            }
            catch
            {
                // 配置文件解析失败，使用默认配置
                _config = DangerousCommandConfig.GetDefault();
            }
        }

        private void CompileRules()
        {
            _compiledRules.Clear();
            _whitelist.Clear();

            foreach (var rule in _config.Rules)
            {
                if (!rule.Enabled) continue;

                var compiled = new CompiledRule { Rule = rule };

                if (rule.PatternType == PatternType.Regex)
                {
                    try
                    {
                        compiled.Regex = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    }
                    catch
                    {
                        continue; // 无效正则跳过
                    }
                }

                _compiledRules.Add(compiled);
            }

            // 加载白名单
            if (_config.Whitelist != null)
            {
                foreach (var w in _config.Whitelist)
                    _whitelist.Add(w.Trim());
            }
        }

        private static bool IsMatch(string command, CompiledRule rule)
        {
            switch (rule.Rule.PatternType)
            {
                case PatternType.Equals:
                    return command.Equals(rule.Rule.Pattern, StringComparison.OrdinalIgnoreCase);

                case PatternType.Contains:
                    return command.IndexOf(rule.Rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0;

                case PatternType.Regex:
                    return rule.Regex != null && rule.Regex.IsMatch(command);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 简易 JSON 配置解析（无外部依赖）
        /// </summary>
        private static DangerousCommandConfig ParseConfig(string json)
        {
            var config = new DangerousCommandConfig();

            // 解析 Enabled
            config.Enabled = ExtractBool(json, "enabled") ?? true;
            config.ApplyToBroadcast = ExtractBool(json, "applyToBroadcast") ?? true;

            // 解析 Rules 数组
            var rulesStart = json.IndexOf("\"rules\"");
            if (rulesStart > 0)
            {
                var arrayStart = json.IndexOf('[', rulesStart);
                var arrayEnd = FindMatchingBracket(json, arrayStart);
                if (arrayStart > 0 && arrayEnd > arrayStart)
                {
                    var rulesJson = json.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
                    config.Rules = ParseRules(rulesJson);
                }
            }

            // 解析 Whitelist（2026-11：此前只读规则不读白名单，配置里的豁免永远不生效）
            var wlStart = json.IndexOf("\"whitelist\"");
            if (wlStart > 0)
            {
                var arrStart = json.IndexOf('[', wlStart);
                var arrEnd = FindMatchingBracket(json, arrStart);
                if (arrStart > 0 && arrEnd > arrStart)
                {
                    var raw = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                    foreach (var entry in raw.Split(','))
                    {
                        var t = entry.Trim().Trim('"');
                        if (t.Length > 0) config.Whitelist.Add(t);
                    }
                }
            }

            // 如果没有规则，用默认
            if (config.Rules.Count == 0)
            {
                var fallback = DangerousCommandConfig.GetDefault();
                fallback.Whitelist = config.Whitelist;
                return fallback;
            }

            return config;
        }

        private static List<DangerousCommandRule> ParseRules(string rulesJson)
        {
            var rules = new List<DangerousCommandRule>();
            int pos = 0;

            while (pos < rulesJson.Length)
            {
                var objStart = rulesJson.IndexOf('{', pos);
                if (objStart < 0) break;
                var objEnd = FindMatchingBracket(rulesJson, objStart);
                if (objEnd < 0) break;

                var obj = rulesJson.Substring(objStart + 1, objEnd - objStart - 1);
                var rule = new DangerousCommandRule
                {
                    Id = ExtractString(obj, "id"),
                    Name = ExtractString(obj, "name"),
                    Pattern = ExtractString(obj, "pattern"),
                    PatternType = Enum.TryParse<PatternType>(ExtractString(obj, "patternType"), true, out var pt) ? pt : PatternType.Regex,
                    Level = Enum.TryParse<DangerLevel>(ExtractString(obj, "level"), true, out var dl) ? dl : DangerLevel.Medium,
                    ConfirmCount = ExtractInt(obj, "confirmCount") ?? 1,
                    Description = ExtractString(obj, "description"),
                    Category = ExtractString(obj, "category"),
                    Enabled = ExtractBool(obj, "enabled") ?? true
                };

                if (!string.IsNullOrEmpty(rule.Id) && !string.IsNullOrEmpty(rule.Pattern))
                    rules.Add(rule);

                pos = objEnd + 1;
            }

            return rules;
        }

        // ===== JSON 工具方法 =====

        private static string ExtractString(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            int end = start;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return json.Substring(start, end - start);
        }

        private static int? ExtractInt(string json, string key)
        {
            var pattern = $"\"{key}\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            int end = json.IndexOfAny(new[] { ',', '}', ' ' }, start);
            if (end < 0) end = json.Length;
            if (int.TryParse(json.Substring(start, end - start).Trim(), out int val))
                return val;
            return null;
        }

        private static bool? ExtractBool(string json, string key)
        {
            var pattern = $"\"{key}\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            var rest = json.Substring(start, Math.Min(10, json.Length - start)).TrimStart();
            if (rest.StartsWith("true")) return true;
            if (rest.StartsWith("false")) return false;
            return null;
        }

        private static int FindMatchingBracket(string json, int openPos)
        {
            if (openPos < 0 || openPos >= json.Length) return -1;
            char open = json[openPos];
            char close = open == '[' ? ']' : open == '{' ? '}' : ')';
            int depth = 0;
            for (int i = openPos; i < json.Length; i++)
            {
                if (json[i] == open) depth++;
                else if (json[i] == close) depth--;
                if (depth == 0) return i;
            }
            return -1;
        }

        private class CompiledRule
        {
            public DangerousCommandRule Rule { get; set; }
            public Regex Regex { get; set; }
        }
    }

    /// <summary>
    /// 命令检测结果
    /// </summary>
    public class CommandCheckResult
    {
        public static readonly CommandCheckResult Safe = new CommandCheckResult { IsDangerous = false };

        public bool IsDangerous { get; set; }
        public DangerousCommandRule MatchedRule { get; set; }
        public DangerLevel Level { get; set; }
        public int ConfirmCount { get; set; }
        public string Description { get; set; }
        public string RuleName { get; set; }
        public string Category { get; set; }
    }

    /// <summary>
    /// 命令拦截事件参数
    /// </summary>
    public class CommandBlockedEventArgs : EventArgs
    {
        public string Command { get; set; }
        public DangerousCommandRule Rule { get; set; }
        public DangerLevel Level { get; set; }
    }
}
