namespace Gdterm.Tests.Core
{
    public static class DefaultPortsTests
    {
        public static void Run()
        {
            ConsoleWrite("DefaultPorts");
            Assert.Equal(22, Gdterm.Core.Constants.DefaultPorts.Ssh, "SSH default port");
            Assert.Equal(3389, Gdterm.Core.Constants.DefaultPorts.Rdp, "RDP default port");
        }

        private static void ConsoleWrite(string name)
        {
            System.Console.WriteLine("[test] " + name);
        }
    }
}
