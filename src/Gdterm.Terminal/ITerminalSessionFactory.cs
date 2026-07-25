using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话工厂接口——覆盖 SSH / 串口 / 本地 Shell。
    /// UI 不直接 new TerminalSession / SerialSession / LocalTerminalSession。
    /// </summary>
    public interface ITerminalSessionFactory
    {
        /// <summary>创建 SSH 终端会话</summary>
        ITerminalSession Create(TerminalEndpoint endpoint);

        /// <summary>创建串口会话</summary>
        ITerminalSession CreateSerial();

        /// <summary>创建本地终端会话</summary>
        ITerminalSession CreateLocal(string shellPath = null, string workingDirectory = null);
    }
}
