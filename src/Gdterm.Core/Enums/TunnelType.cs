namespace Gdterm.Core.Enums
{
    /// <summary>
    /// SSH 隧道类型
    /// </summary>
    public enum TunnelType
    {
        /// <summary>
        /// 本地端口转发：将本地端口转发到远程主机的指定端口
        /// </summary>
        LocalForward = 0,

        /// <summary>
        /// 动态 SOCKS 代理：本地 SOCKS5 代理服务器
        /// </summary>
        DynamicSocks = 1
    }
}
