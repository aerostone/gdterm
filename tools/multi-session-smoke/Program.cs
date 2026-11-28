using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Terminal;
using Gdterm.Tests;
using Gdterm.Tests.Terminal;
using Renci.SshNet;
class Program
{
    static int Main(string[] args)
    {
        MultiSessionTests.Run();
        if (args.Length > 0) RealSsh(args).GetAwaiter().GetResult();
        Console.WriteLine("multi-session: " + Assert.Passes + " passed, " + Assert.Failures + " failed");
        return Assert.Failures == 0 ? 0 : 1;
    }
    static async Task RealSsh(string[] args)
    {
        using (var a = new SshClient("127.0.0.1", int.Parse(args[0]), Environment.UserName, new PrivateKeyFile(args[1])))
        using (var b = new SshClient("127.0.0.1", int.Parse(args[0]), Environment.UserName, new PrivateKeyFile(args[1])))
        {
            a.Connect(); b.Connect();
            var targets = new Dictionary<string, ITerminalSession> {
                ["a"] = new MultiSessionFake { Client = a, Hostname = "same" },
                ["b"] = new MultiSessionFake { Client = b, Hostname = "same" }
            };
            var executor = new BatchCommandExecutor();
            int completed = 0; executor.SessionCompleted += r => Interlocked.Increment(ref completed);
            var results = await executor.ExecuteAsync(targets, "printf '中文\\n'\r\nprintf 'line2\\n'", 3000);
            Assert.True(results.All(r => r.IsSuccess && r.ExitCode == 0), "SSH multiline succeeds on both");
            Assert.True(results.All(r => r.Output.Contains("中文") && r.Output.Contains("line2")), "SSH unicode stdout captured");
            Assert.True(results.All(r => r.DurationMs > 0 && r.DurationMs < 3000), "real elapsed duration");
            Assert.Equal(2, completed, "per-target progress completion");
            results = await executor.ExecuteAsync(targets, "true", 3000);
            Assert.True(results.All(r => r.IsSuccess), "empty output succeeds");
            results = await executor.ExecuteAsync(targets, "echo error >&2; exit 7", 3000);
            Assert.True(results.All(r => !r.IsSuccess && r.ExitCode == 7 && r.StandardError.Contains("error")), "stderr and nonzero exit fail accurately");
            results = await executor.ExecuteAsync(targets, "sleep 10", 200);
            Assert.True(results.All(r => r.Status == BatchCommandStatus.TimedOut), "slow commands time out");
            using (var ct = new CancellationTokenSource(200))
            {
                results = await executor.ExecuteAsync(targets, "sleep 10", 5000, ct.Token);
                Assert.True(results.All(r => r.Status == BatchCommandStatus.Cancelled), "running exec cancellation");
            }
            using (var ct = new CancellationTokenSource())
            {
                ct.Cancel();
                results = await executor.ExecuteAsync(targets, "echo should-not-send", 3000, ct.Token);
                Assert.True(results.All(r => r.Status == BatchCommandStatus.Cancelled), "queued cancellation");
            }
            Assert.True(a.IsConnected && b.IsConnected, "cancel and timeout preserve shared clients");
            results = await executor.ExecuteAsync(targets, "printf reused", 3000);
            Assert.True(results.All(r => r.IsSuccess && r.Output.Contains("reused")), "shared clients reusable");
            b.Disconnect();
            results = await executor.ExecuteAsync(targets, "echo still-running", 3000);
            Assert.True(results.Single(r => r.SessionId == "a").IsSuccess && !results.Single(r => r.SessionId == "b").IsSuccess, "one disconnected target cannot fail other");
        }
    }
}
