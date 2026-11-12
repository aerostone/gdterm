using System;
using System.Collections.Generic;
using System.Linq;
using Gdterm.Core.Models;

namespace Gdterm.Security
{
    /// <summary>
    /// 单行判定：哪一行、命中了哪条规则、命中片段在行内的位置（弹窗高亮用）。
    /// </summary>
    public sealed class LineVerdict
    {
        /// <summary>行号（1 基，相对整批文本）</summary>
        public int LineNumber { get; set; }

        /// <summary>该行原文（未 trim，弹窗原样显示）</summary>
        public string Line { get; set; }

        public CommandCheckResult Check { get; set; }

        public string RuleId { get { return Check != null && Check.MatchedRule != null ? Check.MatchedRule.Id : ""; } }

        public string RuleName { get { return Check != null ? Check.RuleName : ""; } }

        public DangerLevel Level { get { return Check != null ? Check.Level : DangerLevel.Medium; } }
    }

    /// <summary>
    /// 一次输入的整体判定结果。
    /// “危险”是整批判定的：粘贴 20 行里只要有 1 行是 rm -rf /，整批都得停下来看一眼，
    /// 不能老老实实执行掉另外 19 行（部分执行比整体拒绝更危险，用户根本不知道跑掉了几行）。
    /// </summary>
    public sealed class GateDecision
    {
        public string Text { get; set; }
        public InputSource Source { get; set; }

        /// <summary>命中的危险行（已排除会话豁免的规则）</summary>
        public List<LineVerdict> Hits { get; } = new List<LineVerdict>();

        /// <summary>命中了、但规则已被本会话豁免的行（只用于审计，不再打扰用户）</summary>
        public List<LineVerdict> Exempted { get; } = new List<LineVerdict>();

        /// <summary>检测过程本身出错（正则编译/运行异常）。调用方必须 fail-closed。</summary>
        public Exception Error { get; set; }

        /// <summary>是否为多行批（弹窗换“逐行预演”样式）</summary>
        public bool IsBatch { get; set; }

        /// <summary>总行数（预览标题用）</summary>
        public int LineCount { get; set; }

        public bool NeedsConfirm { get { return Hits.Count > 0; } }

        public DangerLevel HighestLevel
        {
            get
            {
                if (Hits.Count == 0) return DangerLevel.Medium;
                var level = Hits[0].Level;
                foreach (var h in Hits) if (h.Level > level) level = h.Level;
                return level;
            }
        }

        /// <summary>
        /// 需要点击确认的次数 = 命中行里最高的那条（不是各行累加——
        /// “粘贴了 5 行 rm -rf”让用户点 15 次只会训练他闭眼点）。
        /// </summary>
        public int ConfirmCount
        {
            get
            {
                var count = 1;
                foreach (var h in Hits)
                {
                    var c = h.Check != null ? h.Check.ConfirmCount : 1;
                    if (c > count) count = c;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// 命令闸门——收敛所有输入入口的唯一判定点（2026-11 审计的结论）。
    ///
    /// 之前的形态：4 个入口各自 `Check() + new DangerousCommandDialog() + AddToWhitelist()`，
    /// 于是粘贴 / 宏回放 / 键位绑定 / 登录脚本 4 条路完全绕过；白名单只有“整条命令精确匹配”一种粒度；
    /// 内置规则被冻结进用户配置文件，改精度也到不了老用户机器上。
    ///
    /// 现在：入口只调 <see cref="Inspect"/>，弹窗与审计由 UI 层统一做（DangerousCommandPrompt）。
    /// </summary>
    public sealed class CommandGate
    {
        private readonly DangerousCommandDetector _detector;
        private readonly HashSet<string> _sessionExemptRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public CommandGate(DangerousCommandDetector detector)
        {
            if (detector == null) throw new ArgumentNullException("detector");
            _detector = detector;
        }

        public DangerousCommandDetector Detector { get { return _detector; } }

        /// <summary>本会话已豁免的规则 Id（内存态，退出即失效）</summary>
        public IReadOnlyCollection<string> SessionExemptRules { get { return _sessionExemptRules; } }

        /// <summary>
        /// 本会话内不再提示某条规则。粒度选“规则级”而不是“命令级”：
        /// 整条命令匹配对带参数的命令形同虚设（rm -rf /tmp/a 豁免了，rm -rf /tmp/b 照样弹）。
        /// </summary>
        public void ExemptRuleForSession(string ruleId)
        {
            if (!string.IsNullOrEmpty(ruleId)) _sessionExemptRules.Add(ruleId);
        }

        public void ClearSessionExemptions()
        {
            _sessionExemptRules.Clear();
        }

        /// <summary>
        /// 判定一段输入（单行命令、多行粘贴、宏回放文本均可）。
        /// 纯计算，不弹窗、不落盘——UI 层拿结果决定怎么问用户。
        /// </summary>
        public GateDecision Inspect(string text, InputSource source)
        {
            var decision = new GateDecision { Text = text ?? "", Source = source };

            if (string.IsNullOrWhiteSpace(text)) return decision;

            try
            {
                var lines = SplitLines(text);
                decision.LineCount = lines.Count;
                decision.IsBatch = lines.Count > 1;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var check = _detector.Check(line);
                    if (check == null || !check.IsDangerous) continue;

                    var verdict = new LineVerdict { LineNumber = i + 1, Line = line, Check = check };
                    var ruleId = verdict.RuleId;
                    if (!string.IsNullOrEmpty(ruleId) && _sessionExemptRules.Contains(ruleId))
                        decision.Exempted.Add(verdict);
                    else
                        decision.Hits.Add(verdict);
                }
            }
            catch (Exception ex)
            {
                decision.Error = ex;
            }

            return decision;
        }

        /// <summary>
        /// 按行切分：粘贴文本可能是 \r\n（Windows 剪贴板）、\n（Unix 复制）、\r（bracketed paste 体）。
        /// 空行丢弃（`\r\n` 会切出空串，空串不可能是命令）。
        /// </summary>
        public static IList<string> SplitLines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                result.Add(line);
            }
            return result;
        }
    }
}
