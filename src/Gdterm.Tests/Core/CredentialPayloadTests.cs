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
            Assert.Equal("leaf-pass", CredentialPayload.ResolveHopPassword(hop, leaf), "no map falls back to leaf");

            leaf.HopPasswordsByRefId = new Dictionary<string, string>
            {
                { "ref-a", "hop-pass" },
                { "ref-b", "other" }
            };
            Assert.Equal("hop-pass", CredentialPayload.ResolveHopPassword(hop, leaf), "mapped hop password");

            hop.CredentialRefId = "missing";
            Assert.Equal("leaf-pass", CredentialPayload.ResolveHopPassword(hop, leaf), "missing ref falls back");

            hop.CredentialRefId = null;
            Assert.Equal("leaf-pass", CredentialPayload.ResolveHopPassword(hop, leaf), "null ref falls back");

            Assert.Equal(null, CredentialPayload.ResolveHopPassword(hop, null), "null credential");

            leaf.ClearSecrets();
            Assert.Equal(null, leaf.Password, "password cleared");
            Assert.True(leaf.HopPasswordsByRefId == null, "hop map cleared");
        }
    }
}
