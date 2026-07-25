namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 客户端工厂——UI 不直接 new RdpClient。
    /// </summary>
    public interface IRdpClientFactory
    {
        /// <summary>创建新的 RDP 客户端实例（含 ActiveX 回退路径）</summary>
        IRdpClient Create();
    }
}
