using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.KeePass.Models;
using KeePassLib;
using KeePassLib.Interfaces;
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
        private readonly HashSet<string> _injectedRdpHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _rdpCredLock = new object();

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

                var logger = new NullStatusLogger();
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

            // SSH 密钥（KeePassLib 2.30: ProtectedBinary + Binaries.Set）
            if (entry.SshPrivateKeyData != null && entry.SshPrivateKeyData.Length > 0)
            {
                pwEntry.Binaries.Set("id_rsa", new ProtectedBinary(true, entry.SshPrivateKeyData));

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
                pwEntry.Binaries.Set("id_rsa", new ProtectedBinary(true, entry.SshPrivateKeyData));
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

        // ===== RDP 凭据注入（CredWrite，避免 cmdkey 命令行明文密码） =====

        private const int CRED_TYPE_GENERIC = 1;
        // go-live P1-11：会话级凭据，进程退出后不残留 TERMSRV 到机器配置
        private const int CRED_PERSIST_SESSION = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2; // 保留常量，不再写入

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        private static string BuildRdpTarget(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("host required", nameof(host));
            // 去掉可能被注入的空白与控制字符
            var h = host.Trim();
            if (h.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("invalid host", nameof(host));
            return "TERMSRV/" + h;
        }

        public bool InjectRdpCredential(string host, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(username))
                return false;

            // 首选：CredWrite API，密码不进进程命令行，不会出现在任务管理器参数里
            IntPtr blob = IntPtr.Zero;
            try
            {
                var target = BuildRdpTarget(host);
                var secret = password ?? string.Empty;
                // Windows 凭据 API 期望 Unicode 字节（含结尾 null 可选）
                var bytes = System.Text.Encoding.Unicode.GetBytes(secret);
                blob = Marshal.AllocHGlobal(bytes.Length);
                if (bytes.Length > 0)
                    Marshal.Copy(bytes, 0, blob, bytes.Length);

                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    UserName = username,
                    CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob,
                    Persist = CRED_PERSIST_SESSION,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    Comment = "gdterm RDP",
                    TargetAlias = null
                };

                if (!CredWrite(ref cred, 0))
                    return false;

                lock (_rdpCredLock)
                    _injectedRdpHosts.Add(host.Trim());
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (blob != IntPtr.Zero)
                    Marshal.FreeHGlobal(blob);
            }
        }

        public void CleanupRdpCredential(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            try
            {
                var target = BuildRdpTarget(host);
                CredDelete(target, CRED_TYPE_GENERIC, 0);
            }
            catch { }
            finally
            {
                lock (_rdpCredLock)
                    _injectedRdpHosts.Remove(host.Trim());
            }
        }

        public void CleanupAllRdpCredentials()
        {
            string[] hosts;
            lock (_rdpCredLock)
                hosts = _injectedRdpHosts.ToArray();
            foreach (var h in hosts)
                CleanupRdpCredential(h);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { CleanupAllRdpCredentials(); } catch { }
            Lock();
        }

        // ===== 健康分析 =====

        public PasswordHealthReport AnalyzeHealth()
        {
            EnsureUnlocked();

            var report = new PasswordHealthReport();
            var allEntries = new List<PwEntry>();
            CollectPwEntries(_database.RootGroup, allEntries);

            report.TotalEntries = allEntries.Count;

            // 按密码哈希分组用于检测重复
            var passwordGroups = new Dictionary<string, List<PwEntry>>();
            var now = DateTime.UtcNow;

            foreach (var entry in allEntries)
            {
                var password = entry.Strings.ReadSafe(PwDefs.PasswordField);
                var title = entry.Strings.ReadSafe(PwDefs.TitleField);
                var username = entry.Strings.ReadSafe(PwDefs.UserNameField);
                var groupPath = GetGroupPath(entry.ParentGroup);

                var issue = new PasswordIssue
                {
                    EntryId = entry.Uuid.ToHexString(),
                    Title = title,
                    Username = username,
                    GroupPath = groupPath
                };

                // 检测空密码
                if (string.IsNullOrEmpty(password))
                {
                    issue.Issue = "密码为空";
                    issue.StrengthScore = 0;
                    report.EmptyPasswords.Add(issue);
                    continue;
                }

                // 计算密码强度
                int score = CalculatePasswordScore(password);
                issue.StrengthScore = score;

                // 检测弱密码
                if (score <= 40)
                {
                    issue.Issue = score <= 20 ? "极弱密码" : "弱密码";
                    report.WeakPasswords.Add(issue);
                }

                // 检测过期密码（超过 90 天）
                var lastMod = entry.LastModificationTime;
                if ((now - lastMod).TotalDays > 90)
                {
                    var expiredIssue = new PasswordIssue
                    {
                        EntryId = issue.EntryId,
                        Title = title,
                        Username = username,
                        GroupPath = groupPath,
                        Issue = $"密码已 {(int)(now - lastMod).TotalDays} 天未更新",
                        StrengthScore = score
                    };
                    report.ExpiredPasswords.Add(expiredIssue);
                }

                // 按密码哈希分组
                var hash = ComputePasswordHash(password);
                if (!passwordGroups.ContainsKey(hash))
                    passwordGroups[hash] = new List<PwEntry>();
                passwordGroups[hash].Add(entry);
            }

            // 检测重复密码
            foreach (var group in passwordGroups)
            {
                if (group.Value.Count > 1)
                {
                    var dupGroup = new DuplicatePasswordGroup
                    {
                        PasswordHash = group.Key.Substring(0, 8) + "..." // 只显示前 8 位
                    };

                    foreach (var entry in group.Value)
                    {
                        dupGroup.Entries.Add(new PasswordIssue
                        {
                            EntryId = entry.Uuid.ToHexString(),
                            Title = entry.Strings.ReadSafe(PwDefs.TitleField),
                            Username = entry.Strings.ReadSafe(PwDefs.UserNameField),
                            GroupPath = GetGroupPath(entry.ParentGroup),
                            Issue = $"与其他 {group.Value.Count - 1} 个条目共用密码"
                        });
                    }

                    report.DuplicatePasswords.Add(dupGroup);
                }
            }

            // 计算总分
            int deductions = 0;
            deductions += report.EmptyPasswords.Count * 15;
            deductions += report.WeakPasswords.Count * 10;
            deductions += report.DuplicatePasswords.Count * 8;
            deductions += report.ExpiredPasswords.Count * 3;
            report.HealthScore = Math.Max(0, 100 - deductions);

            if (report.HealthScore >= 90)
                report.Summary = "密码库健康状况良好";
            else if (report.HealthScore >= 70)
                report.Summary = "密码库存在一些需要关注的问题";
            else if (report.HealthScore >= 50)
                report.Summary = "密码库有较多安全隐患，建议尽快处理";
            else
                report.Summary = "密码库安全状况较差，请立即处理";

            return report;
        }

        private void CollectPwEntries(PwGroup group, List<PwEntry> result)
        {
            foreach (var entry in group.Entries)
                result.Add(entry);

            foreach (var childGroup in group.Groups)
                CollectPwEntries(childGroup, result);
        }

        private static int CalculatePasswordScore(string password)
        {
            if (string.IsNullOrEmpty(password)) return 0;

            int score = 0;
            score += Math.Min(password.Length * 3, 30); // 长度分（最多 30）

            bool hasUpper = false, hasLower = false, hasDigit = false, hasSpecial = false;
            foreach (var ch in password)
            {
                if (char.IsUpper(ch)) hasUpper = true;
                else if (char.IsLower(ch)) hasLower = true;
                else if (char.IsDigit(ch)) hasDigit = true;
                else hasSpecial = true;
            }

            if (hasUpper) score += 15;
            if (hasLower) score += 15;
            if (hasDigit) score += 15;
            if (hasSpecial) score += 25;

            // 连续字符扣分
            int consecutive = 0;
            for (int i = 1; i < password.Length; i++)
            {
                if (password[i] == password[i - 1] + 1 || password[i] == password[i - 1])
                    consecutive++;
            }
            score -= consecutive * 2;

            return Math.Max(0, Math.Min(100, score));
        }

        private static string ComputePasswordHash(string password)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(password);
                var hash = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "");
            }
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
            var logger = new NullStatusLogger();
            _database.Save(logger);
        }

        /// <summary>KeePassLib 2.30 兼容的空日志（Interfaces.IStatusLogger）。</summary>
        private sealed class NullStatusLogger : IStatusLogger
        {
            public void StartLogging(string strOperation, bool bWriteOperationToLog) { }
            public void EndLogging() { }
            public bool SetProgress(uint uPercent) { return true; }
            public bool SetText(string strNewText, LogStatusType lsType) { return true; }
            public bool ContinueWork() { return true; }
        }
    }
}
