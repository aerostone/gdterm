using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签重连协调——异步就绪轮询，禁止 UI 线程 GetResult 死锁（go-live P0-01）。
    /// </summary>
    public sealed class TabReconnectService
    {
        public const int DefaultTimeoutSeconds = 20;
        public const int PollIntervalMs = 200;

        /// <summary>同时进行中的重连数上限（P1-06 资源闸门）。</summary>
        public int MaxConcurrentReconnects { get; set; } = 3;

        private int _inflight;

        public Task<bool> ReconnectActiveAsync(
            TabPage selectedTab,
            IDictionary<TabPage, TabSessionState> sessions,
            Action<TabPage> closeTab,
            Action<ConnectionConfig> openConnection,
            Func<TabPage> getSelectedTab,
            Action<TabSessionState, ITerminalSession> onTerminalConnected)
        {
            if (selectedTab == null || sessions == null || closeTab == null || openConnection == null)
                return Task.FromResult(false);

            TabSessionState session;
            if (!sessions.TryGetValue(selectedTab, out session) || session == null)
                return Task.FromResult(false);

            var config = session.Config;
            var cred = session.Credential;
            closeTab(selectedTab);

            if (config == null) return Task.FromResult(false);
            openConnection(config);

            var tab = getSelectedTab != null ? getSelectedTab() : null;
            if (tab == null || !sessions.TryGetValue(tab, out var newSession) || newSession == null)
                return Task.FromResult(false);

            return CompleteAfterOpenAsync(newSession, cred, onTerminalConnected);
        }

        public Task<bool> ReconnectByIdAsync(
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
                return Task.FromResult(false);

            if (!TryEnterReconnectGate())
            {
                DiagLog.Swallowed("TabReconnect.GateFull",
                    new InvalidOperationException("concurrent reconnect limit: " + MaxConcurrentReconnects));
                return Task.FromResult(false);
            }

            try
            {
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

                if (config == null)
                {
                    ExitReconnectGate();
                    return Task.FromResult(false);
                }

                openConnection(config);

                var selected = getSelectedTab != null ? getSelectedTab() : null;
                if (selected == null || !sessions.TryGetValue(selected, out var newSession) || newSession == null)
                {
                    ExitReconnectGate();
                    return Task.FromResult(false);
                }

                return CompleteAfterOpenAsync(newSession, cred, onTerminalConnected)
                    .ContinueWith(t =>
                    {
                        ExitReconnectGate();
                        return t.Status == TaskStatus.RanToCompletion && t.Result;
                    });
            }
            catch (Exception ex)
            {
                ExitReconnectGate();
                DiagLog.Swallowed("TabReconnect.ById", ex);
                return Task.FromResult(false);
            }
        }

        /// <summary>同步包装仅供非 UI 关键路径；UI 请用 *Async。</summary>
        public bool ReconnectActive(
            TabPage selectedTab,
            IDictionary<TabPage, TabSessionState> sessions,
            Action<TabPage> closeTab,
            Action<ConnectionConfig> openConnection,
            Func<TabPage> getSelectedTab,
            Action<TabSessionState, ITerminalSession> onTerminalConnected)
        {
            // 不在 UI 上 GetResult：启动异步并立即返回 true（已发起）
            var task = ReconnectActiveAsync(selectedTab, sessions, closeTab, openConnection,
                getSelectedTab, onTerminalConnected);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    DiagLog.Swallowed("TabReconnect.Active", t.Exception.GetBaseException());
            }, TaskContinuationOptions.OnlyOnFaulted);
            return true;
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
            var task = ReconnectByIdAsync(connectionId, sessions, tabs, closeTab, openConnection,
                getSelectedTab, onTerminalConnected, connectionStore);
            // Watchdog 路径应 await Async 版本；此处同步包装仅 fire-and-forget 时返回 false 避免假成功
            try
            {
                if (task.IsCompleted)
                    return task.Result;
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TabReconnect.ByIdSync", ex);
            }
            return false;
        }

        public Task<bool> CompleteAfterOpenAsync(
            TabSessionState session,
            CredentialPayload credential,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null) return Task.FromResult(false);

            if (credential != null)
            {
                session.Credential = credential;
                var terminals = new List<TerminalControl>();
                TabActiveSessionQuery.CollectSessionTerminals(session, terminals);
                foreach (var tcCred in terminals)
                {
                    if (tcCred != null)
                        tcCred.Credentials = credential;
                }
            }

            try
            {
                var tc = TabActiveSessionQuery.ResolveTerminal(session);
                if (tc != null)
                    return WaitForTerminalConnectedAsync(session, tc, onTerminalConnected, timeoutSeconds);

                if (session.PendingConnect != null)
                {
                    var connect = session.PendingConnect;
                    session.PendingConnect = null;
                    connect();
                    return Task.FromResult(session.IsConnected);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TabReconnect.CompleteAfterOpen", ex);
                return Task.FromResult(false);
            }

            return Task.FromResult(false);
        }

        public bool CompleteAfterOpen(
            TabSessionState session,
            CredentialPayload credential,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            var task = CompleteAfterOpenAsync(session, credential, onTerminalConnected, timeoutSeconds);
            if (task.IsCompleted)
            {
                try { return task.Result; }
                catch (Exception ex)
                {
                    DiagLog.Swallowed("TabReconnect.CompleteSync", ex);
                    return false;
                }
            }
            // 未完成：不阻塞 UI
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    DiagLog.Swallowed("TabReconnect.CompleteAsync", t.Exception.GetBaseException());
            });
            return false;
        }

        /// <summary>
        /// 异步等待终端就绪（P0-01）：绝不在 UI 线程 GetResult。
        /// Connect 用 ConfigureAwait(false) 路径，轮询在线程池。
        /// </summary>
        public async Task<bool> WaitForTerminalConnectedAsync(
            TabSessionState session,
            TerminalControl terminal,
            Action<TabSessionState, ITerminalSession> onTerminalConnected,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (session == null || terminal == null) return false;

            try
            {
                // 触发连接但不等待 UI 同步上下文
                terminal.ResumeRendering();
                // 若公开 ConnectAsync，优先 await
                var connectTask = terminal.ConnectAsyncIfNeeded();
                if (connectTask != null)
                {
                    var timeout = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
                    var delay = Task.Delay(TimeSpan.FromSeconds(timeout));
                    var done = await Task.WhenAny(connectTask, delay).ConfigureAwait(false);
                    if (done != connectTask)
                        return false;
                    try { await connectTask.ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        DiagLog.Swallowed("TabReconnect.ConnectAsync", ex);
                        return false;
                    }
                }
                else
                {
                    var timeout = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
                    var deadline = DateTime.UtcNow.AddSeconds(timeout);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (terminal.IsConnected)
                            break;
                        await Task.Delay(PollIntervalMs).ConfigureAwait(false);
                    }
                }

                if (!terminal.IsConnected)
                    return false;

                session.IsConnected = true;
                if (onTerminalConnected != null)
                    onTerminalConnected(session, terminal.Session);
                return true;
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("TabReconnect.Wait", ex);
                return false;
            }
        }

        private bool TryEnterReconnectGate()
        {
            lock (this)
            {
                if (_inflight >= MaxConcurrentReconnects)
                    return false;
                _inflight++;
                return true;
            }
        }

        private void ExitReconnectGate()
        {
            lock (this)
            {
                if (_inflight > 0) _inflight--;
            }
        }
    }
}
