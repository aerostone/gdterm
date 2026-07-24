using System.Collections.Generic;
using Gdterm.Logging.Models;

namespace Gdterm.Logging
{
    /// <summary>
    /// 审计日志接口——提供结构化审计日志记录和查询能力
    /// </summary>
    public interface IAuditLogger
    {
        /// <summary>
        /// 记录连接事件
        /// </summary>
        void LogConnection(string connectionId, string host, string protocol, ConnectionAction action);

        /// <summary>
        /// 记录凭据使用事件
        /// </summary>
        void LogCredentialUse(string connectionId, string credentialRefId, CredentialAction action);

        /// <summary>
        /// 记录命令执行事件
        /// </summary>
        void LogCommand(string connectionId, string command);

        /// <summary>
        /// 记录 AI 交互事件
        /// </summary>
        void LogAiInteraction(string connectionId, string prompt, string response);

        /// <summary>
        /// 记录安全事件
        /// </summary>
        void LogSecurityEvent(SecurityEvent evt, string detail);

        /// <summary>
        /// 查询审计日志
        /// </summary>
        IList<AuditEntry> Query(AuditQuery query, int limit = 100);
    }
}
