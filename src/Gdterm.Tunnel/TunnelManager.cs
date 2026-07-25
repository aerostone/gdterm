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
    /// 隧道管理器——管理 SSH 隧道的建立、查询和关闭
    /// </summary>
    public class TunnelManager : ITunnelManager
    {
        private readonly ConcurrentDictionary<string, TunnelSession> _sessions
            = new ConcurrentDictionary<string, TunnelSession>();

        /// <summary>
        /// 建立隧道
        /// </summary>
        /// <param name="config">连接配置（含跳板链和隧道配置）</param>
        /// <param name="credential">凭据（用户名和密码）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>隧道接入点（LocalHost:LocalPort）</returns>
        public async Task<TunnelEndpoint> EstablishAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var connectionId = config.Id;

            // 同一 connectionId 复用活跃隧道（SSH 终端 + SFTP 共享跳板，避免互相拆掉）
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

            // 旧隧道已失效：清理后重建
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
                    {
                        // 直连模式：无跳板链
                        EstablishDirect(session, config, credential, ct);
                    }
                    else
                    {
                        // 跳板模式：逐 hop 连接
                        EstablishWithJumpChain(session, config, credential, ct);
                    }
                }, ct);

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

        /// <summary>
        /// 关闭指定 connectionId 的隧道
        /// </summary>
        public Task CloseAsync(string connectionId)
        {
            if (_sessions.TryRemove(connectionId, out var session))
            {
                session.Dispose();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 查询隧道状态
        /// </summary>
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

        /// <summary>
        /// 关闭所有隧道
        /// </summary>
        public void Dispose()
        {
            foreach (var kvp in _sessions)
            {
                try { kvp.Value.Dispose(); } catch { /* best-effort */ }
            }
            _sessions.Clear();
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
                var hopPassword = hop.CredentialRefId != null ? credential?.Password : credential?.Password;

                try
                {
                    session.ConnectHop(hop, hopPassword, lastClient);
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

                // 获取当前 hop 的 SshClient（通过内部列表最后添加的）
                // 这里通过 session 内部状态获取
                lastClient = session.GetLastClient();
            }

            ct.ThrowIfCancellationRequested();

            // 最后一跳建立端口转发
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
