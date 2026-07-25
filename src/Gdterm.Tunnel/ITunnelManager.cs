using System;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Tunnel.Models;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// SSH 隧道管理抽象——UI/会话层只依赖此接口，不直接依赖 SSH.NET。
    /// </summary>
    public interface ITunnelManager : IDisposable
    {
        /// <summary>建立或复用 connectionId 对应的本地转发端点</summary>
        Task<TunnelEndpoint> EstablishAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct);

        /// <summary>关闭指定连接的隧道（无其它标签共享时应调用）</summary>
        Task CloseAsync(string connectionId);

        /// <summary>查询隧道状态</summary>
        TunnelStatus GetStatus(string connectionId);
    }
}
