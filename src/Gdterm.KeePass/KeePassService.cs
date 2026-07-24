using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
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
    /// 增强功能：SSH 密钥存储、智能匹配、Auto-Type、RDP 凭据注入
    /// </summary>
    public class KeePassService : IKeePassService
    {
        private readonly string _databasePath;
        private readonly PasswordStrengthValidator _validator;
        private PwDatabase _database;
        private bool _disposed;

        // KeePass 自定义字段名
        private const string SshKeyFieldName = "SSH Private Key";
        private const string SshKeyPassFieldName = "SSH Key Passphrase";
        private const string ProtocolFieldName = "Protocol";
        private const string HostnameFieldName = "Hostname";
        private const string PortFieldName = "Port";
        private const string AutoTypeFieldName = "AutoType Sequence";

        public bool IsUnlocked => _database?.IsOpen == true;

        public KeePassService(string databasePath)
        {
            if (string.IsNullOrEmpty(databasePath))
                throw new ArgumentNullException(nameof(databasePath));

            _databasePath = databasePath;
            _validator = new PasswordStrengthValidator();
        }

        public Task<bool> UnlockAsync(string masterPassword)
        {
            if (string.IsNullOrEmpty(masterPassword))
                return Task.FromResult(false);

            try
            {
                Lock();
                _database = new PwDatabase();

                var ioConnInfo = new IOConnectionInfo { Path = _databasePath };
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

        public void Lock()
        {
            if (_database != null)
            {
                try
                {
                    if (_database.IsOpen)
                        _database.Close();
                }
                catch { }

                _database = null;
            }
        }

        public CredentialPayload GetCredential(string credentialRefId)
        {
            EnsureUnlocked();

            if (string.IsNullOrEmpty(credentialRefId))
                throw new ArgumentNullException(nameof(credentialRefId));

            var entry = FindEntryByUuid(credentialRefId);
            if (entry == null)
                throw new KeyNotFoundException($"未找到 ID 为 {credentialRefId} 的密码条目");

            return new CredentialPayload
            {
                Username = entry.Strings.ReadSafe(PwDefs.UserNameField),
                Password = entry.Strings.ReadSafe(PwDefs.PasswordField)
            };
        }

        public KeePassEntry CreateEntry(KeePassEntry entry)
        {
            EnsureUnlocked();
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            // 校验密码强度
            if (!string.IsNullOrEmpty(entry.Password))
            {
                var violations = _validator.Validate(entry.Password);
                if (violations.Count > 0)
                    throw new WeakPasswordException(violations);
            }

            var group = GetOrCreateGroup(entry.GroupPath ?? "");
            var pwEntry = new PwEntry(true, true);

            // 基本字段
            pwEntry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, entry.Title ?? ""));
            pwEntry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, entry.Username ?? ""));
            pwEntry.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, entry.Password ?? ""));
            pwEntry.Strings.Set(PwDefs.UrlField, new ProtectedString(false, entry.Url ?? ""));
            pwEntry.Strings.Set(PwDefs.NotesField, new ProtectedString(false, entry.Notes ?? ""));

            // 自定义字段（协议、主机、端口）
            if (!string.IsNullOrEmpty(entry.Protocol))
                pwEntry.Strings.Set(ProtocolFieldName, new ProtectedString(false, entry.Protocol));
            if (!string.IsNullOrEmpty(entry.Hostname))
                pwEntry.Strings.Set(HostnameFieldName, new ProtectedString(false, entry.Hostname));
            if (entry.Port > 0)
                pwEntry.Strings.Set(PortFieldName, new ProtectedString(false, entry.Port.ToString()));
            if (!string.IsNullOrEmpty(entry.AutoTypeSequence))
                pwEntry.Strings.Set(AutoTypeFieldName, new ProtectedString(false, entry.AutoTypeSequence));

            // SSH 密钥
            if (entry.SshPrivateKeyData != null && entry.SshPrivateKeyData.Length > 0)
            {
                // 存储为附件
                var attachment = new PwBinaryEntry(entry.SshPrivateKeyData, true, "id_rsa");
                pwEntry.Binaries.Add(attachment);

                // 存储密码短语
                if (!string.IsNullOrEmpty(entry.SshPrivateKeyPassphrase))
                    pwEntry.Strings.Set(SshKeyPassFieldName, new ProtectedString(true, entry.SshPrivateKeyPassphrase));
            }

            group.AddEntry(pwEntry, true);
            SaveDatabase();

            entry.Id = pwEntry.Uuid.ToHexString();
            return entry;
        }

        public void UpdateEntry(KeePassEntry entry)
        {
            EnsureUnlocked();
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Id))
                throw new ArgumentException("条目 Id 不能为空", nameof(entry));

            if (!string.IsNullOrEmpty(entry.Password))
            {
                var violations = _validator.Validate(entry.Password);
                if (violations.Count > 0)
                    throw new WeakPasswordException(violations);
            }

            var pwEntry = FindEntryByUuid(entry.Id);
            if (pwEntry == null)
                throw new KeyNotFoundException($"未找到 ID 为 {entry.Id} 的密码条目");

            // 更新基本字段
            if (entry.Title != null) pwEntry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, entry.Title));
            if (entry.Username != null) pwEntry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, entry.Username));
            if (entry.Password != null) pwEntry.Strings.Set(PwDefs.PasswordField, new ProtectedString(true, entry.Password));
            if (entry.Url != null) pwEntry.Strings.Set(PwDefs.UrlField, new ProtectedString(false, entry.Url));
            if (entry.Notes != null) pwEntry.Strings.Set(PwDefs.NotesField, new ProtectedString(false, entry.Notes));

            // 更新自定义字段
            if (entry.Protocol != null) pwEntry.Strings.Set(ProtocolFieldName, new ProtectedString(false, entry.Protocol));
            if (entry.Hostname != null) pwEntry.Strings.Set(HostnameFieldName, new ProtectedString(false, entry.Hostname));
            if (entry.Port > 0) pwEntry.Strings.Set(PortFieldName, new ProtectedString(false, entry.Port.ToString()));
            if (entry.AutoTypeSequence != null) pwEntry.Strings.Set(AutoTypeFieldName, new ProtectedString(false, entry.AutoTypeSequence));

            // 更新 SSH 密钥
            if (entry.SshPrivateKeyData != null && entry.SshPrivateKeyData.Length > 0)
            {
                pwEntry.Binaries.Clear();
                var attachment = new PwBinaryEntry(entry.SshPrivateKeyData, true, "id_rsa");
                pwEntry.Binaries.Add(attachment);
            }
            if (entry.SshPrivateKeyPassphrase != null)
                pwEntry.Strings.Set(SshKeyPassFieldName, new ProtectedString(true, entry.SshPrivateKeyPassphrase));

            SaveDatabase();
        }

        public void DeleteEntry(string entryId)
        {
            EnsureUnlocked();
            if (string.IsNullOrEmpty(entryId)) throw new ArgumentNullException(nameof(entryId));

            var pwEntry = FindEntryByUuid(entryId);
            if (pwEntry == null)
                throw new KeyNotFoundException($"未找到 ID 为 {entryId} 的密码条目");

            var parentGroup = pwEntry.ParentGroup;
            if (parentGroup != null)
            {
                parentGroup.Entries.Remove(pwEntry);
                SaveDatabase();
            }
        }

        public IList<KeePassEntrySummary> ListEntries()
        {
            EnsureUnlocked();
            var result = new List<KeePassEntrySummary>();
            CollectEntries(_database.RootGroup, result);
            return result;
        }

        // ===== 智能匹配 =====

        public KeePassEntry FindEntryByConnection(ConnectionConfig config)
        {
            EnsureUnlocked();
            if (config == null) return null;

            var allEntries = new List<KeePassEntrySummary>();
            CollectEntries(_database.RootGroup, allEntries);

            // 策略1：精确匹配 URL（host:port）
            var match = FindByHostPort(allEntries, config.Host, config.Port);
            if (match != null) return GetFullEntry(match.Id);

            // 策略2：标题包含 host
            match = allEntries.FirstOrDefault(e =>
                e.Title != null && e.Title.IndexOf(config.Host, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null) return GetFullEntry(match.Id);

            // 策略3：用户名匹配 + 协议匹配
            match = allEntries.FirstOrDefault(e =>
                e.Username != null && e.Username.Equals(config.Username, StringComparison.OrdinalIgnoreCase));
            if (match != null) return GetFullEntry(match.Id);

            return null;
        }

        private KeePassEntrySummary FindByHostPort(List<KeePassEntrySummary> entries, string host, int port)
        {
            foreach (var entry in entries)
            {
                // 检查自定义字段
                var pwEntry = FindEntryByUuid(entry.Id);
                if (pwEntry == null) continue;

                var entryHost = pwEntry.Strings.ReadSafe(HostnameFieldName);
                var entryPort = pwEntry.Strings.ReadSafe(PortFieldName);
                var entryUrl = pwEntry.Strings.ReadSafe(PwDefs.UrlField);

                // 匹配自定义 Hostname 字段
                if (!string.IsNullOrEmpty(entryHost) &&
                    entryHost.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(entryPort) || entryPort == port.ToString())
                        return entry;
                }

                // 匹配 URL 字段（ssh://host:port）
                if (!string.IsNullOrEmpty(entryUrl) &&
                    entryUrl.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry;
                }
            }
            return null;
        }

        private KeePassEntry GetFullEntry(string entryId)
        {
            var pwEntry = FindEntryByUuid(entryId);
            if (pwEntry == null) return null;

            return new KeePassEntry
            {
                Id = pwEntry.Uuid.ToHexString(),
                Title = pwEntry.Strings.ReadSafe(PwDefs.TitleField),
                Username = pwEntry.Strings.ReadSafe(PwDefs.UserNameField),
                Password = pwEntry.Strings.ReadSafe(PwDefs.PasswordField),
                Url = pwEntry.Strings.ReadSafe(PwDefs.UrlField),
                Notes = pwEntry.Strings.ReadSafe(PwDefs.NotesField),
                GroupPath = GetGroupPath(pwEntry.ParentGroup),
                Protocol = pwEntry.Strings.ReadSafe(ProtocolFieldName),
                Hostname = pwEntry.Strings.ReadSafe(HostnameFieldName),
                Port = int.TryParse(pwEntry.Strings.ReadSafe(PortFieldName), out int p) ? p : 0,
                AutoTypeSequence = pwEntry.Strings.ReadSafe(AutoTypeFieldName),
                SshPrivateKeyPassphrase = pwEntry.Strings.ReadSafe(SshKeyPassFieldName)
            };
        }

        // ===== SSH 密钥 =====

        public byte[] GetSshPrivateKey(string entryId)
        {
            EnsureUnlocked();
            var pwEntry = FindEntryByUuid(entryId);
            if (pwEntry == null) return null;

            // 从附件获取
            var binary = pwEntry.Binaries.Get("id_rsa");
            if (binary != null)
                return binary.ReadData();

            return null;
        }

        public string GetSshPrivateKeyPassphrase(string entryId)
        {
            EnsureUnlocked();
            var pwEntry = FindEntryByUuid(entryId);
            if (pwEntry == null) return null;

            return pwEntry.Strings.ReadSafe(SshKeyPassFieldName);
        }

        // ===== Auto-Type =====

        public void PerformAutoType(string entryId, string customSequence = null)
        {
            EnsureUnlocked();
            var pwEntry = FindEntryByUuid(entryId);
            if (pwEntry == null)
                throw new KeyNotFoundException($"未找到 ID 为 {entryId} 的密码条目");

            var username = pwEntry.Strings.ReadSafe(PwDefs.UserNameField);
            var password = pwEntry.Strings.ReadSafe(PwDefs.PasswordField);

            // 获取序列
            var sequence = customSequence ??
                           pwEntry.Strings.ReadSafe(AutoTypeFieldName) ??
                           GetDefaultAutoTypeSequence(pwEntry);

            // 替换占位符
            sequence = sequence
                .Replace("{USERNAME}", username)
                .Replace("{PASSWORD}", password)
                .Replace("{ENTER}", "\n")
                .Replace("{TAB}", "\t")
                .Replace("{SPACE}", " ");

            // 发送按键
            SendKeys.SendWait(sequence);
        }

        private string GetDefaultAutoTypeSequence(PwEntry entry)
        {
            var protocol = entry.Strings.ReadSafe(ProtocolFieldName).ToUpperInvariant();

            // RDP 默认序列：用户名 TAB 密码 ENTER
            if (protocol == "RDP")
                return "{USERNAME}{TAB}{PASSWORD}{ENTER}";

            // SSH 默认序列：用户名 ENTER 密码 ENTER
            return "{USERNAME}{ENTER}{PASSWORD}{ENTER}";
        }

        // ===== RDP 凭据注入 =====

        public bool InjectRdpCredential(string host, string username, string password)
        {
            try
            {
                // 使用 cmdkey 添加凭据到 Windows 凭据管理器
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmdkey",
                        Arguments = $"/generic:TERMSRV/{host} /user:{username} /pass:{password}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public void CleanupRdpCredential(string host)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmdkey",
                        Arguments = $"/delete:TERMSRV/{host}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(5000);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Lock();
        }

        // ===== 内部方法 =====

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

            if (found == null)
                found = FindEntryRecursive(_database.RootGroup, uuidHex);

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
                CollectEntries(childGroup, result);
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
