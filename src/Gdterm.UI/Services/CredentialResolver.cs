using System;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 三层凭据解析：CredentialRefId → 文件夹继承 → FindEntryByConnection 智能匹配。
    /// 从 TabContainerControl 抽出，减轻上帝对象（finding-10）。
    /// </summary>
    public sealed class CredentialResolver
    {
        private readonly IKeePassService _keepass;
        private readonly IFolderCredentialStore _folderCredStore;

        public CredentialResolver(IKeePassService keepass, IFolderCredentialStore folderCredStore)
        {
            _keepass = keepass;
            _folderCredStore = folderCredStore;
        }

        public CredentialPayload Resolve(ConnectionConfig config)
        {
            if (config == null) return null;
            try
            {
                if (_keepass == null || !_keepass.IsUnlocked)
                    return null;

                KeePassEntry entry = null;

                if (!string.IsNullOrEmpty(config.CredentialRefId))
                {
                    try { entry = GetKeePassEntry(config.CredentialRefId); }
                    catch { }
                }

                if (entry == null && _folderCredStore != null && !string.IsNullOrEmpty(config.GroupPath))
                {
                    try
                    {
                        var inheritedRefId = _folderCredStore.ResolveByInheritance(config.GroupPath);
                        if (!string.IsNullOrEmpty(inheritedRefId))
                            entry = GetKeePassEntry(inheritedRefId);
                    }
                    catch { }
                }

                if (entry == null)
                    entry = _keepass.FindEntryByConnection(config);

                if (entry == null) return null;

                var credential = new CredentialPayload
                {
                    Username = !string.IsNullOrEmpty(entry.Username) ? entry.Username : config.Username,
                    Password = entry.Password ?? ""
                };

                if (config.Protocol == ProtocolType.SSH && entry.SshPrivateKeyData != null)
                {
                    credential.SshPrivateKey = entry.SshPrivateKeyData;
                    credential.SshPrivateKeyPassphrase = entry.SshPrivateKeyPassphrase;
                }

                return credential;
            }
            catch
            {
                return null;
            }
        }

        private KeePassEntry GetKeePassEntry(string entryId)
        {
            if (_keepass == null || string.IsNullOrEmpty(entryId)) return null;
            var entries = _keepass.ListEntries();
            foreach (var summary in entries)
            {
                if (summary.Id != entryId) continue;
                var cred = _keepass.GetCredential(entryId);
                return new KeePassEntry
                {
                    Id = summary.Id,
                    Title = summary.Title,
                    Username = cred != null ? cred.Username : null,
                    Password = cred != null ? cred.Password : null,
                    SshPrivateKeyData = _keepass.GetSshPrivateKey(entryId),
                    SshPrivateKeyPassphrase = _keepass.GetSshPrivateKeyPassphrase(entryId)
                };
            }
            return null;
        }
    }
}
