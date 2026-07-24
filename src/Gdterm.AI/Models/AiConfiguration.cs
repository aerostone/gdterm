namespace Gdterm.AI.Models
{
    /// <summary>
    /// AI 服务配置
    /// </summary>
    public class AiConfiguration
    {
        /// <summary>
        /// OpenAI-compatible API endpoint（如 https://api.openai.com/v1 或 http://localhost:11434/v1）
        /// </summary>
        public string ApiEndpoint { get; set; }

        /// <summary>
        /// API key（可选，本地模型可能不需要）
        /// </summary>
        public string ApiKey { get; set; }

        /// <summary>
        /// 模型名称（如 gpt-4、llama3、qwen2）
        /// </summary>
        public string Model { get; set; } = "gpt-4";

        /// <summary>
        /// 最大 token 数
        /// </summary>
        public int MaxTokens { get; set; } = 2048;

        /// <summary>
        /// 温度参数（0.0-2.0）
        /// </summary>
        public double Temperature { get; set; } = 0.7;
    }
}
