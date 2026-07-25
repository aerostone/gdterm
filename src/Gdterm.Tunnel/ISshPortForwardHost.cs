using System;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// 端口转发宿主抽象——UI 不直接持有 Renci.SshNet.SshClient。
    /// 由 Terminal 层适配器实现。
    /// </summary>
    public interface ISshPortForwardHost
    {
        bool IsConnected { get; }

        /// <summary>
        /// 内部桥接：返回底层 SshClient 对象（仅 PortForwardManager 使用）。
        /// 调用方不得 Disconnect/Dispose。
        /// </summary>
        object GetUnderlyingClient();
    }
}
