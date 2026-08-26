namespace Gdterm.Rdp
{
    /// <summary>
    /// 默认 RDP 客户端工厂：优先 FreeRDP 进程嵌入（无 MSLicensing 提权问题），
    /// wfreerdp.exe 缺失或元数据强制时回退 mstscax ActiveX。
    /// </summary>
    public sealed class RdpClientFactory : IRdpClientFactory
    {
        public IRdpClient Create()
        {
            return new RdpClient();
        }

        public IRdpClient CreateFor(Gdterm.Core.Models.ConnectionConfig config)
        {
            string engine = null;
            if (config != null && config.Metadata != null)
                config.Metadata.TryGetValue("rdp_engine", out engine);
            engine = (engine ?? "").Trim().ToLowerInvariant();

            if (engine == "mstscax")
            {
                RdpLog.Info("RdpClientFactory", "engine=mstscax（元数据强制）");
                return new RdpClient();
            }

            var exe = FreeRdpClient.FindExecutable();
            if (exe != null)
            {
                RdpLog.Info("RdpClientFactory", "engine=freerdp exe=" + exe);
                return new FreeRdpClient();
            }

            if (engine == "freerdp")
            {
                // 显式要求 FreeRDP 但二进制缺失——抛出让用户看到明确提示，而不是静默换引擎
                throw new System.InvalidOperationException(
                    "rdp_engine=freerdp 但未找到 wfreerdp.exe（期望位置：<程序目录>\\freerdp\\wfreerdp.exe 或 vendor\\freerdp\\wfreerdp.exe）");
            }

            RdpLog.Info("RdpClientFactory", "wfreerdp 未找到，回退 mstscax ActiveX（可将 FreeRDP 2.x 解压到 freerdp\\ 启用）");
            return new RdpClient();
        }
    }
}
