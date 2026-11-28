using System;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Services;
using Renci.SshNet;

namespace Gdterm.Tests.Security
{
    internal static class SshKeyAuthenticationTests
    {
        public static void Run()
        {
            string error;
            foreach (var key in new[] { SshKeyFixtures.OpenSsh, SshKeyFixtures.RsaPem })
                Assert.True(SshPrivateKeyValidator.TryValidate(Encoding.UTF8.GetBytes(key), "", out error), "OpenSSH/PEM private key parses");
            var encrypted = Encoding.UTF8.GetBytes(SshKeyFixtures.EncryptedOpenSsh);
            Assert.True(!SshPrivateKeyValidator.TryValidate(encrypted, "", out error), "encrypted key requires passphrase");
            Assert.True(!SshPrivateKeyValidator.TryValidate(encrypted, "wrong", out error), "incorrect passphrase rejected");
            Assert.True(SshPrivateKeyValidator.TryValidate(encrypted, SshKeyFixtures.Passphrase, out error), "correct passphrase accepted");
            Assert.True(!SshPrivateKeyValidator.TryValidate(Encoding.UTF8.GetBytes("ssh-ed25519 AAAA public-key"), "", out error), "public key rejected");
            Assert.True(!SshPrivateKeyValidator.TryValidate(new byte[] { 1, 2, 3 }, "", out error), "malformed private key rejected");

            var dir = Path.Combine(Path.GetTempPath(), "gdterm-key-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "vault.kdbx");
                using (var vault = new KeePassService(path))
                {
                    Assert.True(vault.EnsureDatabaseAsync("TestVault!5839-private-key").GetAwaiter().GetResult(), "test vault created");
                    var entry = vault.CreateEntry(new KeePassEntry {
                        Title = "key.test", Hostname = "key.test", Protocol = "SSH", Port = 2222, Username = "keyuser",
                        SshPrivateKeyData = encrypted, SshPrivateKeyPassphrase = SshKeyFixtures.Passphrase
                    });
                    vault.Lock();
                    Assert.True(vault.UnlockAsync("TestVault!5839-private-key").GetAwaiter().GetResult(), "test vault reopened");
                    var loaded = vault.GetEntry(entry.Id);
                    Assert.True(loaded.SshPrivateKeyData.SequenceEqual(encrypted), "GetEntry restores key attachment");
                    Assert.Equal(SshKeyFixtures.Passphrase, loaded.SshPrivateKeyPassphrase, "GetEntry restores key passphrase");
                    var config = new ConnectionConfig { Protocol = ProtocolType.SSH, Host = "key.test", Port = 2222, Username = "keyuser" };
                    var resolver = new CredentialResolver(vault, null);
                    CheckAuth(resolver.Resolve(config), "auto match");
                    config.CredentialRefId = entry.Id;
                    CheckAuth(resolver.Resolve(config), "explicit selection");
                    config.CredentialRefId = null;
                    config.Host = "other.test";
                    config.GroupPath = "/servers/production";
                    var folders = new FolderCredentialStoreJson(Path.Combine(dir, "folders.json"));
                    folders.SetCredential("/servers", entry.Id);
                    CheckAuth(new CredentialResolver(vault, folders).Resolve(config), "folder inheritance");
                    var wire = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                    Assert.True(!wire.Contains("BEGIN OPENSSH PRIVATE KEY") && !wire.Contains(SshKeyFixtures.Passphrase), "vault does not expose plaintext secrets");
                    CheckJumpCredentials(vault, resolver, entry.Id);
                    // null update preserves key; explicit empty array removes it, including after reload.
                    vault.UpdateEntry(new KeePassEntry { Id = entry.Id, Title = "renamed" });
                    Assert.True(vault.GetEntry(entry.Id).SshPrivateKeyData.SequenceEqual(encrypted), "unrelated update preserves private key");
                    loaded = vault.GetEntry(entry.Id);
                    loaded.SshPrivateKeyData = new byte[0];
                    loaded.SshPrivateKeyPassphrase = "";
                    loaded.Password = "TestPassword!5839";
                    vault.UpdateEntry(loaded);
                    vault.Lock();
                    vault.UnlockAsync("TestVault!5839-private-key").GetAwaiter().GetResult();
                    Assert.True(vault.GetSshPrivateKey(entry.Id) == null && string.IsNullOrEmpty(vault.GetSshPrivateKeyPassphrase(entry.Id)), "switch to password removes persisted key and passphrase");
                    config.CredentialRefId = entry.Id;
                    var info = SshConnectionInfoFactory.Create("key.test", 2222, null, resolver.Resolve(config));
                    Assert.True(info.AuthenticationMethods.Single() is PasswordAuthenticationMethod, "switch to password uses password authentication");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void CheckJumpCredentials(KeePassService vault, CredentialResolver resolver, string keyId)
        {
            var password = vault.CreateEntry(new KeePassEntry { Title = "hop-password", Hostname = "hop.test",
                Protocol = "SSH", Username = "jump-user", Password = "JumpPassword!5839" });
            var cfg = new ConnectionConfig { Host = "unmatched-target.invalid", Protocol = ProtocolType.SSH,
                Username = "target-user", JumpChain = new JumpChainConfig { Hops = new System.Collections.Generic.List<JumpHop> {
                    new JumpHop { Host = "hop.test", CredentialRefId = password.Id },
                    new JumpHop { Host = "key.test", CredentialRefId = keyId, Username = "override" } } } };
            var p = resolver.Resolve(cfg);
            Assert.True(p != null && p.Password == null && p.SshPrivateKey == null, "target with no credential still resolves hop credentials");
            Assert.Equal("JumpPassword!5839", CredentialPayload.ResolveHopPassword(cfg.JumpChain.Hops[0], p), "hop password independent from target");
            Assert.Equal("jump-user", CredentialPayload.ResolveHopUsername(cfg.JumpChain.Hops[0], p), "hop inherits credential username");
            Assert.Equal("override", CredentialPayload.ResolveHopUsername(cfg.JumpChain.Hops[1], p), "hop overrides credential username");
            Assert.True(CredentialPayload.ResolveHopPrivateKey(cfg.JumpChain.Hops[1], p).SequenceEqual(Encoding.UTF8.GetBytes(SshKeyFixtures.EncryptedOpenSsh)), "hop encrypted key resolves without target entry");
            Assert.Equal(SshKeyFixtures.Passphrase, CredentialPayload.ResolveHopPrivateKeyPassphrase(cfg.JumpChain.Hops[1], p), "hop key passphrase resolves");
            var bytes = CredentialPayload.ResolveHopPrivateKey(cfg.JumpChain.Hops[1], p);
            p.ClearSecrets();
            Assert.True(bytes.All(b => b == 0), "resolved hop key is cleared");
            Assert.True(vault.GetSshPrivateKey(keyId).Any(b => b != 0), "clearing payload cannot mutate vault key");
            vault.DeleteEntry(password.Id);
            cfg.CredentialRefId = keyId;
            p = resolver.Resolve(cfg);
            Assert.True(p.SshPrivateKey != null && CredentialPayload.ResolveHopPassword(cfg.JumpChain.Hops[0], p) == null
                && CredentialPayload.ResolveHopPrivateKey(cfg.JumpChain.Hops[0], p) == null, "deleted hop does not fall back to target key or password");
            p.ClearSecrets();
            vault.Lock();
            Assert.True(resolver.Resolve(cfg) == null, "locked vault exposes no hop secrets");
            Assert.True(vault.UnlockAsync("TestVault!5839-private-key").GetAwaiter().GetResult(), "reopen vault after hop regression");
        }

        private static void CheckAuth(CredentialPayload credential, string scenario)
        {
            Assert.True(credential != null && credential.SshPrivateKey != null, scenario + " resolves private key");
            if (credential?.SshPrivateKey == null) return;
            foreach (var proxy in new[] { null, new ProxyConfig { Kind = ProxyKind.Socks5, Host = "localhost", Port = 1080 },
                new ProxyConfig { Kind = ProxyKind.Http, Host = "localhost", Port = 8080, Username = "proxy", Password = "test" } })
            {
                var info = SshConnectionInfoFactory.Create("key.test", 2222, null, credential, 15, proxy);
                Assert.True(info.AuthenticationMethods.Single() is PrivateKeyAuthenticationMethod, scenario + " creates private-key authentication with/without proxy");
                Assert.Equal("keyuser", info.Username, scenario + " retains username");
            }
            credential.ClearSecrets();
        }
    }
}
