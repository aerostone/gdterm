using System;
using Gdterm.Tests.Ai;
using Gdterm.Tests.Connections;
using Gdterm.Tests.Core;
using Gdterm.Tests.Logging;
using Gdterm.Tests.Scanning;
using Gdterm.Tests.Security;
using Gdterm.Tests.Sftp;
using Gdterm.Tests.Terminal;
using Gdterm.Tests.Ui;

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
            // 渲染性能基准：--perf-bench [out.json]（P2-8 基线；Windows GDI 路径）。
            // 放在单测之前：CI 里单测已经单独跑过，基准模式不必重复。
            int perfIdx = Array.IndexOf(args, "--perf-bench");
            if (perfIdx >= 0)
            {
                string perfOut = perfIdx + 1 < args.Length ? args[perfIdx + 1] : null;
                return PerfBench.Run(perfOut);
            }

            Console.WriteLine("=== gdterm unit tests (zero-NuGet runner) ===");
            Assert.Reset();

            // 单测抛异常不允许拖死整个进程——GHA 实证（0.1.4xx）：一个测试的
            // IndexOutOfRange 未捕获，后续全部测试未跑、证据只剩第一个失败。
            void RunTest(string name, Action run)
            {
                try { run(); }
                catch (Exception ex)
                {
                    Assert.True(false, name + " threw: " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                }
            }

            RunTest("DefaultPorts", DefaultPortsTests.Run);
            RunTest("LogSanitizer", LogSanitizerTests.Run);
            RunTest("ConnectionStoreJson", ConnectionStoreJsonTests.Run);
            RunTest("ChatHistoryStore", ChatHistoryStoreTests.Run);
            RunTest("AiAssistantService", AiAssistantServiceTests.Run);
            RunTest("CommandHistoryStore", CommandHistoryStoreTests.Run);
            RunTest("ExternalFormatImporter", ExternalFormatImporterTests.Run);
            RunTest("OnlineLinks", OnlineLinksTests.Run);
            RunTest("Otp", OtpTests.Run);
            RunTest("CredentialPayload", CredentialPayloadTests.Run);
            RunTest("SecretFinding", SecretFindingTests.Run);
            RunTest("SecurityManagerHash", SecurityManagerHashTests.Run);
            RunTest("SftpSyncPlanner", SftpSyncPlannerTests.Run);
            RunTest("SftpPermission", SftpPermissionTests.Run);
            RunTest("TerminalProfile", TerminalProfileTests.Run);
            RunTest("VtTerminalEngine", VtTerminalEngineTests.Run);
            RunTest("TerminalNotification", TerminalNotificationTests.Run);
            RunTest("TriggerAndLimits", TriggerAndLimitsTests.Run);
            RunTest("ZmodemReceiver", ZmodemReceiverTests.Run);
            RunTest("MacroRecorder", MacroRecorderTests.Run);
            RunTest("ScanPlugin", ScanPluginTests.Run);
            RunTest("AiSettingsFile", AiSettingsFileTests.Run);
            RunTest("PaletteUsageStore", PaletteUsageStoreTests.Run);

            // 可观测性失败信号（A4/A5/A8/A9）——依赖 CrashLog.Initialize 改写全局路径，必须最后跑
            RunTest("Observability", ObservabilityTests.Run);

            // UI 冒烟：--ui-smoke [outDir] 才跑（要桌面会话，默认单元测试不跑）
            int uiIdx = Array.IndexOf(args, "--ui-smoke");
            if (uiIdx >= 0)
            {
                string[] smokeArgs = new string[args.Length - uiIdx - 1];
                Array.Copy(args, uiIdx + 1, smokeArgs, 0, smokeArgs.Length);
                return UiSmokeRunner.Run(smokeArgs);
            }

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
