using System.Collections.Generic;
using Gdterm.Core.Models;

namespace Gdterm.Tests.Core
{
    public static class CredentialPayloadTests
    {
        public static void Run()
        {
            var leaf = new CredentialPayload { Password = "leaf-pass" };
            var hop = new JumpHop { Host = "j1", Port = 22, CredentialRefId = "ref-a" };
            Assert.Equal(null, CredentialPayload.ResolveHopPassword(hop, leaf), "no map must not expose target password");

            leaf.HopPasswordsByRefId = new Dictionary<string, string>
            {
                { "ref-a", "hop-pass" },
                { "ref-b", "other" }
            };
            Assert.Equal("hop-pass", CredentialPayload.ResolveHopPassword(hop, leaf), "mapped hop password");

            hop.CredentialRefId = "missing";
            Assert.Equal(null, CredentialPayload.ResolveHopPassword(hop, leaf), "missing explicit reference must not expose target password");

            hop.CredentialRefId = null;
            Assert.Equal(null, CredentialPayload.ResolveHopPassword(hop, leaf), "unconfigured hop must not inherit target password");

            Assert.Equal(null, CredentialPayload.ResolveHopPassword(hop, null), "null credential");

            hop.CredentialRefId = "ref-a";
            leaf.HopPasswordsByRefId["ref-a"] = "";
            Assert.Equal("", CredentialPayload.ResolveHopPassword(hop, leaf), "explicit empty hop password stays empty");
            leaf.SshPrivateKey = new byte[] { 1, 2 };
            Assert.Equal(null, CredentialPayload.ResolveHopPrivateKey(hop, leaf), "target private key never leaks to hop");
            var hopKey = new byte[] { 3, 4 };
            leaf.HopPrivateKeysByRefId = new Dictionary<string, byte[]> { { "ref-a", hopKey } };
            leaf.HopPrivateKeyPassphrasesByRefId = new Dictionary<string, string> { { "ref-a", "hop-phrase" } };
            leaf.HopUsernamesByRefId = new Dictionary<string, string> { { "ref-a", "vault-user" } };
            Assert.Equal("vault-user", CredentialPayload.ResolveHopUsername(hop, leaf), "hop uses its credential username");
            hop.Username = "override-user";
            Assert.Equal("override-user", CredentialPayload.ResolveHopUsername(hop, leaf), "hop username override wins");
            Assert.Equal("hop-phrase", CredentialPayload.ResolveHopPrivateKeyPassphrase(hop, leaf), "hop passphrase resolves independently");
            leaf.ClearSecrets();
            Assert.True(hopKey[0] == 0 && hopKey[1] == 0, "hop key bytes erased");
            Assert.True(leaf.HopUsernamesByRefId == null && leaf.HopPrivateKeyPassphrasesByRefId == null, "hop identity and passphrase maps cleared");
            var config = new ConnectionConfig();
            Assert.True(!config.RequiresTunnel, "plain connection is direct");
            config.JumpChain = new JumpChainConfig { Hops = new List<JumpHop> { hop } };
            Assert.True(config.RequiresTunnel, "jump-only config requires SSH/SFTP/RDP tunnel");
            config.JumpChain = new JumpChainConfig();
            Assert.True(config.RequiresTunnel, "invalid empty jump chain must reach validation instead of silently connecting directly");
            config.JumpChain = null;
            config.Tunnel = new TunnelConfig();
            Assert.True(config.RequiresTunnel, "legacy tunnel config still requires tunnel");
            Assert.Equal(null, leaf.Password, "password cleared");
            Assert.True(leaf.HopPasswordsByRefId == null, "hop map cleared");
        }
    }
}
