using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.Tools;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 活动标签查询——从 TabSessionState 字典解析终端/SSH 宿主。
    /// finding-05：分屏时从 SplitPane 递归取 TerminalControl。
    /// finding-11：通过 ITerminalSession.TryGetSshClient 取桥接，不转 TerminalSession。
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
            if (!_sessions.TryGetValue(tab, out session) || session == null) return null;
            return ResolveTerminal(session);
        }

        public ITerminalSession GetActiveSession()
        {
            return GetActiveTerminalControl()?.Session;
        }

        public ISshPortForwardHost GetActivePortForwardHost()
        {
            var session = GetActiveSession();
            if (session == null) return null;
            return SshPortForwardHost.FromClientObject(session.TryGetSshClient());
        }

        public IList<Gdterm.Core.Models.ConnectionConfig> GetPortForwardConnections()
        {
            var configs = new Dictionary<string, Gdterm.Core.Models.ConnectionConfig>();
            if (_sessions == null) return new List<Gdterm.Core.Models.ConnectionConfig>();
            foreach (var state in _sessions.Values)
            {
                var terminals = new List<TerminalControl>();
                CollectSessionTerminals(state, terminals);
                foreach (var tc in terminals)
                    if (tc?.Config != null && !string.IsNullOrEmpty(tc.Config.Id) &&
                        tc.Config.Protocol == Gdterm.Core.Enums.ProtocolType.SSH && tc.Session?.TryGetSshClient() != null)
                        configs[tc.Config.Id] = tc.Config;
            }
            return new List<Gdterm.Core.Models.ConnectionConfig>(configs.Values);
        }

        public ISshPortForwardHost GetPortForwardHost(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId)) return null;
            var active = GetActiveTerminalControl();
            var host = MatchPortForwardHost(active, connectionId);
            if (host != null) return host;
            if (_sessions == null) return null;
            foreach (var state in _sessions.Values)
            {
                var terminals = new List<TerminalControl>();
                CollectSessionTerminals(state, terminals);
                foreach (var terminal in terminals)
                {
                    host = MatchPortForwardHost(terminal, connectionId);
                    if (host != null) return host;
                }
            }
            return null;
        }

        private static ISshPortForwardHost MatchPortForwardHost(TerminalControl terminal, string id)
        {
            if (terminal == null || terminal.IsDisposed || terminal.Config?.Id != id || !terminal.IsConnected) return null;
            var host = SshPortForwardHost.FromClientObject(terminal.Session?.TryGetSshClient());
            return host != null && host.IsConnected ? host : null;
        }

        public ISshRemoteSession GetActiveRemoteSession()
        {
            var session = GetActiveSession();
            if (session == null) return null;
            return SshNetRemoteSession.FromClientObject(session.TryGetSshClient());
        }

        public Dictionary<string, ITerminalSession> GetConnectedSessions()
        {
            var map = new Dictionary<string, ITerminalSession>();
            if (_sessions == null) return map;
            foreach (var kvp in _sessions)
            {
                if (kvp.Value == null) continue;
                var terminals = new List<TerminalControl>();
                CollectSessionTerminals(kvp.Value, terminals);
                foreach (var tc in terminals)
                {
                    if (tc == null || tc.Session == null || !tc.IsConnected) continue;
                    var id = kvp.Value.SessionId ?? kvp.Value.Config?.Id ?? Guid.NewGuid().ToString("N");
                    // 分屏多终端：用 connectionId + 控件 hash 区分
                    if (terminals.Count > 1)
                        id = id + "-" + tc.GetHashCode().ToString("X");
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

        internal static TerminalControl ResolveTerminal(TabSessionState session)
        {
            if (session == null) return null;
            if (session.PrimaryTerminal != null && !session.PrimaryTerminal.IsDisposed)
                return session.PrimaryTerminal;

            var direct = session.Control as TerminalControl;
            if (direct != null) return direct;

            return SplitPaneControl.FindFirstTerminal(session.Control);
        }

        internal static void CollectSessionTerminals(TabSessionState session, IList<TerminalControl> into)
        {
            if (session == null || into == null) return;
            if (session.Control is TerminalControl)
            {
                into.Add((TerminalControl)session.Control);
                return;
            }
            SplitPaneControl.CollectTerminals(session.Control, into);
            if (into.Count == 0 && session.PrimaryTerminal != null)
                into.Add(session.PrimaryTerminal);
        }
    }
}
