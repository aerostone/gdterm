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
        /// <summary>危险命令被拦截（用户取消）</summary>
        DangerousCommandBlocked,
        /// <summary>危险命令被用户确认执行（谁、从哪个入口、命中哪条规则）</summary>
        DangerousCommandConfirmed,
        /// <summary>用户为某条危险规则开了豁免（会话级或永久）</summary>
        DangerousCommandWhitelisted,
        /// <summary>应用级诊断错误（非崩溃）</summary>
        ApplicationError
    }
}
