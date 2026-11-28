using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Core.Security;
using Gdterm.Tunnel;
using Gdterm.Tunnel.Models;
using Renci.SshNet;

// Links production tunnel and host-key code; only the SSH server and HTTP proxy are fixtures.
internal static class Program
{
    private static int _port;
    private static CredentialPayload _credential;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL " + label);
        Console.WriteLine("PASS " + label);
    }
    private static ConnectionConfig Config(string id, int hops = 1)
    {
        return new ConnectionConfig { Id = id, Host = "127.0.0.1", Port = _port,
            JumpChain = new JumpChainConfig { Hops = Enumerable.Range(0, hops).Select(i =>
                new JumpHop { Host = "127.0.0.1", Port = _port, CredentialRefId = "hop" }).ToList() } };
    }
    private static TunnelSession Session(TunnelManager m, string id)
    {
        return ((Dictionary<string, TunnelSession>)typeof(TunnelManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(m))[id];
    }
    private static void Command(TunnelEndpoint ep)
    {
        using (var c = SshHostKeyGuard.CreateClient(SshConnectionInfoFactory.Create(ep.LocalHost, ep.LocalPort,
            Environment.UserName, _credential, 5), "127.0.0.1", _port))
        {
            c.Connect();
            using (var cmd = c.CreateCommand("printf jump-target-ok"))
            {
                cmd.CommandTimeout = TimeSpan.FromSeconds(5);
                Check(cmd.Execute() == "jump-target-ok", "forwarded target executes command");
            }
        }
    }
    public static void Main(string[] args)
    {
        _port = int.Parse(args[0]);
        var key = File.ReadAllBytes(args[1]);
        _credential = new CredentialPayload { Username = Environment.UserName, SshPrivateKey = key,
            HopPrivateKeysByRefId = new Dictionary<string, byte[]> { { "hop", key } },
            HopUsernamesByRefId = new Dictionary<string, string> { { "hop", Environment.UserName } } };
        var store = new KnownHostsStore(args[2]);
        var hostKey = File.ReadAllText(args[3]).Split(' ');
        store.Save("127.0.0.1", _port, hostKey[0], hostKey[1]);
        SshHostKeyGuard.Configure(store, null);
        try
        {
            using (var m = new TunnelManager())
            {
                var cfg = Config("two-hop", 2);
                var ep = m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult();
                Check(m.GetStatus(cfg.Id).IsActive, "two-hop tunnel active");
                Command(ep);
                using (var sftp = SshHostKeyGuard.CreateSftpClient(SshConnectionInfoFactory.Create(ep.LocalHost, ep.LocalPort,
                    Environment.UserName, _credential, 5), "127.0.0.1", _port))
                {
                    sftp.Connect();
                    Check(sftp.ListDirectory("/").Any(), "SFTP lists directory through two hops");
                }
                var old = Session(m, cfg.Id);
                old.GetLastClient().Disconnect();
                Check(!m.GetStatus(cfg.Id).IsActive, "disconnected hop reports inactive");
                var next = m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult();
                Check(!ReferenceEquals(old, Session(m, cfg.Id)), "retry replaces dead session");
                Command(next);
                m.CloseAsync(cfg.Id).GetAwaiter().GetResult();
                Check(!m.GetStatus(cfg.Id).IsActive, "close removes active session");
            }
            using (var m = new TunnelManager())
            using (var proxy = new ConnectProxy(_port))
            {
                var cfg = Config("proxy", 2);
                cfg.Proxy = new ProxyConfig { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port };
                Command(m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult());
                Check(proxy.Requests == 1, "only first hop traverses HTTP CONNECT proxy");
            }
            using (var closed = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            using (var m = new TunnelManager())
            {
                closed.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // reserved but not listening
                var cfg = Config("bad-proxy");
                cfg.Proxy = new ProxyConfig { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = ((IPEndPoint)closed.LocalEndPoint).Port };
                bool failed = false;
                try { m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult(); } catch (Gdterm.Tunnel.Exceptions.TunnelException) { failed = true; }
                Check(failed && !m.GetStatus(cfg.Id).IsActive, "unavailable proxy cannot silently connect directly");
                cfg.Proxy = null;
                var empty = Config("empty-chain", 0);
                failed = false;
                try { m.EstablishAsync(empty, _credential, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { failed = true; }
                Check(empty.RequiresTunnel && failed, "empty jump chain fails validation instead of direct connection");
                cfg.JumpChain.Hops[0].CredentialRefId = "deleted";
                failed = false;
                try { m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult(); } catch (Gdterm.Tunnel.Exceptions.TunnelException) { failed = true; }
                Check(failed, "missing hop credential cannot use target key");
            }
            int handshakes = 0;
            SshHostKeyGuard.LogSink = (s, msg) => Interlocked.Increment(ref handshakes);
            using (var m = new TunnelManager())
            using (var start = new Barrier(8))
            {
                var cfg = Config("concurrent");
                var tasks = Enumerable.Range(0, 8).Select(i => Task.Factory.StartNew(() => {
                    if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    return m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                Task.WaitAll(tasks);
                Check(handshakes == 1 && tasks.Select(t => t.Result.LocalPort).Distinct().Count() == 1,
                    "eight concurrent callers establish exactly one SSH client");
                Command(tasks[0].Result);
            }
            SshHostKeyGuard.LogSink = null;
            CheckCancelled(store, false);
            CheckCancelled(store, true);
        }
        finally { _credential.ClearSecrets(); SshHostKeyGuard.LogSink = null; }
        Console.WriteLine("SSH jump regression complete");
    }

    private static void CheckCancelled(KnownHostsStore store, bool dispose)
    {
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var m = new TunnelManager())
        {
            // Hold a real handshake callback to reproduce close/dispose while Connect is in flight.
            SshHostKeyGuard.LogSink = (s, msg) => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
            var cfg = Config(dispose ? "dispose-pending" : "close-pending");
            var task = m.EstablishAsync(cfg, _credential, CancellationToken.None);
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "pending connection reached handshake");
                if (dispose) m.Dispose(); else m.CloseAsync(cfg.Id).GetAwaiter().GetResult();
                SshHostKeyGuard.LogSink = null;
            }
            finally { release.Set(); }
            bool cancelled = false;
            try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !m.GetStatus(cfg.Id).IsActive, (dispose ? "dispose" : "close") + " cancels pending publication");
            if (!dispose) Command(m.EstablishAsync(cfg, _credential, CancellationToken.None).GetAwaiter().GetResult());
        }
    }

    private sealed class ConnectProxy : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly int _targetPort;
        public int Requests;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ConnectProxy(int targetPort) { _targetPort = targetPort; _listener.Start(); Task.Run((Func<Task>)Serve); }
        private async Task Serve()
        {
            try
            {
                var inbound = await _listener.AcceptTcpClientAsync();
                lock (_clients) _clients.Add(inbound);
                var input = inbound.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 4096 && await input.ReadAsync(one, 0, 1) == 1)
                {
                    header.Add(one[0]);
                    if (header.Count >= 4 && System.Text.Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n")) break;
                }
                var text = System.Text.Encoding.ASCII.GetString(header.ToArray());
                if (!text.StartsWith("CONNECT 127.0.0.1:" + _targetPort + " ")) throw new Exception("Unexpected proxy destination");
                Interlocked.Increment(ref Requests);
                var outbound = new TcpClient(); lock (_clients) _clients.Add(outbound);
                await outbound.ConnectAsync(IPAddress.Loopback, _targetPort);
                var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                await input.WriteAsync(response, 0, response.Length);
                await Task.WhenAny(input.CopyToAsync(outbound.GetStream()), outbound.GetStream().CopyToAsync(input));
            }
            catch (Exception ex) { Console.WriteLine("Proxy fixture: " + ex.GetType().Name); }
        }
        public void Dispose() { _listener.Stop(); lock (_clients) foreach (var c in _clients) c.Dispose(); }
    }
}
