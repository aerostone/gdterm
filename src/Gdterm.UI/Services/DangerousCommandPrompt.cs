using System;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Security;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 危险命令确认的唯一 UI 出口。
    ///
    /// 2026-11 审计：判定的 4 个入口各写了一份 `Check → new Dialog → AddToWhitelist`，
    /// 于是每次改语义都要改 4 处，且粘贴/宏/键位/登录脚本 4 条路根本没接。
    /// 现在所有入口只调 <see cref="Confirm"/>：判定、弹窗、豁免落盘、审计日志都在这里一次做完。
    /// </summary>
    public static class DangerousCommandPrompt
    {
        /// <summary>日志里保留的命令长度上限（粘贴 20 行不该把审计日志撑爆）。</summary>
        private const int LogTextLimit = 400;

        /// <summary>
        /// 判定并（必要时）向用户确认。
        /// </summary>
        /// <param name="owner">弹窗归属窗口，可为 null</param>
        /// <param name="gate">命令闸门；null 视为未启用检测，直接放行</param>
        /// <param name="text">待发送文本（单行命令或多行整批）</param>
        /// <param name="source">入口来源，写进弹窗与审计</param>
        /// <param name="audit">审计记录器，可为 null</param>
        /// <param name="host">目标主机（审计用），可为 null</param>
        /// <returns>true = 允许发送；false = 用户取消或检测失败（fail-closed）</returns>
        public static bool Confirm(
            IWin32Window owner,
            CommandGate gate,
            string text,
            InputSource source,
            IAuditLogger audit,
            string host)
        {
            if (gate == null) return true;
            if (string.IsNullOrWhiteSpace(text)) return true;

            GateDecision decision;
            try
            {
                decision = gate.Inspect(text, source);
            }
            catch (Exception ex)
            {
                // 判定本身炸了：不能“放过去”，否则任何构造出来的输入都能靠触发异常绕过闸门
                Log(audit, SecurityEvent.DangerousCommandBlocked,
                    "detector error via " + source + " on " + Describe(host) + ": " + ex.Message);
                ShowFailClosed(owner, L10n.T("危险命令检测失败，已阻止发送该命令。\n") + ex.Message);
                return false;
            }

            if (decision.Error != null)
            {
                Log(audit, SecurityEvent.DangerousCommandBlocked,
                    "detector error via " + source + " on " + Describe(host) + ": " + decision.Error.Message);
                ShowFailClosed(owner, L10n.T("危险命令检测失败，已阻止发送该命令。\n") + decision.Error.Message);
                return false;
            }

            if (!decision.NeedsConfirm)
            {
                if (decision.Exempted.Count > 0)
                {
                    Log(audit, SecurityEvent.DangerousCommandWhitelisted,
                        "session-exempt rules [" + string.Join(",", decision.Exempted.Select(h => h.RuleId).Distinct()) +
                        "] via " + source + " on " + Describe(host) + ": " + Summarize(decision.Text));
                }
                return true;
            }

            bool confirmed;
            bool sessionExempt;
            bool permanentExempt;
            var ruleIds = decision.Hits.Select(h => h.RuleId).Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            using (var dlg = new DangerousCommandDialog(decision))
            {
                try { dlg.ShowDialog(owner); }
                catch (Exception ex)
                {
                    // 弹不出来 = 问不到用户 = 不能放行
                    Log(audit, SecurityEvent.DangerousCommandBlocked,
                        "dialog failed via " + source + " on " + Describe(host) + ": " + ex.Message);
                    return false;
                }
                confirmed = dlg.IsConfirmed;
                sessionExempt = dlg.SessionExemptRequested;
                permanentExempt = dlg.PermanentExemptRequested;
                if (confirmed)
                {
                    var exemptIds = dlg.ExemptRuleIds;
                    if (sessionExempt) ApplySessionExemption(gate, exemptIds, audit, host);
                    if (permanentExempt) ApplyPermanentExemption(gate, exemptIds, audit, host);
                }
            }

            if (!confirmed)
            {
                Log(audit, SecurityEvent.DangerousCommandBlocked,
                    "declined via " + source + " on " + Describe(host) +
                    " rules [" + string.Join(",", ruleIds) + "]: " + Summarize(decision.Text));
                return false;
            }

            // 确认执行也要留痕：事后追“这条 rm -rf 是谁在哪天点过去的”，只有 Blocked 日志是查不出来的
            Log(audit, SecurityEvent.DangerousCommandConfirmed,
                "confirmed via " + source + " on " + Describe(host) +
                " rules [" + string.Join(",", ruleIds) + "] lines " +
                decision.Hits.Count + "/" + decision.LineCount + ": " + Summarize(decision.Text));
            return true;
        }

        private static void ApplySessionExemption(CommandGate gate, System.Collections.Generic.IReadOnlyList<string> ruleIds,
            IAuditLogger audit, string host)
        {
            foreach (var id in ruleIds) gate.ExemptRuleForSession(id);
            if (ruleIds.Count == 0) return;
            Log(audit, SecurityEvent.DangerousCommandWhitelisted,
                "session-exempt rules [" + string.Join(",", ruleIds) + "] on " + Describe(host));
        }

        private static void ApplyPermanentExemption(CommandGate gate, System.Collections.Generic.IReadOnlyList<string> ruleIds,
            IAuditLogger audit, string host)
        {
            if (gate.Detector == null) return;
            foreach (var id in ruleIds)
            {
                try { gate.Detector.AddRuleToWhitelist(id); }
                catch (Exception ex) { DiagLog.Swallowed("DangerousCommandPrompt.Whitelist", ex); }
            }
            if (ruleIds.Count == 0) return;
            Log(audit, SecurityEvent.DangerousCommandWhitelisted,
                "permanent-exempt rules [" + string.Join(",", ruleIds) + "] on " + Describe(host) +
                (string.IsNullOrEmpty(gate.Detector.LastSaveError) ? "" : " (save failed: " + gate.Detector.LastSaveError + ")"));
        }

        /// <summary>审计/弹窗里的命令摘要：多行折叠成一行并截断。</summary>
        public static string Summarize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var oneLine = new StringBuilder();
            foreach (var ch in text)
            {
                if (ch == '\r') continue;
                oneLine.Append(ch == '\n' ? '⏎' : ch);
                if (oneLine.Length >= LogTextLimit) { oneLine.Append("…"); break; }
            }
            return oneLine.ToString();
        }

        private static string Describe(string host)
        {
            return string.IsNullOrEmpty(host) ? "?" : host;
        }

        private static void Log(IAuditLogger audit, SecurityEvent evt, string detail)
        {
            if (audit == null) return;
            try { audit.LogSecurityEvent(evt, detail); }
            catch (Exception ex) { DiagLog.Swallowed("DangerousCommandPrompt.Log", ex); }
        }

        private static void ShowFailClosed(IWin32Window owner, string message)
        {
            try
            {
                MessageBox.Show(owner, message, L10n.T("安全拦截"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { DiagLog.Swallowed("DangerousCommandPrompt.ShowFailClosed", ex); }
        }
    }
}
