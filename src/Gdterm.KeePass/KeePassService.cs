using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.KeePass.Models;
using KeePassLib;
using KeePassLib.Keys;
using KeePassLib.Serialization;
using KeePassLib.Security;

namespace Gdterm.KeePass
{
    /// <summary>
    /// KeePass 密码库服务实现——基于 KeePassLib 实现 .kdbx 文件读写
    /// </summary>
    public class KeePassService : IKeePassService
    {
        private readonly string _databasePath;
        private readonly PasswordStrengthValidator _validator;
        private PwDatabase _database;
        private bool _disposed;

        public bool IsUnlocked => _database?.IsOpen == true;

        /// <param name="databasePath">.kdbx 文件完整路径</param>
        public KeePassService(string databasePath)
        {
            if (string.IsNullOrEmpty(databasePath))
                throw new ArgumentNullException(nameof(databasePath));

            _databasePath = databasePath;
            _validator = new PasswordStrengthValidator();
        }

        /// <summary>
        /// 解锁密码库
        /// </summary>
        public Task<bool> UnlockAsync(string masterPassword)
        {
            if (string.IsNullOrEmpty(masterPassword))
                return Task.FromResult(false);

            try
            {
                // 先锁定已打开的库
                Lock();

                _database = new PwDatabase();

                var ioConnInfo = new IOConnectionInfo
                {
                    Path = _databasePath
                };

                var compositeKey = new CompositeKey();
                compositeKey.AddUserKey(new KcpPassword(masterPassword));

                var logger = new KeePassLib.Logging.NullStatusLogger();
                _database.Open(ioConnInfo, compositeKey, logger);

                return Task.FromResult(true);
            }
            catch
            {
                _database = null;
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// 锁定密码库，清除内存中的明文
        /// </summary>
        public void Lock()
        {
            if (_database != null)
            {
                try
                {
                    if (_database.IsOpen)
                        _database.Close();
                }
                catch { /* best-effort */ }

                _database = null;
            }
        }

        /// <summary>
        /// 根据 CredentialRefId 获取凭据
        /// </summary>
        public CredentialPayload GetCredential(string credentialRefId)
        {
            EnsureUnlocked();

            if (string.IsNullOrEmpty(credentialRefId))
                throw new ArgumentNullException(nameof(credentialRefId));

            // 遍历所有条目查找匹配的 UUID
            var entry = FindEntryByUuid(credentialRefId);
            if (entry == null)
                throw new KeyNotFoundException($"未找到 ID 为 {credentialRefId} 的密码条目");

            return new CredentialPayload
            {
                Username = entry.Strings.ReadSafe(PwDefs.UserNameField),
                Password = entry.Strings.ReadSafe(PwDefs.PasswordField)
            };
        }

        /// <summary>
        /// 创建密码条目
        /// </summary>
        public KeePassEntry CreateEntry(KeePassEntry entry)
        {
            EnsureUnlocked();

            if (entry == null) throw new ArgumentNullException(nameof(entry));

            // 校验密码强度
            var violations = _validator.Validate(entry.Password);
            if (violations.Count > 0)
                throw new WeakPasswordException(violations);

            // 获取或创建目标分组
            var group = GetOrCreateGroup(entry.GroupPath ?? "");

            // 创建 KeePass 条目
            var pwEntry = new PwEntry(true, true);
            pwEntry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, entry.Title ?? ""));
            pwEntry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, entry.Username ?? ""));
            pwEntry.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, entry.Password ?? ""));
            pwEntry.Strings.Set(PwDefs.UrlField, new ProtectedString(false, entry.Url ?? ""));
            pwEntry.Strings.Set(PwDefs.NotesField, new ProtectedString(false, entry.Notes ?? ""));

            group.AddEntry(pwEntry, true);

            // 保存数据库
            SaveDatabase();

            // 返回带 Id 的条目
            entry.Id = pwEntry.Uuid.ToHexString();
            return entry;
        }

        /// <summary>
        /// 更新密码条目
        /// </summary>
        public void UpdateEntry(KeePassEntry entry)
        {
            EnsureUnlocked();

            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id))
                throw new ArgumentException("条目 Id 不能为空", nameof(entry));

            // 校验密码强度（如果提供了新密码）
            if (!string.IsNullOrEmpty(entry.Password))
            {
                var violations = _validator.Validate(entry.Password);
                if (violations.Count > 0)
                    throw new WeakPasswordException(violations);
            }

            var pwEntry = FindEntryByUuid(entry.Id);
            if (pwEntry == null)
                throw new KeyNotFoundException($"未找到 ID 为 {entry.Id} 的密码条目");

            // 更新字段
            if (entry.Title != null)
                pwEntry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, entry.Title));
            if (entry.Username != null)
                pwEntry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, entry.Username));
            if (entry.Password != null)
                pwEntry.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, entry.Password));
            if (entry.Url != null)
                pwEntry.Strings.Set(PwDefs.UrlField, new ProtectedString(false, entry.Url));
            if (entry.Notes != null)
                pwEntry.Strings.Set(PwDefs.NotesField, new ProtectedString(false, entry.Notes));

            // 保存数据库
            SaveDatabase();
        }

        /// <summary>
        /// 列出所有条目（不含密码明文）
        /// </summary>
        public IList<KeePassEntrySummary> ListEntries()
        {
            EnsureUnlocked();

            var result = new List<KeePassEntrySummary>();
            CollectEntries(_database.RootGroup, result);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Lock();
        }

        private void EnsureUnlocked()
        {
            if (!IsUnlocked)
                throw new InvalidOperationException("密码库未解锁，请先调用 UnlockAsync");
        }

        private PwEntry FindEntryByUuid(string uuidHex)
        {
            PwEntry found = null;

            _database.RootGroup.SearchEntries(
                new SearchParameters()
                {
                    SearchInUuids = true,
                    SearchString = uuidHex,
                    ComparisonMode = StringComparison.OrdinalIgnoreCase
                },
                new List<PwEntry>(),
                ref found
            );

            // 如果 SearchEntries 不工作，使用递归查找
            if (found == null)
            {
                found = FindEntryRecursive(_database.RootGroup, uuidHex);
            }

            return found;
        }

        private PwEntry FindEntryRecursive(PwGroup group, string uuidHex)
        {
            foreach (var entry in group.Entries)
            {
                if (entry.Uuid.ToHexString().Equals(uuidHex, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            foreach (var childGroup in group.Groups)
            {
                var result = FindEntryRecursive(childGroup, uuidHex);
                if (result != null) return result;
            }

            return null;
        }

        private void CollectEntries(PwGroup group, List<KeePassEntrySummary> result)
        {
            foreach (var entry in group.Entries)
            {
                result.Add(new KeePassEntrySummary
                {
                    Id = entry.Uuid.ToHexString(),
                    Title = entry.Strings.ReadSafe(PwDefs.TitleField),
                    Username = entry.Strings.ReadSafe(PwDefs.UserNameField),
                    GroupPath = GetGroupPath(entry.ParentGroup)
                });
            }

            foreach (var childGroup in group.Groups)
            {
                CollectEntries(childGroup, result);
            }
        }

        private string GetGroupPath(PwGroup group)
        {
            if (group == null || group == _database.RootGroup)
                return "/";

            var parts = new List<string>();
            var current = group;
            while (current != null && current != _database.RootGroup)
            {
                parts.Add(current.Name);
                current = current.ParentGroup;
            }

            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        private PwGroup GetOrCreateGroup(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath) || groupPath == "/")
                return _database.RootGroup;

            var parts = groupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = _database.RootGroup;

            foreach (var part in parts)
            {
                var child = current.Groups.FirstOrDefault(g =>
                    g.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

                if (child == null)
                {
                    child = new PwGroup(true, true, part, PwIcon.Folder);
                    current.AddGroup(child, true);
                }

                current = child;
            }

            return current;
        }

        private void SaveDatabase()
        {
            var logger = new KeePassLib.Logging.NullStatusLogger();
            _database.Save(logger);
        }

        /// <summary>
        /// KeePassLib 空日志器
        /// </summary>
        private class NullStatusLogger : KeePassLib.Logging.IStatusLogger
        {
            public void StartLogging(string strOperation, bool bWriteOperationToLog) { }
            public void EndLogging() { }
            public bool SetProgress(uint uPercent) => true;
            public bool SetText(string strNewText, KeePassLib.Logging.LogStatusType lsType) => true;
            public bool ContinueWork() => true;
        }
    }
}
