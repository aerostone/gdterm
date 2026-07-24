using System;

namespace Gdterm.Security.Models
{
    /// <summary>
    /// 锁定状态变化事件参数
    /// </summary>
    public class LockStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 是否已锁定
        /// </summary>
        public bool IsLocked { get; set; }

        /// <summary>
        /// 锁定原因："idle"（闲时超时）、"manual"（手动锁定）、"unlock"（解锁）
        /// </summary>
        public string Reason { get; set; }

        public LockStateChangedEventArgs() { }

        public LockStateChangedEventArgs(bool isLocked, string reason)
        {
            IsLocked = isLocked;
            Reason = reason;
        }
    }
}
