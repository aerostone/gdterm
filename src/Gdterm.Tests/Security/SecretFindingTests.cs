using Gdterm.Security;

namespace Gdterm.Tests.Security
{
    public static class SecretFindingTests
    {
        public static void Run()
        {
            var shortFinding = new SecretFinding { MatchedContent = "short" };
            Assert.Equal("****", shortFinding.GetRedactedContent(), "short content fully masked");

            var longFinding = new SecretFinding
            {
                MatchedContent = "ABCDEFGHIJKLMNOPQRST"
            };
            var red = longFinding.GetRedactedContent();
            Assert.True(red.StartsWith("ABCD"), "redacted starts with first 4");
            Assert.True(red.EndsWith("QRST"), "redacted ends with last 4");
            Assert.True(red.Contains("****"), "redacted has mask");
            Assert.True(red != longFinding.MatchedContent, "redacted != full");
        }
    }
}
