using System;
using Gdterm.Security.Models;

namespace Gdterm.Security
{
    /// <summary>
    /// 安全管理器接口——闲时锁定、手动锁定/解锁、主密码管理
    /// </summary>
    public interface ISecurityManager : IDisposable
    {
        /// <summary>
        /// 重置空闲计时器（UI 每次用户操作时调用）
        /// </summary>
        void ResetIdleTimer();

        /// <summary>
        /// 手动锁定
        /// </summary>
        void LockNow();

        /// <summary>
        /// 解锁（验证主密码）
        /// </summary>
        /// <param name="masterPassword">主密码</param>
        /// <returns>解锁是否成功</returns>
        bool Unlock(string masterPassword);

        /// <summary>
        /// 设置/修改主密码
        /// </summary>
        /// <param name="oldPassword">旧密码（首次设置时传 null）</param>
        /// <param name="newPassword">新密码</param>
        /// <exception cref="WeakPasswordException">密码不满足强度要求</exception>
        void SetMasterPassword(string oldPassword, string newPassword);

        /// <summary>
        /// 锁定状态变化事件（UI 订阅以更新界面）
        /// </summary>
        event EventHandler<LockStateChangedEventArgs> LockStateChanged;

        /// <summary>
        /// 是否已锁定
        /// </summary>
        bool IsLocked { get; }

        /// <summary>
        /// 闲时超时时间（硬上限 30 分钟）
        /// </summary>
        TimeSpan IdleTimeout { get; set; }

        /// <summary>
        /// 获取主密码明文（仅用于传递给 KeePass 解锁，不持久化）
        /// 已锁定时返回 null
        /// </summary>
        string GetMasterPassword();

        /// <summary>
        /// 验证主密码是否正确（不改变锁定状态）
        /// 用于凭据管理等敏感操作的二次验证
        /// </summary>
        bool VerifyMasterPassword(string password);
    }
}
