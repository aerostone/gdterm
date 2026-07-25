using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 端口转发规则
    /// </summary>
    public class PortForwardRule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public PortForwardType Type { get; set; }
        public string LocalHost { get; set; } = "127.0.0.1";
        public int LocalPort { get; set; }
        public string RemoteHost { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; }
        public string ConnectionId { get; set; }
        public bool Enabled { get; set; } = true;
        public string Description { get; set; }
    }

    public enum PortForwardType
    {
        /// <summary>本地转发（L）——本地端口 → 远程主机</summary>
        Local,
        /// <summary>远程转发（R）——远程端口 → 本地主机</summary>
        Remote,
        /// <summary>动态转发（D）——SOCKS5 代理</summary>
        Dynamic
    }
}
