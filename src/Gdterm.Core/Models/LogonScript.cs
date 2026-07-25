using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 登录脚本步骤类型
    /// </summary>
    public enum LogonStepType
    {
        /// <summary>发送文本（自动追加回车）</summary>
        Send,
        /// <summary>等待关键词出现（超时后跳过）</summary>
        Wait,
        /// <summary>延时（毫秒）</summary>
        Delay
    }

    /// <summary>
    /// 登录脚本步骤
    /// </summary>
    public class LogonStep
    {
        /// <summary>步骤类型</summary>
        public LogonStepType Type { get; set; }

        /// <summary>发送文本或等待的关键词</summary>
        public string Value { get; set; }

        /// <summary>超时（毫秒，仅 Wait 类型有效）</summary>
        public int TimeoutMs { get; set; } = 10000;

        /// <summary>说明</summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// 登录脚本配置
    /// </summary>
    public class LogonScript
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<LogonStep> Steps { get; set; } = new List<LogonStep>();
        public bool Enabled { get; set; } = true;
        public string AssociatedConnectionId { get; set; }
    }
}
