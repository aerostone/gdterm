using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签重连协调——关签/开签编排 + 异步就绪轮询（finding-07：禁止 UI 线程 Sleep+DoEvents）。
    /// </summary>
    public sealed class TabReconnectService
    {
        public const int DefaultTimeoutSeconds = 20;
        public const int PollIntervalMs = 200;

        public bool ReconnectActive(
            TabPage selectedTab,
            IDictionary<TabPage, TabSessionState> sessions,
            Action<TabPage> closeTab,
            Action<ConnectionConfig> openConnection,
            Func<TabPage> getSelectedTab,
            Action<TabSessionState, ITerminalSession> onTerminalConnected)
        {
            if (selectedTab == null || sessions == null || closeTab == null || openConnection == null)
                return false;

            TabSessionState session;
            if (!sessions.TryGetValue(selectedTab, out session) || session == null)
                return false;

            var config = session.Config;
            var cred = session.Credential;
            closeTab(selectedTab);

            if (config == null) return false;
            openConnection(config);

            var tab = getSelectedTab != null ? getSelectedTab() : null;
            if (tab == null || !sessions.TryGetValue(tab, out var newSession) || newSession == null)
                return false;

            return CompleteAfterOpen(newSession, cred, onTerminalConnected);
        }

        public bool ReconnectById(
            string connectionId,
            IDictionary<TabPage, TabSessionState> sessions,
            IEnumerable<TabPage> tabs,
            Action<TabPage> closeTab,
            Action<ConnectionConfig> openConnection,
            Func<TabPage> getSelectedTab,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            IConnectionStore connectionStore)
        {
            if (string.IsNullOrEmpty(connectionId) || sessions == null || closeTab == null || openConnection == null)
                return false;

            ConnectionConfig config = null;
            CredentialPayload cred = null;

            if (tabs != null)
            {
                foreach (var tab in new List<TabPage>(tabs))
                {
                    TabSessionState session;
                    if (sessions.TryGetValue(tab, out session) &&
                        session != null &&
                        session.Config != null &&
                        session.Config.Id == connectionId)
                    {
                        config = session.Config;
                        cred = session.Credential;
                        closeTab(tab);
                        break;
                    }
                }
            }

            if (config == null && connectionStore != null)
                config = connectionStore.GetById(connectionId);

            if (config == null) return false;

            openConnection(config);

            var selected = getSelectedTab != null ? getSelectedTab() : null;
            if (selected == null || !sessions.TryGetValue(selected, out var newSession) || newSession == null)
                return false;

            return CompleteAfterOpen(newSession, cred, onTerminalConnected);
        }

        public bool CompleteAfterOpen(
            TabSessionState session,
            CredentialPayload credential,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null) return false;

            // finding-04：无缓存凭据时不回填
            if (credential != null)
            {
                session.Credential = credential;
                var tcCred = session.Control as TerminalControl;
                if (tcCred != null)
                    tcCred.Credentials = credential;
            }

            try
            {
                var tc = session.Control as TerminalControl;
                if (tc != null)
                    return WaitForTerminalConnected(session, tc, onTerminalConnected, timeoutSeconds);

                if (session.PendingConnect != null)
                {
                    var connect = session.PendingConnect;
                    session.PendingConnect = null;
                    connect();
                    return session.IsConnected;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// ResumeRendering 后在线程池轮询 IsConnected（finding-07）。
        /// 无 Thread.Sleep、无 Application.DoEvents，避免消息泵重入。
        /// UI 线程调用时仍会同步等待（最多 timeout），但不会重入关签逻辑。
        /// </summary>
        public bool WaitForTerminalConnected(
            TabSessionState session,
            TerminalControl terminal,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null || terminal == null) return false;

            terminal.ResumeRendering();
            var timeout = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;

            bool connected;
            try
            {
                connected = Task.Run(async () =>
                {
                    var deadline = DateTime.UtcNow.AddSeconds(timeout);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (terminal.IsConnected)
                            return true;
                        await Task.Delay(PollIntervalMs).ConfigureAwait(false);
                    }
                    return terminal.IsConnected;
                }).GetAwaiter().GetResult();
            }
            catch
            {
                connected = terminal.IsConnected;
            }

            if (!connected)
                return false;

            session.IsConnected = true;
            if (onTerminalConnected != null)
                onTerminalConnected(session, terminal.Session);
            return true;
        }
    }
}
