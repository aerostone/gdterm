using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Tunnel.Exceptions;
using Gdterm.Tunnel.Models;
using Renci.SshNet;
using Gdterm.Core;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// 隧道管理器——管理 SSH 隧道的建立、查询和关闭。
    /// go-live P1-04：同一 connectionId 并发 Establish 原子化（单飞）。
    /// </summary>
    public class TunnelManager : ITunnelManager
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, TunnelSession> _sessions = new Dictionary<string, TunnelSession>();
        private readonly Dictionary<string, PendingTunnel> _pending = new Dictionary<string, PendingTunnel>();
        private bool _disposed;

        private sealed class PendingTunnel
        {
            public CancellationTokenSource Cancellation;
            public Task<TunnelEndpoint> Task;
        }

        public Task<TunnelEndpoint> EstablishAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            ct.ThrowIfCancellationRequested();
            if (config.JumpChain != null && (config.JumpChain.Hops == null || config.JumpChain.Hops.Count == 0))
                throw new InvalidOperationException(L10n.T("已启用跳板，请至少添加一台跳板机。"));
            var id = string.IsNullOrEmpty(config.Id) ? Guid.NewGuid().ToString("N") : config.Id;
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(TunnelManager));
                TunnelSession existing;
                if (_sessions.TryGetValue(id, out existing) && existing.IsActive)
                    return Task.FromResult(Endpoint(existing));
                PendingTunnel pending;
                if (_pending.TryGetValue(id, out pending)) return pending.Task;
                pending = new PendingTunnel { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct) };
                // Register before running any connection work. GetOrAdd factories can run more than once.
                _pending.Add(id, pending);
                pending.Task = Task.Run(() => EstablishCore(config, credential, id, pending));
                return pending.Task;
            }
        }

        private TunnelEndpoint EstablishCore(ConnectionConfig config, CredentialPayload credential, string id, PendingTunnel pending)
        {
            var ct = pending.Cancellation.Token;
            var session = new TunnelSession(id);
            TunnelSession old = null;
            bool published = false;
            try
            {
                lock (_sync)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_sessions.TryGetValue(id, out old)) _sessions.Remove(id);
                }
                old?.Dispose();
                ct.ThrowIfCancellationRequested();
                if (config.JumpChain == null) EstablishDirect(session, config, credential, ct);
                else EstablishWithJumpChain(session, config, credential, ct);
                lock (_sync)
                {
                    ct.ThrowIfCancellationRequested();
                    PendingTunnel current;
                    if (_disposed || !_pending.TryGetValue(id, out current) || !ReferenceEquals(current, pending))
                        throw new OperationCanceledException(ct);
                    if (!session.IsActive) throw new InvalidOperationException(session.LastError);
                    _sessions[id] = session;
                    published = true;
                    return Endpoint(session);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (TunnelException) { throw; }
            catch (Exception ex)
            {
                throw new TunnelException(L10n.T("隧道建立失败: ") + ex.Message, -1, config.Host, config.Port, ex);
            }
            finally
            {
                if (!published) session.Dispose();
                lock (_sync)
                {
                    PendingTunnel current;
                    if (_pending.TryGetValue(id, out current) && ReferenceEquals(current, pending)) _pending.Remove(id);
                    pending.Cancellation.Dispose();
                }
            }
        }

        private static TunnelEndpoint Endpoint(TunnelSession session)
        {
            return new TunnelEndpoint { LocalHost = session.LocalHost, LocalPort = session.LocalPort, ConnectionId = session.ConnectionId };
        }

        public Task CloseAsync(string connectionId)
        {
            TunnelSession session = null;
            lock (_sync)
            {
                if (connectionId == null) return Task.CompletedTask;
                if (_sessions.TryGetValue(connectionId, out session)) _sessions.Remove(connectionId);
                PendingTunnel pending;
                if (_pending.TryGetValue(connectionId, out pending))
                {
                    _pending.Remove(connectionId);
                    pending.Cancellation.Cancel();
                }
            }
            session?.Dispose();
            return Task.CompletedTask;
        }

        public TunnelStatus GetStatus(string connectionId)
        {
            lock (_sync)
            {
                TunnelSession session;
                if (connectionId != null && _sessions.TryGetValue(connectionId, out session))
                {
                    bool active = session.IsActive;
                    return new TunnelStatus { IsActive = active, LastError = session.LastError,
                        Uptime = active ? DateTime.UtcNow - session.EstablishedAt : TimeSpan.Zero };
                }
                return new TunnelStatus { IsActive = false, LastError = L10n.T("隧道不存在"), Uptime = TimeSpan.Zero };
            }
        }

        public void Dispose()
        {
            List<TunnelSession> sessions;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = new List<TunnelSession>(_sessions.Values);
                _sessions.Clear();
                foreach (var pending in _pending.Values) pending.Cancellation.Cancel();
                _pending.Clear();
            }
            foreach (var session in sessions) session.Dispose();
        }

        private void EstablishDirect(TunnelSession session, ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                session.ConnectDirect(config, credential);
            }
            catch (Exception ex)
            {
                throw new TunnelException(
                    L10n.Tf("直连 {0}:{1} 失败: {2}", config.Host, config.Port, ex.Message),
                    hopIndex: -1,
                    host: config.Host,
                    port: config.Port,
                    innerException: ex);
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                session.StartPortForwarding(config);
            }
            catch (Exception ex)
            {
                throw new TunnelException(
                    L10n.T("端口转发失败: ") + ex.Message,
                    hopIndex: -1,
                    host: config.Host,
                    port: config.Tunnel?.RemotePort ?? config.Port,
                    innerException: ex);
            }
        }

        private void EstablishWithJumpChain(TunnelSession session, ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            var hops = config.JumpChain.Hops;
            SshClient lastClient = null;

            for (int i = 0; i < hops.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var hop = hops[i];
                // P1-10：优先 hop 映射的完整凭据（含私钥），否则密码
                try
                {
                    session.ConnectHop(hop, credential, lastClient, i == 0 ? config.Proxy : null);
                }
                catch (Exception ex)
                {
                    throw new TunnelException(
                        L10n.Tf("跳板 Hop[{0}] {1}:{2} 连接失败: {3}", i + 1, hop?.Host, hop?.Port ?? 0, ex.Message),
                        hopIndex: i,
                        host: hop?.Host,
                        port: hop?.Port ?? 0,
                        innerException: ex);
                }

                lastClient = session.GetLastClient();
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                session.StartPortForwarding(config);
            }
            catch (Exception ex)
            {
                var lastHop = hops[hops.Count - 1];
                throw new TunnelException(
                    L10n.T("端口转发失败: ") + ex.Message,
                    hopIndex: hops.Count - 1,
                    host: lastHop.Host,
                    port: lastHop.Port,
                    innerException: ex);
            }
        }
    }
}
