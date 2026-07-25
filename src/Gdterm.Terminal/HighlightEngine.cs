using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 关键词高亮匹配结果
    /// </summary>
    public class HighlightMatch
    {
        public int StartIndex { get; set; }
        public int Length { get; set; }
        public Color Foreground { get; set; }
        public Color Background { get; set; }
        public bool Bold { get; set; }
        public int RuleSortOrder { get; set; }
    }

    /// <summary>
    /// 终端关键词高亮引擎——编译正则、匹配文本行
    /// </summary>
    public class HighlightEngine : IDisposable
    {
        private List<CompiledRule> _rules = new List<CompiledRule>();

        private class CompiledRule
        {
            public HighlightRule Rule;
            public Regex CompiledRegex;
            public Color ForeColor;
            public Color BackColor;
        }

        public HighlightEngine() { }

        public HighlightEngine(List<HighlightRule> rules)
        {
            LoadRules(rules);
        }

        /// <summary>加载/重载规则</summary>
        public void LoadRules(List<HighlightRule> rules)
        {
            _rules.Clear();
            if (rules == null) return;

            foreach (var rule in rules)
            {
                if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) continue;

                try
                {
                    var compiled = new CompiledRule { Rule = rule };

                    RegexOptions opts = RegexOptions.Compiled;
                    if (!rule.CaseSensitive) opts |= RegexOptions.IgnoreCase;
                    compiled.CompiledRegex = new Regex(rule.Pattern, opts);

                    if (!string.IsNullOrEmpty(rule.ForegroundColor))
                        compiled.ForeColor = ParseColor(rule.ForegroundColor);
                    if (!string.IsNullOrEmpty(rule.BackgroundColor))
                        compiled.BackColor = ParseColor(rule.BackgroundColor);

                    _rules.Add(compiled);
                }
                catch
                {
                    // 跳过无效正则
                }
            }

            // 按 SortOrder 排序（优先级高的先匹配）
            _rules.Sort((a, b) => a.Rule.SortOrder.CompareTo(b.Rule.SortOrder));
        }

        /// <summary>对一行文本返回所有高亮匹配</summary>
        public List<HighlightMatch> MatchLine(string line)
        {
            var matches = new List<HighlightMatch>();
            if (string.IsNullOrEmpty(line) || _rules.Count == 0) return matches;

            foreach (var rule in _rules)
            {
                try
                {
                    var regexMatches = rule.CompiledRegex.Matches(line);
                    foreach (Match m in regexMatches)
                    {
                        matches.Add(new HighlightMatch
                        {
                            StartIndex = m.Index,
                            Length = m.Length,
                            Foreground = rule.ForeColor,
                            Background = rule.BackColor,
                            Bold = rule.Rule.Bold,
                            RuleSortOrder = rule.Rule.SortOrder
                        });
                    }
                }
                catch { }
            }

            // 按位置排序，高优先级（低 SortOrder）覆盖低优先级
            matches.Sort((a, b) =>
            {
                int cmp = a.StartIndex.CompareTo(b.StartIndex);
                return cmp != 0 ? cmp : a.RuleSortOrder.CompareTo(b.RuleSortOrder);
            });

            return matches;
        }

        /// <summary>快速判断一行是否有任何高亮</summary>
        public bool HasAnyMatch(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            foreach (var rule in _rules)
            {
                try { if (rule.CompiledRegex.IsMatch(line)) return true; }
                catch { }
            }
            return false;
        }

        private static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.Empty;
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return Color.FromArgb(r, g, b);
            }
            return Color.Empty;
        }

        public void Dispose()
        {
            _rules.Clear();
        }
    }
}
