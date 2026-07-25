using System;
using System.Collections.Generic;
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
        /// 添加白名单命令
        /// </summary>
        public void AddToWhitelist(string command)
        {
            if (!string.IsNullOrWhiteSpace(command))
                _whitelist.Add(command.Trim());
        }

        /// <summary>
        /// 移除白名单命令
        /// </summary>
        public void RemoveFromWhitelist(string command)
        {
            if (!string.IsNullOrWhiteSpace(command))
                _whitelist.Remove(command.Trim());
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

            // 如果没有规则，用默认
            if (config.Rules.Count == 0)
                return DangerousCommandConfig.GetDefault();

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
