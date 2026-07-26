using System;
using System.Collections.Generic;
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

                // finding-06：预解析跳板 hop.CredentialRefId → 密码，供 TunnelManager 使用
                PopulateHopPasswords(config, credential);

                return credential;
            }
            catch
            {
                return null;
            }
        }

        private void PopulateHopPasswords(ConnectionConfig config, CredentialPayload credential)
        {
            if (credential == null || config?.JumpChain?.Hops == null) return;
            Dictionary<string, string> map = null;
            foreach (var hop in config.JumpChain.Hops)
            {
                if (hop == null || string.IsNullOrEmpty(hop.CredentialRefId)) continue;
                try
                {
                    var hopEntry = GetKeePassEntry(hop.CredentialRefId);
                    if (hopEntry == null || string.IsNullOrEmpty(hopEntry.Password)) continue;
                    if (map == null) map = new Dictionary<string, string>();
                    map[hop.CredentialRefId] = hopEntry.Password;
                }
                catch { }
            }
            if (map != null)
                credential.HopPasswordsByRefId = map;
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
