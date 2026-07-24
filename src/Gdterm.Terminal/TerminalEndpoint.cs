namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端连接端点
    /// </summary>
    public class TerminalEndpoint
    {
        /// <summary>
        /// 主机地址
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// 端口
        /// </summary>
        public int Port { get; set; } = 22;
    }
}
