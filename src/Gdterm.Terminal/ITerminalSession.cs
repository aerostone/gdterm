using System;
using System.Collections.Generic;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话接口——管理一个 SSH 连接的交互式 shell 生命周期
    /// </summary>
    /// <remarks>
    /// 直连模式：直接连接 config.Host:config.Port
    /// 跳板模式：通过 ITunnelManager 建立端口转发后连接 localhost:forwarded_port
    /// </remarks>
    public interface ITerminalSession : IDisposable
    {
        /// <summary>
        /// 所属连接的 Id
        /// </summary>
        string ConnectionId { get; }

        /// <summary>
        /// 目标主机名
        /// </summary>
        string Hostname { get; }

        /// <summary>
        /// 操作系统类型（从 SSH 检测或用户标记）
        /// </summary>
        string OsType { get; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 连接到远程主机
        /// </summary>
        /// <param name="config">连接配置</param>
        /// <param name="credential">凭据</param>
        /// <param name="rows">终端行数</param>
        /// <param name="columns">终端列数</param>
        void Connect(ConnectionConfig config, CredentialPayload credential, int rows = 24, int columns = 80);

        /// <summary>
        /// 通过隧道连接远程主机
        /// </summary>
        /// <param name="config">连接配置</param>
        /// <param name="credential">凭据</param>
        /// <param name="tunnelEndpoint">隧道接入点</param>
        /// <param name="rows">终端行数</param>
        /// <param name="columns">终端列数</param>
        void ConnectViaTunnel(ConnectionConfig config, CredentialPayload credential, TunnelEndpoint tunnelEndpoint, int rows = 24, int columns = 80);

        /// <summary>
        /// 获取最近 N 行终端输出（用于 AI 上下文）
        /// </summary>
        IList<string> GetRecentOutput(int lineCount);

        /// <summary>
        /// 获取当前选中文本
        /// </summary>
        string GetSelection();

        /// <summary>
        /// 向终端发送命令（AI 建议执行时调用）
        /// </summary>
        void SendInput(string text);

        /// <summary>
        /// 终端输出事件（AI 实时订阅，UI 订阅用于渲染）
        /// </summary>
        event EventHandler<TerminalOutputEventArgs> OutputReceived;

        /// <summary>
        /// 若本会话持有已连接的底层 SSH 客户端则返回之（object 避免 UI 直接依赖 SSH.NET）。
        /// 串口/本地会话返回 null。调用方不得 Disconnect/Dispose。
        /// </summary>
        object TryGetSshClient();
    }
}
