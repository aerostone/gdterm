using System;

namespace Gdterm.Tunnel.Models
{
    /// <summary>
    /// 隧道状态信息
    /// </summary>
    public class TunnelStatus
    {
        /// <summary>
        /// 隧道是否活跃
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// 最后一次错误信息（成功时为 null）
        /// </summary>
        public string LastError { get; set; }

        /// <summary>
        /// 隧道已持续时间（从建立到查询时刻）
        /// </summary>
        public TimeSpan Uptime { get; set; }
    }
}
