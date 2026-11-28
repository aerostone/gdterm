using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Connections;
using Gdterm.Tunnel;
using Renci.SshNet;

class Program
{
    static int checks, failures;
    static void Check(bool ok, string text) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + text); if (!ok) failures++; }
    static SshClient Connect(int port, string key)
    {
        var c = new SshClient("127.0.0.1", port, Environment.UserName, new PrivateKeyFile(key));
        c.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5); c.Connect(); return c;
    }
    static PortForwardRule Rule(string id, int local, int remote, PortForwardType type = PortForwardType.Local) =>
        new PortForwardRule { Id = id, Name = "测试 \"rule\" \\ [ ]", Description = "first\nsecond", ConnectionId = "server-A", ConnectionName = "服务器 A", LocalPort = local, RemotePort = remote, Type = type };
    static int Free() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    static bool Banner(int port, int socksDestination = 0)
    {
        using (var c = new TcpClient("127.0.0.1", port))
        {
            c.ReceiveTimeout = c.SendTimeout = 5000;
            var s = c.GetStream();
            if (socksDestination != 0)
            {
                s.Write(new byte[] { 5, 1, 0 }, 0, 3);
                Check(s.ReadByte() == 5 && s.ReadByte() == 0, "SOCKS5 no-auth negotiation");
                var request = new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(socksDestination >> 8), (byte)socksDestination };
                s.Write(request, 0, request.Length);
                var reply = Read(s, 4); Check(reply[0] == 5 && reply[1] == 0, "SOCKS5 CONNECT succeeds");
                Read(s, reply[3] == 1 ? 6 : reply[3] == 4 ? 18 : s.ReadByte() + 2);
            }
            var banner = Read(s, 4); return Encoding.ASCII.GetString(banner) == "SSH-";
        }
    }
    static byte[] Read(Stream s, int count) { var b = new byte[count]; int n = 0; while (n < count) { int got = s.Read(b, n, count - n); if (got == 0) throw new EndOfStreamException(); n += got; } return b; }
    static async Task Until(Func<bool> test)
    {
        for (int i = 0; i < 100; i++) { if (test()) return; await Task.Delay(25); }
        throw new TimeoutException("Expected forward state did not arrive");
    }
    static void Signal(int pid, string signal) { using (var p = Process.Start("kill", signal + " " + pid)) { p.WaitForExit(); if (p.ExitCode != 0) throw new Exception("isolated session signal failed"); } }
    static async Task Main(string[] args)
    {
        int port = int.Parse(args[0]);
        using (var a = Connect(port, args[1])) using (var b = Connect(port, args[1]))
        using (var m = new PortForwardManager())
        {
            var tabA = new System.Windows.Forms.TabPage(); var tabB = new System.Windows.Forms.TabPage();
            var active = tabA;
            var sessionA = new Gdterm.Terminal.QuerySession(); var sessionB = new Gdterm.Terminal.QuerySession { Client = b };
            var tcA = new Gdterm.UI.Controls.TerminalControl { Config = new ConnectionConfig { Id = "A" }, Session = sessionA };
            var tcB = new Gdterm.UI.Controls.TerminalControl { Config = new ConnectionConfig { Id = "B" }, Session = sessionB };
            var states = new System.Collections.Generic.Dictionary<System.Windows.Forms.TabPage, Gdterm.UI.Services.TabSessionState>();
            var query = new Gdterm.UI.Services.TabActiveSessionQuery(() => active, states);
            Check(query.GetPortForwardHost("A") == null, "resolver supports panel opened before SSH tabs exist");
            states[tabA] = new Gdterm.UI.Services.TabSessionState { Config = tcA.Config, Control = tcA };
            states[tabB] = new Gdterm.UI.Services.TabSessionState { Config = tcB.Config, Control = tcB };
            sessionA.Client = a;
            Check(ReferenceEquals(query.GetPortForwardHost("A").GetUnderlyingClient(), a), "late SSH connection resolves without rebuilding panel");
            active = tabB;
            Check(ReferenceEquals(query.GetPortForwardHost("A").GetUnderlyingClient(), a), "switching active tab B still resolves rule assigned to A");
            Check(query.GetPortForwardHost("missing") == null, "unknown connection never falls back to active B");
            using (var replacement = Connect(port, args[1]))
            {
                sessionA.Client = replacement;
                Check(ReferenceEquals(query.GetPortForwardHost("A").GetUnderlyingClient(), replacement), "replacement SSH client resolves on every start");
                states[tabA].Control = new System.Collections.Generic.List<Gdterm.UI.Controls.TerminalControl> { tcB, tcA };
                states[tabA].PrimaryTerminal = tcB;
                Check(ReferenceEquals(query.GetPortForwardHost("A").GetUnderlyingClient(), replacement), "split pane resolves matching leaf rather than primary terminal");
                replacement.Disconnect();
                Check(query.GetPortForwardHost("A") == null, "disconnected A is not replaced by connected B");
            }
            sessionA.Client = a;
            var local = Rule("local", Free(), port);
            Check(await m.StartClientAsync(a, local), "local start");
            Check(Banner(local.LocalPort), "local carries real SSH bytes");
            Check(!await m.StartClientAsync(b, local), "duplicate live rule cannot silently switch owner");
            Check(a.ForwardedPorts.Count() == 1 && !b.ForwardedPorts.Any(), "live rule remains on SSH A");
            Check(m.Stop(b, local.Id), "stop with different caller client still cleans original owner");
            Check(!a.ForwardedPorts.Any() && b.IsConnected && a.IsConnected, "stop unregisters A without disconnecting shared clients");
            var remote = Rule("remote", port, Free(), PortForwardType.Remote);
            Check(await m.StartClientAsync(b, remote), "remote start");
            Check(Banner(remote.RemotePort), "remote carries real SSH bytes");
            await m.StopAsync(remote.Id); Check(!b.ForwardedPorts.Any(), "remote stop unregisters");
            var socks = Rule("socks", Free(), 0, PortForwardType.Dynamic);
            socks.RemoteHost = "";
            Check(await m.StartClientAsync(a, socks), "SOCKS5 starts without fixed destination");
            Check(Banner(socks.LocalPort, port), "SOCKS5 carries real SSH bytes");
            await m.StopAsync(socks.Id); Check(!a.ForwardedPorts.Any(), "SOCKS5 stop unregisters");
            var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
            try
            {
                var bad = Rule("occupied", ((IPEndPoint)occupied.LocalEndpoint).Port, port);
                for (int i = 0; i < 3; i++)
                {
                    Check(!await m.StartClientAsync(a, bad), "occupied listener rejected " + i);
                    Check(!a.ForwardedPorts.Any() && !string.IsNullOrEmpty(m.GetStatus(bad.Id).Error), "failed start rolls back registration and reports cause " + i);
                }
            }
            finally { occupied.Stop(); }
            foreach (int n in new[] { 0, -1, 65536 })
            {
                var bad = Rule("invalid", n, port);
                Check(!await m.StartClientAsync(a, bad) && !a.ForwardedPorts.Any(), "invalid port rejects before registration " + n);
            }
            var address = Rule("address", Free(), port); address.LocalHost = "";
            Check(!await m.StartClientAsync(a, address), "blank listen address rejected");
            address.LocalHost = "http://localhost"; Check(!await m.StartClientAsync(a, address), "URL is not accepted as host");
            Check(await m.StartClientAsync(a, local), "restart after stop");
            a.Disconnect();
            Check(!m.IsActive(local.Id), "disconnected forward never reports running");
            await Until(() => !m.GetStatus(local.Id).Busy);
            Check(!a.ForwardedPorts.Any(), "disconnected port unregistered from original owner");
            Check(await m.StartClientAsync(b, local), "same rule retries on reconnected/replacement client");
            Check(Banner(local.LocalPort), "retry transfers real data");
            await m.StopAsync(local.Id);
            a.Connect();
            Check(await m.StartClientAsync(a, local), "forward starts on original reconnected client");
            a.Disconnect(); a.Connect();
            m.GetStatus(local.Id); await Until(() => !m.GetStatus(local.Id).Busy);
            Check(await m.StartClientAsync(a, local) && Banner(local.LocalPort), "same SSH object reconnect reaps old port and accepts retry");
            await m.StopAsync(local.Id);
            for (int round = 0; round < 3; round++)
            {
                var ports = new System.Collections.Generic.HashSet<int>();
                while (ports.Count < 12) ports.Add(Free());
                var rules = ports.Select((p, i) => Rule("batch-" + i, p, port)).ToArray();
                var starts = await Task.WhenAll(rules.Select(r => m.StartClientAsync(b, r)));
                Check(starts.All(x => x) && b.ForwardedPorts.Count() == rules.Length, "parallel batch registers every forward " + round);
                Check(rules.All(r => Banner(r.LocalPort)), "parallel batch transmits every listener " + round);
                var stops = await Task.WhenAll(rules.Select(r => m.StopAsync(r.Id)));
                Check(stops.All(x => x) && !b.ForwardedPorts.Any(), "parallel batch removes every registration " + round);
            }
            int pid = int.Parse(b.RunCommand("echo $PPID").Result.Trim());
            Signal(pid, "-STOP");
            Task<bool> slowStart = null, slowStop = null;
            try
            {
                var slow = Rule("slow", port, Free(), PortForwardType.Remote);
                var sw = Stopwatch.StartNew(); slowStart = m.StartClientAsync(b, slow); sw.Stop();
                Check(sw.ElapsedMilliseconds < 300, "delayed remote start returns immediately to caller");
                await Task.Delay(180);
                Check(!slowStart.IsCompleted && m.GetStatus(slow.Id).State == PortForwardState.Starting, "pending server reply exposes Starting state");
                sw.Restart(); slowStop = m.StopAsync(slow.Id); sw.Stop();
                Check(sw.ElapsedMilliseconds < 300 && m.GetStatus(slow.Id).State == PortForwardState.Stopping, "cancel pending start is nonblocking and visible");
            }
            finally { Signal(pid, "-CONT"); }
            Check(!await slowStart && await slowStop, "cancelled slow start cannot publish Running");
            Check(!b.ForwardedPorts.Any() && b.IsConnected, "pending cancellation removes forward and preserves SSH");
            Check(await m.StartClientAsync(b, local), "running instance before shutdown");
            await m.ShutdownAsync();
            Check(!b.ForwardedPorts.Any() && !m.IsActive(local.Id), "shutdown cleans running registration");
            Check(!await m.StartClientAsync(b, local) && b.IsConnected, "disposed manager rejects restart without disposing shared SSH");
        }
        using (var c = Connect(port, args[1]))
        {
            var m = new PortForwardManager(); int pid = int.Parse(c.RunCommand("echo $PPID").Result.Trim());
            Signal(pid, "-STOP"); Task<bool> start = null; Task shutdown = null;
            try
            {
                start = m.StartClientAsync(c, Rule("shutdown-race", port, Free(), PortForwardType.Remote));
                await Task.Delay(150); shutdown = m.ShutdownAsync();
                Check(!shutdown.IsCompleted, "shutdown tracks pending startup cleanup");
            }
            finally { Signal(pid, "-CONT"); }
            await shutdown; Check(!await start && !c.ForwardedPorts.Any() && c.IsConnected, "shutdown racing handshake never resurrects port");
        }
        var path = Path.Combine(args[4], "rules.json"); var store = new PortForwardRuleStore(path);
        var stored = Rule("saved", 22345, port); store.Save(new System.Collections.Generic.List<PortForwardRule> { stored });
        var loaded = new PortForwardRuleStore(path).Load().Single();
        Check(loaded.Name == stored.Name && loaded.Description == stored.Description && loaded.ConnectionId == stored.ConnectionId && loaded.ConnectionName == stored.ConnectionName, "portable rule roundtrip preserves Unicode, quotes, newlines, ownership");
        stored.Description = "edited"; store.Save(new System.Collections.Generic.List<PortForwardRule> { stored });
        Check(store.Load().Single().Description == "edited", "atomic replacement persists edits");
        stored.LocalPort = 0;
        try { store.Save(new System.Collections.Generic.List<PortForwardRule> { stored }); Check(false, "invalid save rejected"); } catch (InvalidDataException) { Check(true, "invalid save rejected"); }
        Check(store.Load().Single().LocalPort == 22345, "invalid save preserves previous valid file");
        File.WriteAllText(path, "{broken");
        try { store.Load(); Check(false, "corrupt file surfaced"); } catch { Check(true, "corrupt file surfaced instead of silently overwriting rules"); }
        Console.WriteLine("checks=" + checks + " failures=" + failures); Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
