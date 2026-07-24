namespace Gdterm.Logging.Models
{
    /// <summary>
    /// 审计日志配置——控制哪些事件类型被记录、是否脱敏、是否加密
    /// </summary>
    public class AuditLogConfig
    {
        // ===== 事件开关 =====

        /// <summary>
        /// 记录连接事件（连接/断开/失败）
        /// </summary>
        public bool LogConnections { get; set; } = true;

        /// <summary>
        /// 记录凭据使用事件（密码库读取）
        /// </summary>
        public bool LogCredentialUsage { get; set; } = false;

        /// <summary>
        /// 记录命令执行事件
        /// </summary>
        public bool LogCommands { get; set; } = true;

        /// <summary>
        /// 记录 AI 交互事件
        /// </summary>
        public bool LogAiInteractions { get; set; } = false;

        /// <summary>
        /// 记录安全事件（锁定/解锁/异常）
        /// </summary>
        public bool LogSecurityEvents { get; set; } = true;

        // ===== 脱敏选项 =====

        /// <summary>
        /// 对命令内容进行脱敏（替换密码/token 等敏感词）
        /// </summary>
        public bool SanitizeCommands { get; set; } = true;

        /// <summary>
        /// 对 AI 交互内容进行脱敏
        /// </summary>
        public bool SanitizeAiContent { get; set; } = true;

        /// <summary>
        /// 脱敏替换文本
        /// </summary>
        public string SanitizeReplacement { get; set; } = "***";

        // ===== 加密选项 =====

        /// <summary>
        /// 加密日志文件（使用主密码派生的密钥）
        /// </summary>
        public bool EncryptLogs { get; set; } = false;

        // ===== 保留策略 =====

        /// <summary>
        /// 最大日志文件数
        /// </summary>
        public int MaxFileCount { get; set; } = 10;

        /// <summary>
        /// 日志保留天数
        /// </summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>
        /// 单个日志文件最大大小（MB）
        /// </summary>
        public int MaxFileSizeMB { get; set; } = 10;
    }
}
