namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 客户端工厂——UI 不直接 new RdpClient。
    /// </summary>
    public interface IRdpClientFactory
    {
        /// <summary>创建新的 RDP 客户端实例（含 ActiveX 回退路径）</summary>
        IRdpClient Create();

        /// <summary>
        /// 按连接配置选择引擎创建客户端：
        /// 元数据 rdp_engine=mstscax 强制 ActiveX；否则优先 FreeRDP（wfreerdp.exe 存在时），缺失则回退 mstscax。
        /// </summary>
        IRdpClient CreateFor(Gdterm.Core.Models.ConnectionConfig config);
    }
}
