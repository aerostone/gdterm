using System;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.Core.Models;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 侧栏/终端工具面板工厂——从 MainForm 抽出 Create*Panel 逻辑（finding-10）。
    /// MainForm 只负责宿主布局与菜单触发。
    /// </summary>
    public sealed class SidePanelFactory
    {
        private readonly TabContainerControl _tabs;
        private readonly ActiveSessionBridge _bridge;
        private readonly ToolRegistry _toolRegistry;
        private readonly SecretScanner _secretScanner;
        private readonly MultiChannelManager _multiChannelManager;
        private readonly MultiChannelRecorder _syncRecorder;
        private readonly DangerousCommandDetector _dangerousDetector;
        private readonly IAuditLogger _auditLogger;
        private readonly CommandHistoryStore _commandHistoryStore;
        private readonly HighlightStore _highlightStore;
        private readonly TerminalKeyBindingStore _keyBindingStore;
        private readonly QuickCommandStore _quickCommandStore;
        private readonly IBookmarkStore _bookmarkStore;
        private readonly IConnectionStore _connectionStore;
        private readonly IWin32Window _dialogOwner;
        private readonly ISecurityManager _securityManager;

        public SidePanelFactory(
            TabContainerControl tabs,
            ActiveSessionBridge bridge,
            ToolRegistry toolRegistry,
            SecretScanner secretScanner,
            MultiChannelManager multiChannelManager,
            DangerousCommandDetector dangerousDetector,
            IAuditLogger auditLogger,
            CommandHistoryStore commandHistoryStore,
            HighlightStore highlightStore,
            TerminalKeyBindingStore keyBindingStore,
            QuickCommandStore quickCommandStore,
            IBookmarkStore bookmarkStore,
            IConnectionStore connectionStore,
            IWin32Window dialogOwner,
            ISecurityManager securityManager = null,
            MultiChannelRecorder syncRecorder = null)
        {
            _tabs = tabs;
            _bridge = bridge;
            _toolRegistry = toolRegistry;
            _secretScanner = secretScanner;
            _multiChannelManager = multiChannelManager;
            _dangerousDetector = dangerousDetector;
            _auditLogger = auditLogger;
            _commandHistoryStore = commandHistoryStore;
            _highlightStore = highlightStore;
            _keyBindingStore = keyBindingStore;
            _quickCommandStore = quickCommandStore;
            _bookmarkStore = bookmarkStore;
            _connectionStore = connectionStore;
            _dialogOwner = dialogOwner;
            _securityManager = securityManager;
            _syncRecorder = syncRecorder;
        }

        public Control CreateToolboxPanel()
        {
            if (_toolRegistry == null)
                return Unavailable("工具箱未初始化");
            var panel = new ToolboxPanel(_toolRegistry);
            if (_bridge != null) _bridge.BindToolbox(panel);
            else
            {
                try { panel.SetRemoteSession(_tabs.GetActiveRemoteSession()); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelFactory", exSwallowed); } catch { } }
            }
            return panel;
        }

        public Control CreateSecretScanPanel()
        {
            if (_secretScanner == null)
                return Unavailable("扫描器未初始化");
            return new SecretScanPanel(_secretScanner, _securityManager);
        }

        public Control CreateMultiChannelPanel()
        {
            SyncMultiChannelRegistrations();
            var panel = new MultiChannelPanel(_multiChannelManager, _syncRecorder);
            panel.BroadcastCommandRequested += OnBroadcastCommandRequested;
            return panel;
        }

        public Control CreateMacroPanel()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "macros");
            return new MacroPanel(dir, () => _tabs != null ? _tabs.GetActiveTerminalControl() : null);
        }

        public Control CreateBatchPanel()
        {
            var panel = new BatchCommandPanel();
            panel.SetDangerousDetector(_dangerousDetector);
            if (_tabs != null)
                panel.SetSessions(_tabs.GetConnectedSessions());
            return panel;
        }

        public Control CreateHistoryPanel()
        {
            if (_commandHistoryStore == null)
                return Unavailable("命令历史未初始化");
            return new CommandHistoryPanel(_commandHistoryStore);
        }

        public Control CreateAiChatPanel(Func<TerminalControl> getActiveTerminal, IAiAssistantService aiService)
        {
            if (aiService == null)
                return Unavailable("AI 服务未初始化");
            return new AiChatPanel(aiService, getActiveTerminal ?? (() => _tabs != null ? _tabs.GetActiveTerminalControl() : null));
        }

        public Control CreateCommandTemplatePanel(Func<TerminalControl> getActiveTerminal, CommandTemplateStore store)
        {
            if (store == null)
                return Unavailable("命令模板库未初始化");
            return new CommandTemplatePanel(store, getActiveTerminal ?? (() => _tabs != null ? _tabs.GetActiveTerminalControl() : null));
        }

        public Control CreateHealthPanel()
        {
            var panel = new HealthMonitorPanel();
            var mon = _bridge != null ? _bridge.GetHealthMonitor() : _tabs?.GetActiveHealthMonitor();
            if (mon != null) panel.SetMonitor(mon);
            return panel;
        }

        public Control CreatePortForwardPanel()
        {
            try
            {
                var mgr = new PortForwardManager();
                var panel = new PortForwardPanel(mgr);
                if (_bridge != null) _bridge.BindPortForward(panel);
                else
                {
                    var host = _tabs?.GetActivePortForwardHost();
                    if (host != null) panel.SetPortForwardHost(host);
                }
                return panel;
            }
            catch (Exception ex)
            {
                return Unavailable("端口转发不可用: " + ex.Message);
            }
        }

        public Control CreateHighlightPanel()
        {
            if (_highlightStore == null)
                return Unavailable("高亮存储未初始化");
            return new HighlightRulePanel(_highlightStore);
        }

        public Control CreateKeyBindingPanel()
        {
            if (_keyBindingStore == null)
                return Unavailable("快捷键存储未初始化");
            return new KeyBindingPanel(_keyBindingStore);
        }

        public Control CreateLogonScriptPanel()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "logon-scripts.json");
                var store = new LogonScriptStore(path);
                return new LogonScriptPanel(store);
            }
            catch (Exception ex)
            {
                return Unavailable("登录脚本: " + ex.Message);
            }
        }

        public Control CreateSnippetSearchPanel(Action<string> onSend)
        {
            List<QuickCommand> cmds = null;
            try { cmds = _quickCommandStore?.LoadAll(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelFactory", exSwallowed); } catch { } }
            var panel = new SnippetSearchPanel(cmds ?? new List<QuickCommand>());
            var snipTc = _tabs?.GetActiveTerminalControl();
            if (snipTc != null)
                panel.SetActiveTerminal(snipTc);
            else
                panel.SetActiveSession(_tabs?.GetActiveSession());
            panel.SnippetSent += (cmd, qc) =>
            {
                if (onSend != null) onSend(cmd);
            };
            return panel;
        }

        /// <summary>把终端搜索条挂到 tab 容器顶部。</summary>
        public void AttachSearchBar(Control tabHost)
        {
            if (tabHost == null) return;
            var tc = _tabs?.GetActiveTerminalControl();
            if (tc == null)
            {
                MessageBox.Show(_dialogOwner, "请先打开终端标签", "查找",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var bar = new TerminalSearchBar
            {
                Dock = DockStyle.Top,
                Height = 32
            };
            tabHost.Controls.Add(bar);
            bar.BringToFront();
            bar.ShowAndFocus();
            // 查找接线（F-查找）：SearchRequested 用 TerminalSearchEngine 跑缓冲匹配并计数，
            // NavigateRequested 切上/下一个：计数切换 + Cell 视口滚动到匹配行。
            Gdterm.Terminal.TerminalSearchEngine engine = null;
            bar.SearchRequested += (pattern, caseSensitive, useRegex, wholeWord) =>
            {
                try
                {
                    var lines = tc.GetRecentLines(500);
                    engine = new Gdterm.Terminal.TerminalSearchEngine(new System.Collections.Generic.List<string>(lines));
                    int n = engine.Search(pattern, caseSensitive, useRegex, wholeWord);
                    int cur = engine.HasResults ? engine.CurrentIndex : -1;
                    bar.UpdateMatchCount(cur, n);
                }
                catch { bar.UpdateMatchCount(-1, 0); }
            };
            bar.NavigateRequested += (next) =>
            {
                try
                {
                    if (engine == null || !engine.HasResults) return;
                    var m = next ? engine.Next() : engine.Previous();
                    if (m == null) return;
                    bar.UpdateMatchCount(engine.CurrentIndex, engine.TotalMatches);
                    // 跳行：用匹配行文本锚滚动 Cell 视口（Lightweight 已可见，无操作）
                    try
                    {
                        string anchor = m.LineText ?? "";
                        anchor = anchor.Trim();
                        if (anchor.Length > 40) anchor = anchor.Substring(0, 40);
                        if (anchor.Length > 0) tc.ScrollToMatch(anchor, next);
                    }
                    catch { }
                }
                catch { }
            };
            bar.CloseRequested += () =>
            {
                tabHost.Controls.Remove(bar);
                bar.Dispose();
            };
        }

        /// <summary>多通道：注销离线会话，注册当前在线会话。</summary>
        public void SyncMultiChannelRegistrations()
        {
            if (_multiChannelManager == null || _tabs == null) return;
            try
            {
                var all = _tabs.GetConnectedSessions();
                var liveIds = new HashSet<string>(all.Keys);
                foreach (var info in _multiChannelManager.GetAllSessions())
                {
                    if (info != null && !liveIds.Contains(info.SessionId))
                        _multiChannelManager.Unregister(info.SessionId);
                }
                foreach (var kv in all)
                {
                    // 显示名用人话：Hostname（RDP/SSH 来自 config.Host，串口是端口名，本地是 localhost），
                    // 之前直接拿 kv.Key（sessionId/connectionId）当显示名，多通道列表里全是 Guid 味的字符串。
                    string displayName = kv.Key;
                    try
                    {
                        var hn = kv.Value != null ? kv.Value.Hostname : null;
                        if (!string.IsNullOrEmpty(hn)) displayName = hn;
                    }
                    catch { displayName = kv.Key; }
                    _multiChannelManager.Register(kv.Key, kv.Value, displayName, null);
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelFactory", exSwallowed); } catch { } }
        }

        private void OnBroadcastCommandRequested(object sender, string cmd)
        {
            if (string.IsNullOrEmpty(cmd) || _multiChannelManager == null) return;
            if (_dangerousDetector != null)
            {
                var check = _dangerousDetector.Check(cmd);
                if (check != null && check.IsDangerous)
                {
                    using (var dlg = new DangerousCommandDialog(cmd, check))
                    {
                        dlg.ShowDialog(_dialogOwner);
                        if (!dlg.IsConfirmed)
                        {
                            try
                            {
                                _auditLogger?.LogSecurityEvent(
                                    SecurityEvent.DangerousCommandBlocked,
                                    "broadcast blocked: " + cmd);
                            }
                            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelFactory", exSwallowed); } catch { } }
                            return;
                        }
                        if (dlg.RememberChoice)
                        {
                            try { _dangerousDetector.AddToWhitelist(cmd); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelFactory", exSwallowed); } catch { } }
                        }
                    }
                }
            }
            _multiChannelManager.BroadcastCommand(cmd + "\r");
            try
            {
                _commandHistoryStore?.RecordCommand(new CommandHistoryEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = cmd,
                    ExecutedAt = DateTime.UtcNow,
                    IsBroadcast = true
                });
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SidePanelFactory", exSwallowed); } catch { } }
        }

        public Control CreateBookmarksPanel(Action<ConnectionConfig> onOpen)
        {
            if (_bookmarkStore == null)
                return Unavailable("书签存储未初始化");
            var panel = new SessionBookmarksPanel(_bookmarkStore, _connectionStore);
            if (onOpen != null)
                panel.OpenConnectionRequested += onOpen;
            return panel;
        }

        public Control CreateTransferCenterPanel()
        {
            return new Gdterm.UI.Controls.TransferCenterPanel();
        }

        public Control CreateNotificationCenterPanel()
        {
            return new Gdterm.UI.Controls.NotificationCenterPanel();
        }

        private static Control Unavailable(string text)
        {
            return new AntdUI.Label
            {
                Text = text,
                Dock = DockStyle.Fill
            };
        }
    }
}
