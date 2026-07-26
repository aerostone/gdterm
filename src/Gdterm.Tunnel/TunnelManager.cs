using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Tunnel.Exceptions;
using Gdterm.Tunnel.Models;
using Renci.SshNet;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// 隧道管理器——管理 SSH 隧道的建立、查询和关闭。
    /// go-live P1-04：同一 connectionId 并发 Establish 原子化（单飞）。
    /// </summary>
    public class TunnelManager : ITunnelManager
    {
        private readonly ConcurrentDictionary<string, TunnelSession> _sessions
            = new ConcurrentDictionary<string, TunnelSession>();

        /// <summary>connectionId → 进行中的建立任务（单飞）。</summary>
        private readonly ConcurrentDictionary<string, Task<TunnelEndpoint>> _inflight
            = new ConcurrentDictionary<string, Task<TunnelEndpoint>>();

        public Task<TunnelEndpoint> EstablishAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var connectionId = config.Id ?? Guid.NewGuid().ToString("N");

            // 复用活跃隧道
            if (!string.IsNullOrEmpty(connectionId)
                && _sessions.TryGetValue(connectionId, out var existing)
                && existing != null
                && existing.IsActive)
            {
                return Task.FromResult(new TunnelEndpoint
                {
                    LocalHost = existing.LocalHost,
                    LocalPort = existing.LocalPort,
                    ConnectionId = connectionId
                });
            }

            // P1-04：并发同 connectionId 只建一次
            Task<TunnelEndpoint> created = null;
            var task = _inflight.GetOrAdd(connectionId, id =>
            {
                created = EstablishCoreAsync(config, credential, ct);
                // 无论成败都从 inflight 移除，允许后续重试
                created.ContinueWith(_ =>
                {
                    Task<TunnelEndpoint> removed;
                    _inflight.TryRemove(id, out removed);
                }, TaskContinuationOptions.ExecuteSynchronously);
                return created;
            });

            return task;
        }

        private async Task<TunnelEndpoint> EstablishCoreAsync(
            ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            var connectionId = config.Id;

            // 双检：可能在排队期间已有活跃隧道
            if (!string.IsNullOrEmpty(connectionId)
                && _sessions.TryGetValue(connectionId, out var existing)
                && existing != null
                && existing.IsActive)
            {
                return new TunnelEndpoint
                {
                    LocalHost = existing.LocalHost,
                    LocalPort = existing.LocalPort,
                    ConnectionId = connectionId
                };
            }

            if (!string.IsNullOrEmpty(connectionId)
                && _sessions.TryRemove(connectionId, out var oldSession))
            {
                try { oldSession.Dispose(); } catch { /* best-effort */ }
            }

            var session = new TunnelSession(connectionId);

            try
            {
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();

                    if (config.JumpChain?.Hops == null || config.JumpChain.Hops.Count == 0)
                        EstablishDirect(session, config, credential, ct);
                    else
                        EstablishWithJumpChain(session, config, credential, ct);
                }, ct).ConfigureAwait(false);

                _sessions[connectionId] = session;

                return new TunnelEndpoint
                {
                    LocalHost = session.LocalHost,
                    LocalPort = session.LocalPort,
                    ConnectionId = connectionId
                };
            }
            catch (OperationCanceledException)
            {
                session.Dispose();
                throw;
            }
            catch (TunnelException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new TunnelException(
                    $"隧道建立失败: {ex.Message}",
                    hopIndex: -1,
                    host: config.Host,
                    port: config.Port,
                    innerException: ex);
            }
        }

        public Task CloseAsync(string connectionId)
        {
            if (_sessions.TryRemove(connectionId, out var session))
            {
                session.Dispose();
            }
            return Task.CompletedTask;
        }

        public TunnelStatus GetStatus(string connectionId)
        {
            if (_sessions.TryGetValue(connectionId, out var session))
            {
                return new TunnelStatus
                {
                    IsActive = session.IsActive,
                    LastError = session.LastError,
                    Uptime = session.IsActive ? DateTime.UtcNow - session.EstablishedAt : TimeSpan.Zero
                };
            }

            return new TunnelStatus
            {
                IsActive = false,
                LastError = "隧道不存在",
                Uptime = TimeSpan.Zero
            };
        }

        public void Dispose()
        {
            foreach (var kvp in _sessions)
            {
                try { kvp.Value.Dispose(); } catch { /* best-effort */ }
            }
            _sessions.Clear();
            _inflight.Clear();
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
                    $"直连 {config.Host}:{config.Port} 失败: {ex.Message}",
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
                    $"端口转发失败: {ex.Message}",
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
                    session.ConnectHop(hop, credential, lastClient);
                }
                catch (Exception ex)
                {
                    throw new TunnelException(
                        $"跳板 Hop[{i}] {hop.Host}:{hop.Port} 连接失败: {ex.Message}",
                        hopIndex: i,
                        host: hop.Host,
                        port: hop.Port,
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
                    $"端口转发失败: {ex.Message}",
                    hopIndex: hops.Count - 1,
                    host: lastHop.Host,
                    port: lastHop.Port,
                    innerException: ex);
            }
        }
    }
}
