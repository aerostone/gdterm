namespace Gdterm.Logging.Models
{
    /// <summary>
    /// 日志轮转配置
    /// </summary>
    public class LogRotationConfig
    {
        /// <summary>
        /// 单个日志文件最大大小（字节），默认 10MB
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// 最大保留文件数，默认 10 个
        /// </summary>
        public int MaxFileCount { get; set; } = 10;

        /// <summary>
        /// 日志保留天数，默认 30 天
        /// </summary>
        public int RetentionDays { get; set; } = 30;
    }
}
