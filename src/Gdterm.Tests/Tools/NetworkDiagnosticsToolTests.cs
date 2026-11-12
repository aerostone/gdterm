using System;
using System.Collections.Generic;
using Gdterm.Tools.Modules;

namespace Gdterm.Tests.Tools
{
    /// <summary>
    /// 网络诊断（ping / traceroute / DNS）纯逻辑回归：平台命令解析与参数拼装。
    ///
    /// 为什么值得单独测：这块代码里最容易错、且错得最"安静"的是**平台语义**——
    /// unix 的 `ping -n` 是不反解、`-c` 才是次数，Windows 反过来。拼错的后果是
    /// 用户看到一串不带计数的输出，不会报错，只会觉得"这工具不好用"。
    /// UI 面板与进程执行不在测试范围（需要真进程/真窗口），只测可判定的拼装层。
    /// </summary>
    public static class NetworkDiagnosticsToolTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] NetworkDiagnosticsTool");
            TestPingArguments();
            TestTracerouteArguments();
            TestDnsArguments();
            TestResolveCommand();
            TestTargetIsSingleArgument();
        }

        private static void TestPingArguments()
        {
            var win = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Ping, "ping", "example.com", 4, true);
            Assert.Equal("-n 4 example.com", Join(win), "ping: windows -n <count>");
            var unix = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Ping, "ping", "example.com", 4, false);
            Assert.Equal("-c 4 example.com", Join(unix), "ping: unix -c <count>");
            // 次数非法时回落 4 次，不能出现 "ping -n 0"
            var zero = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Ping, "ping", "h", 0, true);
            Assert.Equal("-n 4 h", Join(zero), "ping: count<=0 回落 4");
        }

        private static void TestTracerouteArguments()
        {
            var win = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Traceroute, "tracert", "10.0.0.1", 4, true);
            Assert.Equal("-d 10.0.0.1", Join(win), "traceroute: windows -d（不反解）");
            var unix = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Traceroute, "traceroute", "10.0.0.1", 4, false);
            Assert.Equal("-n 10.0.0.1", Join(unix), "traceroute: unix -n（不反解）");
        }

        private static void TestDnsArguments()
        {
            var dns = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Dns, "nslookup", "corp.local", 4, true);
            Assert.Equal("corp.local", Join(dns), "dns: 目标即唯一参数");
        }

        private static void TestResolveCommand()
        {
            // 有 tracert 就优先 tracert（Windows），没有则 traceroute（unix）
            Assert.Equal("tracert", NetworkDiagnosticsTool.ResolveCommand(DiagnosticKind.Traceroute,
                n => n == "tracert"), "resolve: 优先 tracert");
            Assert.Equal("traceroute", NetworkDiagnosticsTool.ResolveCommand(DiagnosticKind.Traceroute,
                n => n == "traceroute"), "resolve: 回落 traceroute");
            // 一个都没有 → null（调用方据此报"本机没找到"，而不是抛异常）
            Assert.True(NetworkDiagnosticsTool.ResolveCommand(DiagnosticKind.Ping, n => false) == null,
                "resolve: 全缺 → null");
            Assert.Equal("dig", NetworkDiagnosticsTool.ResolveCommand(DiagnosticKind.Dns,
                n => n == "dig"), "resolve: dns 认 dig");
        }

        /// <summary>
        /// 目标串必须是**一个**参数——这是防命令注入的结构性保证。
        /// 用户把 `1.1.1.1 ; rm -rf /` 贴进来时，它只会作为一个非法主机名传给 ping。
        /// </summary>
        private static void TestTargetIsSingleArgument()
        {
            var args = NetworkDiagnosticsTool.BuildArguments(DiagnosticKind.Ping, "ping",
                "1.1.1.1 & calc.exe", 2, true);
            Assert.Equal("-n 2 1.1.1.1 & calc.exe", Join(args), "inject: 目标整体作为一个参数（不经 shell）");
            int last = args.Count - 1;
            Assert.True(args[last].IndexOf("calc.exe") > 0, "inject: 未被空格拆成多个参数");
        }

        private static string Join(List<string> args)
        {
            return string.Join(" ", args.ToArray());
        }
    }
}
