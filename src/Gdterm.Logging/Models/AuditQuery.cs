using System;

namespace Gdterm.Logging.Models
{
    /// <summary>
    /// 审计查询条件
    /// </summary>
    public class AuditQuery
    {
        /// <summary>
        /// 起始时间
        /// </summary>
        public DateTime? From { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime? To { get; set; }

        /// <summary>
        /// 连接 Id 过滤
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 事件类型过滤
        /// </summary>
        public string EventType { get; set; }
    }
}
