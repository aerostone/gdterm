using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 会话书签——快速连接入口
    /// </summary>
    public class SessionBookmark
    {
        /// <summary>
        /// 书签 ID
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 书签名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 关联的连接配置 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 分组标签（可选）
        /// </summary>
        public string Tags { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 最后连接时间
        /// </summary>
        public DateTime? LastConnectedAt { get; set; }

        /// <summary>
        /// 连接次数
        /// </summary>
        public int ConnectCount { get; set; }

        /// <summary>
        /// 是否收藏（置顶）
        /// </summary>
        public bool IsFavorite { get; set; }
    }

    /// <summary>
    /// 最近连接记录
    /// </summary>
    public class RecentConnection
    {
        /// <summary>
        /// 连接配置 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 连接主机
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// 连接协议
        /// </summary>
        public string Protocol { get; set; }

        /// <summary>
        /// 连接时间
        /// </summary>
        public DateTime ConnectedAt { get; set; }

        /// <summary>
        /// 连接是否成功
        /// </summary>
        public bool Success { get; set; }
    }
}
