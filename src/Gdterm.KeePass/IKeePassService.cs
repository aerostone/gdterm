using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.KeePass.Models;

namespace Gdterm.KeePass
{
    /// <summary>
    /// KeePass 密码库服务接口——提供密码库解锁、条目管理、凭据获取、智能匹配能力
    /// </summary>
    public interface IKeePassService : IDisposable
    {
        /// <summary>
        /// 解锁密码库
        /// </summary>
        Task<bool> UnlockAsync(string masterPassword);

        /// <summary>
        /// 确保密码库存在并解锁；不存在时用主密码自动创建。
        /// 首次使用向导与启动后首次连接都用它初始化 kdbx。
        /// </summary>
        Task<bool> EnsureDatabaseAsync(string masterPassword);

        /// <summary>
        /// 修改密码库主密码：用 oldPw 解锁已存在 kdbx，再用 newPw 重新加密并保存。
        /// 同步调用方负责更新 SecurityManager 的主密码哈希与 master-password.ini。
        /// </summary>
        /// <returns>true 成功（旧密码错误或 kdbx 损坏返回 false）</returns>
        Task<bool> ChangeMasterPasswordAsync(string oldMasterPassword, string newMasterPassword);

        /// <summary>
        /// 锁定密码库，清除内存中的明文
        /// </summary>
        void Lock();

        /// <summary>
        /// 是否已解锁
        /// </summary>
        bool IsUnlocked { get; }

        /// <summary>
        /// 根据 CredentialRefId 获取凭据
        /// </summary>
        CredentialPayload GetCredential(string credentialRefId);

        /// <summary>
        /// 创建密码条目
        /// </summary>
        KeePassEntry CreateEntry(KeePassEntry entry);

        /// <summary>
        /// 更新密码条目
        /// </summary>
        void UpdateEntry(KeePassEntry entry);

        /// <summary>
        /// 删除密码条目
        /// </summary>
        void DeleteEntry(string entryId);

        /// <summary>
        /// 列出所有条目（不含密码明文）
        /// </summary>
        IList<KeePassEntrySummary> ListEntries();

        /// <summary>
        /// 按 Id 读取完整条目（含密码与密钥元数据，仅在已解锁时可用）
        /// </summary>
        KeePassEntry GetEntry(string entryId);

        // ===== 智能匹配 =====

        /// <summary>
        /// 根据连接配置智能匹配 KeePass 条目
        /// 匹配规则：URL（host:port）> 标题（包含 host）> 备注（包含 host）
        /// </summary>
        /// <param name="config">连接配置</param>
        /// <returns>匹配的条目（含凭据），未匹配返回 null</returns>
        KeePassEntry FindEntryByConnection(ConnectionConfig config);

        // ===== SSH 密钥 =====

        /// <summary>
        /// 获取 SSH 私钥数据（PEM 格式）
        /// </summary>
        /// <param name="entryId">条目 ID</param>
        /// <returns>私钥字节数组，无私钥返回 null</returns>
        byte[] GetSshPrivateKey(string entryId);

        /// <summary>
        /// 获取 SSH 私钥密码短语
        /// </summary>
        string GetSshPrivateKeyPassphrase(string entryId);

        // ===== Auto-Type =====

        /// <summary>
        /// 执行 Auto-Type（向当前焦点窗口发送按键序列）
        /// </summary>
        /// <param name="entryId">条目 ID</param>
        /// <param name="customSequence">自定义序列（null 使用默认）</param>
        void PerformAutoType(string entryId, string customSequence = null);

        // ===== RDP 凭据注入 =====

        /// <summary>
        /// 将 RDP 凭据注入 Windows 凭据管理器（CredWrite，不经 cmdkey 命令行）。
        /// 目标名 TERMSRV/{host}；连接结束后应调用 Cleanup。
        /// </summary>
        /// <param name="host">目标主机</param>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <returns>是否成功</returns>
        bool InjectRdpCredential(string host, string username, string password);

        /// <summary>
        /// 清理 RDP 凭据（从 Windows 凭据管理器删除）
        /// </summary>
        void CleanupRdpCredential(string host);

        /// <summary>
        /// 清理本进程注入的全部 RDP 凭据（异常退出前的 best-effort 收尾）
        /// </summary>
        void CleanupAllRdpCredentials();

        /// <summary>
        /// 分析密码库健康状况
        /// 检测：弱密码、重复密码、空密码、过期密码
        /// </summary>
        PasswordHealthReport AnalyzeHealth();
    }
}
