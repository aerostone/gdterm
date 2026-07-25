using System;
using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话工厂——按端点创建 SSH TerminalSession
    /// </summary>
    public class TerminalSessionFactory : ITerminalSessionFactory
    {
        public ITerminalSession Create(TerminalEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            // TerminalSession 在 Connect/ConnectViaTunnel 时再绑定真实目标；
            // 工厂仅负责实例化，兼容现有 UI 懒连接流程。
            return new TerminalSession();
        }

        /// <summary>
        /// 创建串口会话
        /// </summary>
        public static SerialSession CreateSerial()
        {
            return new SerialSession();
        }

        /// <summary>
        /// 创建本地终端会话
        /// </summary>
        public static LocalTerminalSession CreateLocal(string shellPath = null, string workingDirectory = null)
        {
            return new LocalTerminalSession(shellPath, workingDirectory);
        }
    }
}
