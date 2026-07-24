namespace Gdterm.Core.Models
{
    /// <summary>
    /// 隧道建立后的本地接入点
    /// </summary>
    public class TunnelEndpoint
    {
        /// <summary>
        /// 本地监听地址，通常为 "127.0.0.1"
        /// </summary>
        public string LocalHost { get; set; }

        /// <summary>
        /// 转发后的本地端口
        /// </summary>
        public int LocalPort { get; set; }

        /// <summary>
        /// 所属连接的 Id
        /// </summary>
        public string ConnectionId { get; set; }
    }
}
