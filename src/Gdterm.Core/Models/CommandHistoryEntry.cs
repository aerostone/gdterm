using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 命令历史记录——记录每个终端会话中执行的命令
    /// </summary>
    public class CommandHistoryEntry
    {
        /// <summary>
        /// 记录 ID
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 连接 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 主机名
        /// </summary>
        public string Hostname { get; set; }

        /// <summary>
        /// 协议类型
        /// </summary>
        public string Protocol { get; set; }

        /// <summary>
        /// 执行的命令
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// 命令输出（前 N 行）
        /// </summary>
        public string Output { get; set; }

        /// <summary>
        /// 命令退出码（如果可获取）
        /// </summary>
        public int? ExitCode { get; set; }

        /// <summary>
        /// 执行时间
        /// </summary>
        public DateTime ExecutedAt { get; set; }

        /// <summary>
        /// 执行耗时（毫秒）
        /// </summary>
        public long DurationMs { get; set; }

        /// <summary>
        /// 是否多通道广播
        /// </summary>
        public bool IsBroadcast { get; set; }

        /// <summary>
        /// 广播目标会话列表（仅 IsBroadcast=true 时有值）
        /// </summary>
        public List<string> BroadcastTargets { get; set; }

        /// <summary>
        /// 标签（用户可自定义，用于分类）
        /// </summary>
        public string Tags { get; set; }
    }

    /// <summary>
    /// 命令历史查询条件
    /// </summary>
    public class CommandHistoryQuery
    {
        /// <summary>
        /// 按连接 ID 过滤
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// 按主机名过滤
        /// </summary>
        public string Hostname { get; set; }

        /// <summary>
        /// 按命令内容过滤（模糊匹配）
        /// </summary>
        public string CommandContains { get; set; }

        /// <summary>
        /// 起始时间
        /// </summary>
        public DateTime? From { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime? To { get; set; }

        /// <summary>
        /// 是否只查询广播命令
        /// </summary>
        public bool? IsBroadcast { get; set; }

        /// <summary>
        /// 按标签过滤
        /// </summary>
        public string Tag { get; set; }

        /// <summary>
        /// 最大返回条数
        /// </summary>
        public int Limit { get; set; } = 100;

        /// <summary>
        /// 排序方式（true=最新在前）
        /// </summary>
        public bool NewestFirst { get; set; } = true;
    }
}
