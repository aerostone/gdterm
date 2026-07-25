using System;
using Gdterm.Tools.Models;

namespace Gdterm.Tools
{
    /// <summary>
    /// 远程 SSH 会话抽象——运维工具只依赖此接口，不直接碰 Renci.SshNet.SshClient。
    /// </summary>
    public interface ISshRemoteSession
    {
        /// <summary>底层客户端是否仍连接</summary>
        bool IsConnected { get; }

        /// <summary>在远端执行一条命令</summary>
        RemoteCommandResult RunCommand(string command);

        /// <summary>创建基于当前会话的临时文件传输通道（调用方负责 Dispose）</summary>
        IRemoteFileTransfer CreateFileTransfer();
    }
}
