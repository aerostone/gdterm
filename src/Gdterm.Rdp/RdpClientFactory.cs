namespace Gdterm.Rdp
{
    /// <summary>
    /// 默认 RDP 客户端工厂。
    /// </summary>
    public sealed class RdpClientFactory : IRdpClientFactory
    {
        public IRdpClient Create()
        {
            return new RdpClient();
        }
    }
}
