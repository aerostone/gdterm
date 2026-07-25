using System;
using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话工厂——按协议创建 SSH / Serial / Local 会话。
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

        public ITerminalSession CreateSerial()
        {
            return new SerialSession();
        }

        public ITerminalSession CreateLocal(string shellPath = null, string workingDirectory = null)
        {
            return new LocalTerminalSession(shellPath, workingDirectory);
        }

        /// <summary>兼容旧静态调用</summary>
        public static SerialSession CreateSerialStatic()
        {
            return new SerialSession();
        }

        /// <summary>兼容旧静态调用</summary>
        public static LocalTerminalSession CreateLocalStatic(string shellPath = null, string workingDirectory = null)
        {
            return new LocalTerminalSession(shellPath, workingDirectory);
        }
    }
}
