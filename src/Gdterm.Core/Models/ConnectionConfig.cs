using System.Collections.Generic;
using Gdterm.Core.Enums;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 一个远程连接的完整配置
    /// </summary>
    public class ConnectionConfig
    {
        /// <summary>
        /// 连接唯一标识（GUID），存储层全局唯一
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 显示名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 连接协议（RDP 或 SSH）
        /// </summary>
        public ProtocolType Protocol { get; set; }

        /// <summary>
        /// 目标主机（IP 或域名）
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// 目标端口（RDP 默认 3389，SSH 默认 22）
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// RDP 域名（SSH 不用）
        /// </summary>
        public string Domain { get; set; }

        /// <summary>
        /// 树形分组路径，如 "Jump/Web"
        /// </summary>
        public string GroupPath { get; set; }

        /// <summary>
        /// 跳板链配置。null 表示直连
        /// </summary>
        public JumpChainConfig JumpChain { get; set; }

        /// <summary>
        /// 隧道配置。null 表示不使用隧道
        /// </summary>
        public TunnelConfig Tunnel { get; set; }

        /// <summary>
        /// 关联的 KeePass 条目 UUID。null 表示不关联
        /// </summary>
        public string CredentialRefId { get; set; }

        /// <summary>
        /// 扩展字段
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; }
    }
}
