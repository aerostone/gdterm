using System;

namespace Gdterm.Logging.Models
{
    /// <summary>
    /// 审计条目
    /// </summary>
    public class AuditEntry
    {
        /// <summary>
        /// 事件时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 关联的连接 Id
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 事件类型（Connection/Credential/Command/AiInteraction/Security）
        /// </summary>
        public string EventType { get; set; }

        /// <summary>
        /// 事件详情（JSON 格式，包含具体操作信息）
        /// </summary>
        public string Detail { get; set; }
    }
}
