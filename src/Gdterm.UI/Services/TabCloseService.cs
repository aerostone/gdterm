using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Core.Enums;
using Gdterm.KeePass;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签关闭编排——释放 RDP/健康监控、隧道最后用户关闭、审计（finding-10）。
    /// TabContainer 只负责字典持有与 UI 事件转发。
    /// </summary>
    public sealed class TabCloseService
    {
        private readonly TabSessionLifecycle _lifecycle;
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private readonly IKeePassService _keepassService;
        private readonly ITunnelManager _tunnelManager;

        public TabCloseService(
            TabSessionLifecycle lifecycle,
            AutoReconnectWatchdog reconnectWatchdog,
            IKeePassService keepassService,
            ITunnelManager tunnelManager)
        {
            _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
            _reconnectWatchdog = reconnectWatchdog;
            _keepassService = keepassService;
            _tunnelManager = tunnelManager;
        }

        /// <summary>
        /// 关闭单个标签。返回 SessionId（可空）供 SessionClosed 事件使用。
        /// </summary>
        public string CloseTab(
            TabPage tab,
            IDictionary<TabPage, TabSessionState> sessions,
            TabControl tabControl)
        {
            if (tab == null || sessions == null) return null;
            TabSessionState session;
            if (!sessions.TryGetValue(tab, out session) || session == null) return null;

            var sessionId = session.SessionId;
            var connectionId = session.Config != null ? session.Config.Id : null;

            if (!string.IsNullOrEmpty(sessionId))
            {
                DiagLog.Try("TabClose.Unwatch", () => _reconnectWatchdog?.Unwatch(sessionId));
            }

            DiagLog.Try("TabClose.HealthMonitor", () => session.HealthMonitor?.Dispose());

            if (session.Protocol == ProtocolType.RDP)
            {
                DiagLog.Try("TabClose.RdpDispose", () => session.RdpClient?.Dispose());
                DiagLog.Try("TabClose.CleanupRdpCredential", () =>
                {
                    var host = session.Config != null ? session.Config.Host : null;
                    _keepassService?.CleanupRdpCredential(host);
                });
            }

            // finding-05：分屏时释放全部 TerminalControl，再释放 SplitPane
            try
            {
                var terminals = new List<Gdterm.UI.Controls.TerminalControl>();
                TabActiveSessionQuery.CollectSessionTerminals(session, terminals);
                foreach (var tc in terminals)
                {
                    if (tc == null) continue;
                    if (ReferenceEquals(tc, session.Control)) continue; // 下面统一 Dispose Control
                    DiagLog.Try("TabClose.SplitTerminal", () =>
                    {
                        var d = tc as IDisposable;
                        if (d != null) d.Dispose();
                    });
                }
            }
            catch { }

            var disposable = session.Control as IDisposable;
            if (disposable != null)
            {
                DiagLog.Try("TabClose.ControlDispose", () => disposable.Dispose());
            }

            // 先从字典移除，再判断同 connectionId 是否还有其他标签共享隧道
            sessions.Remove(tab);

            if (!string.IsNullOrEmpty(connectionId) && _lifecycle != null)
            {
                var remaining = new List<string>();
                foreach (var other in sessions.Values)
                {
                    if (other != null && other.Config != null && other.Config.Id != null)
                        remaining.Add(other.Config.Id);
                }
                _lifecycle.CloseTunnelIfLastUser(_tunnelManager, connectionId, remaining);
            }

            if (_lifecycle != null)
            {
                var host = session.Config != null
                    ? (session.Config.Host ?? session.Config.Name)
                    : null;
                _lifecycle.LogConnectionClose(
                    connectionId,
                    host,
                    session.Protocol.ToString());
            }

            if (tabControl != null)
            {
                DiagLog.Try("TabClose.RemovePage", () => tabControl.TabPages.Remove(tab));
            }

            return sessionId;
        }

        /// <summary>关闭全部标签并清空字典。</summary>
        public void CloseAllTabs(
            TabControl tabControl,
            IDictionary<TabPage, TabSessionState> sessions)
        {
            if (tabControl == null || sessions == null) return;

            var pages = new List<TabPage>();
            foreach (TabPage tab in tabControl.TabPages)
                pages.Add(tab);

            foreach (var tab in pages)
                CloseTab(tab, sessions, tabControl);

            DiagLog.Try("TabClose.ClearPages", () => tabControl.TabPages.Clear());
            sessions.Clear();
        }
    }
}
