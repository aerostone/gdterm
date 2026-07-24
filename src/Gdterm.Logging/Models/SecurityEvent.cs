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
        BruteForceDetected
    }
}
