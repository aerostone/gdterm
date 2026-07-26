using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签重连协调——关签/开签编排 + 懒连接就绪轮询（finding-07 / finding-10）。
    /// </summary>
    public sealed class TabReconnectService
    {
        public const int DefaultTimeoutSeconds = 20;
        public const int PollIntervalMs = 200;

        /// <summary>
        /// 重连当前选中标签：关闭后按缓存 config/cred 重开，并等待真实连接。
        /// </summary>
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

        /// <summary>
        /// 按 connectionId 重连：先关同 Id 标签，必要时从 store 取配置，再等待真实连接。
        /// </summary>
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

        /// <summary>
        /// 在 OpenConnection 之后：回填凭据，强制终端连接并等待就绪。
        /// </summary>
        public bool CompleteAfterOpen(
            TabSessionState session,
            CredentialPayload credential,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null) return false;

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
                {
                    return WaitForTerminalConnected(session, tc, onTerminalConnected, timeoutSeconds);
                }

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

            // 非终端/非 RDP 延迟连接：仅表示标签已重建，不算连接成功
            return false;
        }

        /// <summary>强制 ResumeRendering 并轮询 IsConnected。</summary>
        public bool WaitForTerminalConnected(
            TabSessionState session,
            TerminalControl terminal,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null || terminal == null) return false;

            terminal.ResumeRendering();
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (terminal.IsConnected)
                {
                    session.IsConnected = true;
                    if (onTerminalConnected != null)
                        onTerminalConnected(session, terminal.Session);
                    return true;
                }
                System.Threading.Thread.Sleep(PollIntervalMs);
                Application.DoEvents();
            }

            if (terminal.IsConnected)
            {
                session.IsConnected = true;
                if (onTerminalConnected != null)
                    onTerminalConnected(session, terminal.Session);
                return true;
            }
            return false;
        }
    }
}
