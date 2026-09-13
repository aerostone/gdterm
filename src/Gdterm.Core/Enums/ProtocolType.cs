namespace Gdterm.Core.Enums
{
    /// <summary>
    /// 连接协议类型
    /// </summary>
    public enum ProtocolType
    {
        /// <summary>
        /// RDP 远程桌面
        /// </summary>
        RDP = 0,

        /// <summary>
        /// SSH 安全外壳
        /// </summary>
        SSH = 1,

        /// <summary>
        /// 串口连接
        /// </summary>
        Serial = 2,

        /// <summary>
        /// Telnet 明文终端（老交换机/工控 console；无加密，敏感环境请用 SSH）
        /// </summary>
        Telnet = 3
    }
}
