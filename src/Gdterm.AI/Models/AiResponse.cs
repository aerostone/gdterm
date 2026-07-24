using System.Collections.Generic;

namespace Gdterm.AI.Models
{
    /// <summary>
    /// AI 响应结果
    /// </summary>
    public class AiResponse
    {
        /// <summary>
        /// AI 回复内容
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// 从回复中提取的可执行命令
        /// </summary>
        public IList<string> SuggestedCommands { get; set; } = new List<string>();

        /// <summary>
        /// 消耗的 token 数
        /// </summary>
        public int TokensUsed { get; set; }

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 错误信息（IsSuccess=false 时有值）
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
