namespace Gdterm.AI.Models
{
    /// <summary>
    /// 对话消息
    /// </summary>
    public class ChatMessage
    {
        /// <summary>
        /// 角色：system/user/assistant
        /// </summary>
        public string Role { get; set; }

        /// <summary>
        /// 消息内容
        /// </summary>
        public string Content { get; set; }

        public ChatMessage() { }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }
}
