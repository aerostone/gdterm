using System;

namespace Gdterm.AI.Models
{
    /// <summary>
    /// AI 模型配置信息。
    /// </summary>
    public class AiModelConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Endpoint { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public int? MaxTokens { get; set; }
        public double? Temperature { get; set; }
        public bool IsDefault { get; set; }
        public DateTime LastUsedAt { get; set; }
        public long TotalTokensUsed { get; set; }
    }
}
