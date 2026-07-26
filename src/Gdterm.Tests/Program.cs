using System;
using Gdterm.Tests.Connections;
using Gdterm.Tests.Core;
using Gdterm.Tests.Logging;
using Gdterm.Tests.Security;
using Gdterm.Tests.Terminal;

namespace Gdterm.Tests
{
    /// <summary>
    /// 控制台测试入口。Windows:
    ///   msbuild gdterm.sln /p:Configuration=Release
    ///   src\Gdterm.Tests\bin\Release\Gdterm.Tests.exe
    /// 退出码 0=全过，1=有失败。
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("=== gdterm unit tests (zero-NuGet runner) ===");
            Assert.Reset();

            DefaultPortsTests.Run();
            LogSanitizerTests.Run();
            ConnectionStoreJsonTests.Run();
            CredentialPayloadTests.Run();
            SecretFindingTests.Run();
            SecurityManagerHashTests.Run();
            VtTerminalEngineTests.Run();

            Console.WriteLine();
            Console.WriteLine("Passed: {0}  Failed: {1}", Assert.Passes, Assert.Failures);
            if (Assert.Failures > 0)
            {
                Console.WriteLine("--- failures ---");
                foreach (var m in Assert.Messages)
                    Console.WriteLine(m);
                return 1;
            }
            Console.WriteLine("ALL OK");
            return 0;
        }
    }
}
