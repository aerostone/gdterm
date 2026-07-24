using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.KeePass.Models;

namespace Gdterm.KeePass
{
    /// <summary>
    /// KeePass 密码库服务接口——提供密码库解锁、条目管理、凭据获取能力
    /// </summary>
    public interface IKeePassService : IDisposable
    {
        /// <summary>
        /// 解锁密码库
        /// </summary>
        /// <param name="masterPassword">主密码</param>
        /// <returns>解锁是否成功</returns>
        Task<bool> UnlockAsync(string masterPassword);

        /// <summary>
        /// 锁定密码库，清除内存中的明文
        /// </summary>
        void Lock();

        /// <summary>
        /// 是否已解锁
        /// </summary>
        bool IsUnlocked { get; }

        /// <summary>
        /// 根据 ConnectionConfig.CredentialRefId 获取凭据
        /// </summary>
        /// <param name="credentialRefId">KeePass 条目 UUID</param>
        /// <returns>凭据（用户名+密码）</returns>
        /// <exception cref="InvalidOperationException">密码库未解锁时抛出</exception>
        CredentialPayload GetCredential(string credentialRefId);

        /// <summary>
        /// 创建密码条目（自动校验密码强度）
        /// </summary>
        /// <param name="entry">条目信息</param>
        /// <returns>创建后的条目（含分配的 Id）</returns>
        /// <exception cref="WeakPasswordException">密码不满足强度要求时抛出</exception>
        KeePassEntry CreateEntry(KeePassEntry entry);

        /// <summary>
        /// 更新密码条目（自动校验密码强度）
        /// </summary>
        /// <param name="entry">条目信息</param>
        /// <exception cref="WeakPasswordException">密码不满足强度要求时抛出</exception>
        void UpdateEntry(KeePassEntry entry);

        /// <summary>
        /// 列出所有条目（不含密码明文，用于 UI 展示和关联选择）
        /// </summary>
        IList<KeePassEntrySummary> ListEntries();
    }
}
