using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 终端会话工厂接口
    /// </summary>
    public interface ITerminalSessionFactory
    {
        /// <summary>
        /// 创建终端会话
        /// </summary>
        ITerminalSession Create(TerminalEndpoint endpoint);
    }
}
