using System;
using System.Collections.Generic;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Diagnostics;

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
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CredentialResolver", exSwallowed); } catch { } }
                }

                if (entry == null && _folderCredStore != null && !string.IsNullOrEmpty(config.GroupPath))
                {
                    try
                    {
                        var inheritedRefId = _folderCredStore.ResolveByInheritance(config.GroupPath);
                        if (!string.IsNullOrEmpty(inheritedRefId))
                            entry = GetKeePassEntry(inheritedRefId);
                    }
                    catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CredentialResolver", exSwallowed); } catch { } }
                }

                if (entry == null)
                {
                    try { entry = _keepass.FindEntryByConnection(config); }
                    catch (Exception ex) { DiagLog.Swallowed("CredentialResolver.Target", ex); }
                }

                if (entry == null && (config.JumpChain?.Hops == null || config.JumpChain.Hops.Count == 0)) return null;

                var credential = new CredentialPayload
                {
                    Username = !string.IsNullOrEmpty(entry?.Username) ? entry.Username : config.Username,
                    Password = entry?.Password
                };

                if (config.Protocol == ProtocolType.SSH && entry?.SshPrivateKeyData != null)
                {
                    credential.SshPrivateKey = entry.SshPrivateKeyData;
                    credential.SshPrivateKeyPassphrase = entry.SshPrivateKeyPassphrase;
                }

                // finding-06 / P1-10：预解析跳板密码与私钥，供 TunnelManager 使用
                PopulateHopCredentials(config, credential);

                return credential;
            }
            catch
            {
                return null;
            }
        }

        private void PopulateHopCredentials(ConnectionConfig config, CredentialPayload credential)
        {
            if (credential == null || config?.JumpChain?.Hops == null) return;
            Dictionary<string, string> pwdMap = null;
            Dictionary<string, byte[]> keyMap = null;
            Dictionary<string, string> ppMap = null;
            var userMap = new Dictionary<string, string>();
            foreach (var hop in config.JumpChain.Hops)
            {
                if (hop == null || string.IsNullOrEmpty(hop.CredentialRefId)) continue;
                if (userMap.ContainsKey(hop.CredentialRefId)) continue;
                try
                {
                    var hopEntry = GetKeePassEntry(hop.CredentialRefId);
                    if (hopEntry == null) continue;
                    userMap[hop.CredentialRefId] = hopEntry.Username;
                    if (pwdMap == null) pwdMap = new Dictionary<string, string>();
                    pwdMap[hop.CredentialRefId] = hopEntry.Password ?? "";
                    if (hopEntry.SshPrivateKeyData != null && hopEntry.SshPrivateKeyData.Length > 0)
                    {
                        if (keyMap == null) keyMap = new Dictionary<string, byte[]>();
                        keyMap[hop.CredentialRefId] = hopEntry.SshPrivateKeyData;
                        if (!string.IsNullOrEmpty(hopEntry.SshPrivateKeyPassphrase))
                        {
                            if (ppMap == null) ppMap = new Dictionary<string, string>();
                            ppMap[hop.CredentialRefId] = hopEntry.SshPrivateKeyPassphrase;
                        }
                    }
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("CredentialResolver", exSwallowed); } catch { } }
            }
            if (pwdMap != null)
                credential.HopPasswordsByRefId = pwdMap;
            credential.HopUsernamesByRefId = userMap;
            if (keyMap != null)
                credential.HopPrivateKeysByRefId = keyMap;
            if (ppMap != null)
                credential.HopPrivateKeyPassphrasesByRefId = ppMap;
        }

        private KeePassEntry GetKeePassEntry(string entryId)
        {
            if (_keepass == null || string.IsNullOrEmpty(entryId)) return null;
            return _keepass.GetEntry(entryId);
        }
    }
}
