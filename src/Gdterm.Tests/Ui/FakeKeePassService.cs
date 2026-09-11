using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// UI 冒烟测试用假密码库：2 条固定条目，不碰真实 kdbx。
    /// 只实现冒烟用到的 ListEntries/AnalyzeHealth，其余抛 NotSupported。
    /// </summary>
    internal sealed class FakeKeePassService : IKeePassService
    {
        public bool IsUnlocked => true;

        public IList<KeePassEntrySummary> ListEntries()
        {
            return new List<KeePassEntrySummary>
            {
                new KeePassEntrySummary { Id = "smoke-1", Title = "冒烟条目一", Username = "alice", GroupPath = "/服务器", Url = "ssh://host1", LastModified = DateTime.Now },
                new KeePassEntrySummary { Id = "smoke-2", Title = "冒烟条目二", Username = "bob", GroupPath = "/数据库", Url = "", LastModified = DateTime.Now },
            };
        }

        public PasswordHealthReport AnalyzeHealth()
        {
            return new PasswordHealthReport
            {
                TotalEntries = 2,
                HealthScore = 75,
                Summary = "冒烟数据",
                WeakPasswords = new List<PasswordIssue>
                {
                    new PasswordIssue { Title = "冒烟条目二", Username = "bob", GroupPath = "/数据库", Issue = "密码过短", StrengthScore = 30 },
                },
                DuplicatePasswords = new List<DuplicatePasswordGroup>(),
                EmptyPasswords = new List<PasswordIssue>(),
                ExpiredPasswords = new List<PasswordIssue>(),
            };
        }

        public void Dispose() { }
        public Task<bool> UnlockAsync(string masterPassword) => Task.FromResult(true);
        public Task<bool> EnsureDatabaseAsync(string masterPassword) => Task.FromResult(true);
        public Task<bool> ChangeMasterPasswordAsync(string o, string n) => Task.FromResult(true);
        public void Lock() { }
        public CredentialPayload GetCredential(string id) => throw new NotSupportedException();
        public KeePassEntry CreateEntry(KeePassEntry entry) => throw new NotSupportedException();
        public void UpdateEntry(KeePassEntry entry) => throw new NotSupportedException();
        public IList<string> ValidatePasswordStrength(string password) => new List<string>();
        public void DeleteEntry(string entryId) => throw new NotSupportedException();
        public KeePassEntry GetEntry(string entryId) => throw new NotSupportedException();
        public KeePassEntry FindEntryByConnection(ConnectionConfig config) => throw new NotSupportedException();
        public byte[] GetSshPrivateKey(string entryId) => throw new NotSupportedException();
        public string GetSshPrivateKeyPassphrase(string entryId) => throw new NotSupportedException();
        public void PerformAutoType(string entryId, string customSequence = null) => throw new NotSupportedException();
        public bool InjectRdpCredential(string host, string username, string password) => throw new NotSupportedException();
        public void CleanupRdpCredential(string host) => throw new NotSupportedException();
        public void CleanupAllRdpCredentials() => throw new NotSupportedException();
    }
}
