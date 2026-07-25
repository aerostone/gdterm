using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.AI.Models;
using Gdterm.Terminal;

namespace Gdterm.AI
{
    /// <summary>
    /// AI 对话服务接口——提供连接上下文感知的 AI 对话能力
    /// </summary>
    public interface IAiAssistantService
    {
        /// <summary>
        /// 发送消息并获取回复（自动注入连接上下文）
        /// </summary>
        /// <param name="message">用户消息</param>
        /// <param name="session">当前终端会话（用于提取上下文）</param>
        /// <param name="ct">取消令牌</param>
        Task<AiResponse> SendMessageAsync(string message, ITerminalSession session, CancellationToken ct);

        /// <summary>
        /// 从 AI 回复中提取可执行命令
        /// </summary>
        IList<string> ExtractCommands(string response);

        /// <summary>
        /// 发送命令到终端（用户确认后调用）
        /// </summary>
        void ExecuteCommand(ITerminalSession session, string command);

        /// <summary>
        /// 清空对话历史
        /// </summary>
        void ClearHistory();

        /// <summary>
        /// AI 服务配置
        /// </summary>
        AiConfiguration Configuration { get; set; }

        /// <summary>
        /// 流式发送消息（逐 token 回调）
        /// </summary>
        Task<AiResponse> SendMessageStreamingAsync(string message, ITerminalSession session, CancellationToken ct, Action<string> onToken);
    }
}
