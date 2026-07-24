using Gdterm.Core.Enums;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 隧道配置
    /// </summary>
    public class TunnelConfig
    {
        /// <summary>
        /// 隧道类型（本地端口转发 或 动态 SOCKS 代理）
        /// </summary>
        public TunnelType Type { get; set; }

        /// <summary>
        /// 本地监听端口。0 表示自动分配可用端口
        /// </summary>
        public int LocalPort { get; set; }

        /// <summary>
        /// 远程目标主机（LocalForward 时使用，DynamicSocks 时忽略）
        /// </summary>
        public string RemoteHost { get; set; }

        /// <summary>
        /// 远程目标端口（LocalForward 时使用，DynamicSocks 时忽略）
        /// </summary>
        public int RemotePort { get; set; }
    }
}
