using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.Logging.Models;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 标签会话生命周期辅助——登录脚本、健康监控、隧道最后用户关闭（finding-10）。
    /// TabContainer 仍持有 TabSession 字典与 UI 控件，业务策略集中到此处。
    /// </summary>
    public sealed class TabSessionLifecycle
    {
        private readonly IAuditLogger _auditLogger;
        private readonly AutoReconnectWatchdog _reconnectWatchdog;
        private LogonScriptStore _logonScriptStore;

        public TabSessionLifecycle(
            IAuditLogger auditLogger,
            AutoReconnectWatchdog reconnectWatchdog)
        {
            _auditLogger = auditLogger;
            _reconnectWatchdog = reconnectWatchdog;
        }

        public void TryRunLogonScript(TerminalControl terminal, ConnectionConfig config)
        {
            if (terminal == null || config == null || string.IsNullOrEmpty(config.Id)) return;
            try
            {
                if (_logonScriptStore == null)
                {
                    var path = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "data", "config", "logon-scripts.json");
                    _logonScriptStore = new LogonScriptStore(path);
                }
                var scripts = _logonScriptStore.Load();
                if (scripts == null || scripts.Count == 0) return;
                LogonScript match = null;
                foreach (var s in scripts)
                {
                    if (s == null || !s.Enabled) continue;
                    if (string.Equals(s.AssociatedConnectionId, config.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        match = s;
                        break;
                    }
                }
                if (match == null || match.Steps == null || match.Steps.Count == 0) return;
                var session = terminal.Session;
                if (session == null || !session.IsConnected) return;
                var engine = new LogonScriptEngine();
                Task.Run(async () =>
                {
                    try
                    {
                        await engine.ExecuteAsync(match, session).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            _auditLogger?.LogSecurityEvent(
                                SecurityEvent.ApplicationError,
                                "logon script failed on " + config.Host + ": " + ex.Message);
                        }
                        catch { }
                    }
                    finally
                    {
                        try { engine.Dispose(); } catch { }
                    }
                });
            }
            catch { }
        }

        /// <summary>
        /// 为会话挂健康监控与自动重连 Watch。
        /// onLost 可选，默认 NotifyConnectionLost(sessionId)。
        /// </summary>
        public ConnectionHealthMonitor WireHealthAndReconnect(
            string sessionId,
            ITerminalSession session,
            ConnectionHealthMonitor previous,
            Action<string> onLost = null)
        {
            if (session == null) return null;
            try { previous?.Dispose(); } catch { }

            var monitor = new ConnectionHealthMonitor(session)
            {
                MaxHistoryEntries = 120,
                IsPaused = false
            };
            monitor.ConnectionLost += host =>
            {
                if (onLost != null) onLost(sessionId);
                else _reconnectWatchdog?.NotifyConnectionLost(sessionId);
            };
            monitor.Start(5000);
            _reconnectWatchdog?.Watch(sessionId, session);
            return monitor;
        }

        /// <summary>
        /// 若 connectionId 已无其他标签使用，则关闭隧道。
        /// remainingConnectionIds 为关闭当前标签后仍存活的连接 Id 集合。
        /// </summary>
        public void CloseTunnelIfLastUser(
            ITunnelManager tunnelManager,
            string connectionId,
            IEnumerable<string> remainingConnectionIds)
        {
            if (tunnelManager == null || string.IsNullOrEmpty(connectionId)) return;
            var stillUsing = false;
            if (remainingConnectionIds != null)
            {
                foreach (var id in remainingConnectionIds)
                {
                    if (id == connectionId)
                    {
                        stillUsing = true;
                        break;
                    }
                }
            }
            if (stillUsing) return;
            try
            {
                tunnelManager.CloseAsync(connectionId).GetAwaiter().GetResult();
            }
            catch { /* best-effort */ }
        }

        public void LogConnectionClose(string connectionId, string host, string protocol)
        {
            try
            {
                _auditLogger?.LogConnection(connectionId, host, protocol, ConnectionAction.Close);
            }
            catch { }
        }
    }
}
