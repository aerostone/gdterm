namespace Gdterm.Logging.Models
{
    /// <summary>
    /// 安全事件类型
    /// </summary>
    public enum SecurityEvent
    {
        IdleLock,
        Unlock,
        WeakPasswordRejected,
        BruteForceDetected,
        /// <summary>UI 线程未处理异常</summary>
        UnhandledUiException,
        /// <summary>非 UI / 终结器 / 后台未处理异常</summary>
        UnhandledDomainException,
        /// <summary>未观察的 Task 异常</summary>
        UnobservedTaskException,
        /// <summary>危险命令被拦截</summary>
        DangerousCommandBlocked,
        /// <summary>应用级诊断错误（非崩溃）</summary>
        ApplicationError
    }
}
