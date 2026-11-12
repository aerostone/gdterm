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
        /// <summary>规则级永久豁免（配置里存为 rule:&lt;id&gt;）</summary>
        private readonly HashSet<string> _exemptRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            SpanMatch matchedSpan = default(SpanMatch);

            foreach (var rule in _compiledRules)
            {
                if (!rule.Rule.Enabled) continue;
                // 规则级永久豁免：跳过这条规则，而不是让整条命令不再受其它规则约束
                if (IsRuleWhitelisted(rule.Rule.Id)) continue;

                SpanMatch span;
                if (!TryMatch(trimmed, rule, out span)) continue;

                if (matched == null || rule.Rule.Level > maxLevel)
                {
                    matched = rule.Rule;
                    maxLevel = rule.Rule.Level;
                    matchedSpan = span;
                }
            }

            if (matched == null)
                return CommandCheckResult.Safe;

            var result = new CommandCheckResult
            {
                IsDangerous = true,
                MatchedRule = matched,
                RuleId = matched.Id,
                Level = matched.Level,
                ConfirmCount = matched.ConfirmCount,
                Description = matched.Description,
                RuleName = matched.Name,
                Category = matched.Category,
                MatchIndex = matchedSpan.Index,
                MatchLength = matchedSpan.Length
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
            // 配置界面里白名单是一整块文本，rule:<id> 条目和命令条目混在一起
            if (cmd.StartsWith(RuleWhitelistPrefix, StringComparison.OrdinalIgnoreCase))
            {
                RemoveRuleFromWhitelist(cmd.Substring(RuleWhitelistPrefix.Length).Trim());
                return;
            }
            _whitelist.Remove(cmd);
            if (_config.Whitelist != null)
                _config.Whitelist.RemoveAll(w => string.Equals(w, cmd, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }

        /// <summary>
        /// 规则级永久豁免（存为 rule:&lt;id&gt;）。粒度选规则而非命令：
        /// “记住这条命令”对带参数的运维命令几乎无用——换个目录就再弹一次，
        /// 用户要么被烦死、要么学会无脑点确认，两种结果都等于闸门失效。
        /// </summary>
        public void AddRuleToWhitelist(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return;
            var id = ruleId.Trim();
            var entry = "rule:" + id;
            if (!_exemptRuleIds.Add(id)) return;
            if (_config.Whitelist == null) _config.Whitelist = new List<string>();
            if (!_config.Whitelist.Any(w => string.Equals(w, entry, StringComparison.OrdinalIgnoreCase)))
                _config.Whitelist.Add(entry);
            SaveConfig();
        }

        /// <summary>取消规则级永久豁免并落盘。</summary>
        public void RemoveRuleFromWhitelist(string ruleId)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return;
            var id = ruleId.Trim();
            _exemptRuleIds.Remove(id);
            if (_config.Whitelist != null)
                _config.Whitelist.RemoveAll(w => string.Equals(w, RuleWhitelistPrefix + id, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }

        /// <summary>该规则是否被永久豁免。</summary>
        public bool IsRuleWhitelisted(string ruleId)
        {
            return !string.IsNullOrEmpty(ruleId) && _exemptRuleIds.Contains(ruleId);
        }

        /// <summary>规则级豁免在配置白名单里的前缀。</summary>
        public const string RuleWhitelistPrefix = "rule:";

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
            // 内置规则只读：改完下次加载会被代码里收紧的版本覆盖，静默失效能算作“保存成功”才是真的坑
            if (IsBuiltinRule(id)) return false;
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
            if (IsBuiltinRule(id)) return false;
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
            sb.Append("  \"schemaVersion\": ").Append(DangerousCommandConfig.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
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
                if (!string.IsNullOrEmpty(r.Exclude))
                    sb.Append(", \"exclude\": ").Append(Quote(r.Exclude));
                sb.Append("}");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            // 审计 F1：转义走 JsonText，读取端 ExtractString 才还原得回来（规则里全是正则，\d \s 必须原样）
            return "\"" + Gdterm.Core.JsonText.Escape(value) + "\"";
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
                _config = MergeWithBuiltins(ParseConfig(json));
            }
            catch
            {
                // 配置文件解析失败，使用默认配置
                _config = DangerousCommandConfig.GetDefault();
            }
        }

        /// <summary>
        /// 内置规则只读 + 用户 delta（schemaVersion 2）。
        /// 老形态把 55 条内置规则整份冻进用户文件，于是“收紧 rm-rf 正则”这类修复永远到不了老用户机器上——
        /// 全量写的配置就是一次性的代码快照。现在：内置规则的模式/等级/文案以代码为准，
        /// 只继承用户的 enabled；用户自建规则原样保留。
        /// </summary>
        private static DangerousCommandConfig MergeWithBuiltins(DangerousCommandConfig fromFile)
        {
            if (fromFile == null) return DangerousCommandConfig.GetDefault();

            var defaults = DangerousCommandConfig.GetDefault();
            var builtinIds = new HashSet<string>(defaults.Rules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

            var savedById = new Dictionary<string, DangerousCommandRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in fromFile.Rules ?? new List<DangerousCommandRule>())
            {
                if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                if (!savedById.ContainsKey(r.Id)) savedById[r.Id] = r;
            }

            var merged = new DangerousCommandConfig
            {
                SchemaVersion = DangerousCommandConfig.CurrentSchemaVersion,
                Enabled = fromFile.Enabled,
                ApplyToBroadcast = fromFile.ApplyToBroadcast,
                Whitelist = fromFile.Whitelist ?? new List<string>()
            };

            foreach (var builtin in defaults.Rules)
            {
                DangerousCommandRule saved;
                if (savedById.TryGetValue(builtin.Id, out saved))
                    builtin.Enabled = saved.Enabled;   // 用户在界面上关掉的内置规则，尊重
                merged.Rules.Add(builtin);
            }

            foreach (var r in fromFile.Rules ?? new List<DangerousCommandRule>())
            {
                if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                if (builtinIds.Contains(r.Id)) continue;   // 内置 Id 的旧副本丢弃
                merged.Rules.Add(r);
            }

            return merged;
        }

        /// <summary>该 Id 是否为内置规则（内置只读：可启用/禁用，不可改模式或删除）。</summary>
        public static bool IsBuiltinRule(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return DangerousCommandConfig.GetDefault().Rules
                .Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private void CompileRules()
        {
            _compiledRules.Clear();
            _whitelist.Clear();

            _exemptRuleIds.Clear();

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

                if (!string.IsNullOrWhiteSpace(rule.Exclude))
                {
                    try
                    {
                        compiled.ExcludeRegex = new Regex(rule.Exclude, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                    }
                    catch
                    {
                        compiled.ExcludeRegex = null; // 否决模式写坏了不能把整条规则带走
                    }
                }

                _compiledRules.Add(compiled);
            }

            // 加载白名单（命令级精确匹配 + rule:<id> 规则级豁免）
            if (_config.Whitelist != null)
            {
                foreach (var raw in _config.Whitelist)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var w = raw.Trim();
                    if (w.StartsWith(RuleWhitelistPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var id = w.Substring(RuleWhitelistPrefix.Length).Trim();
                        if (id.Length > 0) _exemptRuleIds.Add(id);
                        continue;
                    }
                    _whitelist.Add(w);
                }
            }
        }

        private static bool TryMatch(string command, CompiledRule rule, out SpanMatch span)
        {
            span = default(SpanMatch);

            // 否决模式：用户把已知安全的形态从宽规则里摘出去，不必整条关掉
            if (rule.ExcludeRegex != null && rule.ExcludeRegex.IsMatch(command))
                return false;

            switch (rule.Rule.PatternType)
            {
                case PatternType.Equals:
                    if (!command.Equals(rule.Rule.Pattern, StringComparison.OrdinalIgnoreCase)) return false;
                    span = new SpanMatch(0, command.Length);
                    return true;

                case PatternType.Contains:
                {
                    var idx = command.IndexOf(rule.Rule.Pattern, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return false;
                    span = new SpanMatch(idx, rule.Rule.Pattern.Length);
                    return true;
                }

                case PatternType.Regex:
                {
                    if (rule.Regex == null) return false;
                    var m = rule.Regex.Match(command);
                    if (!m.Success) return false;
                    span = new SpanMatch(m.Index, m.Length);
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>命中片段在命令里的位置（弹窗把这一段涂红）。</summary>
        private struct SpanMatch
        {
            public SpanMatch(int index, int length) { Index = index; Length = length; }
            public int Index { get; }
            public int Length { get; }
        }

        /// <summary>
        /// 简易 JSON 配置解析（无外部依赖）
        /// </summary>
        private static DangerousCommandConfig ParseConfig(string json)
        {
            var config = new DangerousCommandConfig();

            // 解析 Enabled / 架构版本
            config.SchemaVersion = ExtractInt(json, "schemaVersion") ?? 1;
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
                    Enabled = ExtractBool(obj, "enabled") ?? true,
                    Exclude = ExtractString(obj, "exclude")
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
            // 审计 F1：原实现完全不反转义 → 存回去的 "\d\s" 读回变成 "\\d\\s"，黑名单正则静默失配
            return Gdterm.Core.JsonText.ExtractString(json, key);
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
            // 审计 F1：原实现不跳字符串，规则里出现 [ 或 { 就错位；统一走 JsonText
            return Gdterm.Core.JsonText.FindMatchingBracket(json, openPos);
        }

        private class CompiledRule
        {
            public DangerousCommandRule Rule { get; set; }
            public Regex Regex { get; set; }
            /// <summary>否决模式（命中即撤销本条规则）</summary>
            public Regex ExcludeRegex { get; set; }
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

        /// <summary>命中规则 Id（规则级豁免 / 审计用；比 Name 稳定）。</summary>
        public string RuleId { get; set; }

        /// <summary>命中片段在命令中的起始位置（-1 = 无位置信息）</summary>
        public int MatchIndex { get; set; } = -1;

        /// <summary>命中片段长度</summary>
        public int MatchLength { get; set; }
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
