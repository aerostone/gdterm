using System;
using System.Collections.Generic;
using System.Text;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 触发器（P1-7）：高亮规则命中后可以发命令 / 弹通知 / 切标签，Xshell 式。
    /// 判定发生在「输出流按行切分」这一层，不依赖渲染器——所以即便标签被暂停、
    /// 或被 alt-screen 挡住，触发器照样会响。代价是只看流上的字面文本，不看屏幕回改
    /// （进度条用 \r 覆盖的那种不会重复触发，反而更好）。
    /// </summary>
    public partial class TerminalControl
    {
        private const int TriggerBufferMax = 65536;

        private HighlightEngine _triggerEngine;
        private readonly StringBuilder _triggerBuffer = new StringBuilder();
        private readonly Dictionary<string, DateTime> _triggerLastFired = new Dictionary<string, DateTime>();

        /// <summary>触发器要求切标签，参数是目标标签标题的关键字。TabContainerControl 订阅。</summary>
        public event EventHandler<string> TriggerTabSwitchRequested;

        /// <summary>按高亮规则重建触发器引擎；没有带动作的规则就整个关掉（零开销）。</summary>
        internal void SetTriggerRules(List<HighlightRule> rules)
        {
            try
            {
                if (rules == null || rules.Count == 0)
                {
                    if (_triggerEngine != null) { try { _triggerEngine.Dispose(); } catch (Exception) { } }
                    _triggerEngine = null;
                    return;
                }
                if (_triggerEngine == null) _triggerEngine = new HighlightEngine();
                _triggerEngine.LoadRules(rules);
                if (!_triggerEngine.HasTriggers)
                {
                    try { _triggerEngine.Dispose(); } catch (Exception) { }
                    _triggerEngine = null;
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.SetTriggerRules", ex); }
        }

        /// <summary>喂一段终端输出，按行扫触发器。只保留未成行的尾巴（上限 64KB）。</summary>
        private void EvaluateTriggers(string text)
        {
            if (_triggerEngine == null || string.IsNullOrEmpty(text)) return;
            try
            {
                _triggerBuffer.Append(text);
                if (_triggerBuffer.Length > TriggerBufferMax)
                    _triggerBuffer.Remove(0, _triggerBuffer.Length - TriggerBufferMax);

                int start = 0;
                for (int i = 0; i < _triggerBuffer.Length; i++)
                {
                    char c = _triggerBuffer[i];
                    if (c != '\n' && c != '\r') continue;
                    int len = i - start;
                    if (len > 0) FireTriggersForLine(_triggerBuffer.ToString(start, len));
                    start = i + 1;
                }
                if (start > 0) _triggerBuffer.Remove(0, start);
            }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.EvaluateTriggers", ex); }
        }

        private void FireTriggersForLine(string line)
        {
            List<TriggerHit> hits;
            try { hits = _triggerEngine.MatchTriggers(line); }
            catch (Exception ex) { DiagLog.Swallowed("TerminalControl.MatchTriggers", ex); return; }
            if (hits == null || hits.Count == 0) return;

            var now = DateTime.UtcNow;
            foreach (var hit in hits)
            {
                if (hit == null) continue;
                string key = (hit.RuleId ?? hit.RuleName ?? "?") + "|" + (int)hit.Action;
                DateTime last;
                if (hit.CooldownSeconds > 0
                    && _triggerLastFired.TryGetValue(key, out last)
                    && (now - last).TotalSeconds < hit.CooldownSeconds)
                    continue;
                _triggerLastFired[key] = now;
                DispatchTrigger(hit, line);
            }
        }

        private void DispatchTrigger(TriggerHit hit, string line)
        {
            switch (hit.Action)
            {
                case HighlightAction.SendCommand:
                    {
                        string cmd = hit.Argument;
                        if (string.IsNullOrWhiteSpace(cmd)) return;
                        try
                        {
                            TrySendInput(cmd, isCommandLine: true);   // 危险命令闸门照样生效
                            NotificationCenterPanel.Push("info", L10n.T("触发器已发送命令：") + cmd);
                        }
                        catch (Exception ex) { DiagLog.Swallowed("TerminalControl.TriggerSend", ex); }
                        break;
                    }
                case HighlightAction.Notify:
                    {
                        string body = string.IsNullOrWhiteSpace(hit.Argument) ? (line ?? "").Trim() : hit.Argument;
                        string title = string.IsNullOrWhiteSpace(hit.RuleName) ? L10n.T("触发器") : hit.RuleName;
                        try { NotificationCenterPanel.Push(title, body); }
                        catch (Exception ex) { DiagLog.Swallowed("TerminalControl.TriggerNotify", ex); }
                        try { ToastNotifier.Show(title + "：" + body, ToastNotifier.Level.Warning, 4500, false); }
                        catch (Exception ex) { DiagLog.Swallowed("TerminalControl.TriggerToast", ex); }
                        break;
                    }
                case HighlightAction.SwitchTab:
                    {
                        var arg = (hit.Argument ?? string.Empty).Trim();
                        if (arg.Length == 0) return;
                        var h = TriggerTabSwitchRequested;
                        if (h != null) { try { h(this, arg); } catch (Exception ex) { DiagLog.Swallowed("TerminalControl.TriggerSwitchTab", ex); } }
                        break;
                    }
            }
        }
    }
}
