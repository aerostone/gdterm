using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 活动标签查询——从 TabSessionState 字典解析终端/SSH 宿主（finding-10）。
    /// TabContainer 只持有字典与 TabControl，查询逻辑集中在此。
    /// </summary>
    public sealed class TabActiveSessionQuery
    {
        private readonly Func<TabPage> _getSelectedTab;
        private readonly IDictionary<TabPage, TabSessionState> _sessions;

        public TabActiveSessionQuery(
            Func<TabPage> getSelectedTab,
            IDictionary<TabPage, TabSessionState> sessions)
        {
            _getSelectedTab = getSelectedTab;
            _sessions = sessions;
        }

        public TerminalControl GetActiveTerminalControl()
        {
            var tab = _getSelectedTab != null ? _getSelectedTab() : null;
            if (tab == null || _sessions == null) return null;
            TabSessionState session;
            if (!_sessions.TryGetValue(tab, out session)) return null;
            return session.Control as TerminalControl;
        }

        public ITerminalSession GetActiveSession()
        {
            return GetActiveTerminalControl()?.Session;
        }

        public ISshPortForwardHost GetActivePortForwardHost()
        {
            var session = GetActiveSession() as TerminalSession;
            if (session == null || session.UnderlyingClient == null) return null;
            return SshPortForwardHost.Wrap(session.UnderlyingClient);
        }

        public ISshRemoteSession GetActiveRemoteSession()
        {
            var session = GetActiveSession() as TerminalSession;
            if (session == null || session.UnderlyingClient == null) return null;
            return SshNetRemoteSession.Wrap(session.UnderlyingClient);
        }

        public Dictionary<string, ITerminalSession> GetConnectedSessions()
        {
            var map = new Dictionary<string, ITerminalSession>();
            if (_sessions == null) return map;
            foreach (var kvp in _sessions)
            {
                var tc = kvp.Value != null ? kvp.Value.Control as TerminalControl : null;
                if (tc != null && tc.Session != null && tc.IsConnected)
                {
                    var id = kvp.Value.SessionId ?? kvp.Value.Config?.Id ?? Guid.NewGuid().ToString("N");
                    map[id] = tc.Session;
                }
            }
            return map;
        }

        public ConnectionHealthMonitor GetActiveHealthMonitor()
        {
            var tab = _getSelectedTab != null ? _getSelectedTab() : null;
            if (tab == null || _sessions == null) return null;
            TabSessionState session;
            if (!_sessions.TryGetValue(tab, out session)) return null;
            return session.HealthMonitor;
        }
    }
}
