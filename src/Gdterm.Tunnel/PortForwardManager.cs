using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Tunnel
{
    public enum PortForwardState { Stopped, Starting, Running, Stopping, Failed }

    public sealed class PortForwardStatus
    {
        public PortForwardState State { get; }
        public string Error { get; }
        public bool Busy => State == PortForwardState.Starting || State == PortForwardState.Stopping;
        public PortForwardStatus(PortForwardState state, string error = null) { State = state; Error = error; }
    }

    /// <summary>Owns forwards, never the shared SSH clients. Network work never holds the state lock.</summary>
    public sealed class PortForwardManager : IDisposable
    {
        private sealed class Entry
        {
            public string Id;
            public SshClient Owner;
            public ForwardedPort Port;
            public readonly object Operation = new object();
            public bool StopRequested, Cleaned;
            public Task<bool> StartTask, StopTask;
            public string StopError, CleanupError;
            public EventHandler<Renci.SshNet.Common.ExceptionEventArgs> ErrorHandler;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, PortForwardStatus> _statuses = new Dictionary<string, PortForwardStatus>();
        private readonly Timer _monitor;
        private ISshPortForwardHost _boundHost;
        private bool _disposed;
        private Task _shutdown;
        public event Action<string, bool> ForwardStatusChanged;

        public PortForwardManager() { _monitor = new Timer(_ => Monitor(), null, 500, 500); }
        public void Bind(ISshPortForwardHost host) { lock (_sync) { if (!_disposed) _boundHost = host; } }
        public void Unbind() { lock (_sync) _boundHost = null; }
        public bool HasBoundHost { get { lock (_sync) return _boundHost != null && _boundHost.IsConnected; } }

        public Task<bool> StartAsync(ISshPortForwardHost host, PortForwardRule rule)
        {
            try { return StartClientAsync(host?.GetUnderlyingClient() as SshClient, rule); }
            catch (Exception ex) { return Failed(rule?.Id, ex.Message); }
        }

        // Synchronous compatibility API. UI uses StartAsync / StopAsync exclusively.
        public bool StartLocal(PortForwardRule rule) => StartLocal(ResolveClient(), rule);
        public bool StartRemote(PortForwardRule rule) => StartRemote(ResolveClient(), rule);
        public bool StartLocal(SshClient client, PortForwardRule rule) => StartTyped(client, rule, PortForwardType.Local);
        public bool StartRemote(SshClient client, PortForwardRule rule) => StartTyped(client, rule, PortForwardType.Remote);
        public bool StartDynamic(SshClient client, PortForwardRule rule) => StartTyped(client, rule, PortForwardType.Dynamic);
        private bool StartTyped(SshClient client, PortForwardRule rule, PortForwardType type)
        {
            if (rule == null) return false;
            var copy = Copy(rule); copy.Type = type;
            return StartClientAsync(client, copy).GetAwaiter().GetResult();
        }

        public Task<bool> StartClientAsync(SshClient client, PortForwardRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id)) return Task.FromResult(false);
            rule = Copy(rule);
            var error = rule.EndpointError();
            if (error != null) return Failed(rule.Id, error);
            if (client == null || !Connected(client)) return Failed(rule.Id, L10n.T("所属 SSH 连接未连接，请连接后重试"));
            // Reap a disconnected instance before retry; it must finish removing its original port first.
            GetStatus(rule.Id);
            Task<bool> result;
            lock (_sync)
            {
                if (_disposed) return Task.FromResult(false);
                if (_entries.ContainsKey(rule.Id)) return Task.FromResult(false);
                var entry = new Entry { Id = rule.Id, Owner = client };
                _entries.Add(rule.Id, entry);
                _statuses[rule.Id] = new PortForwardStatus(PortForwardState.Starting);
                result = entry.StartTask = Task.Run(() => StartCore(entry, rule));
            }
            Notify(rule.Id);
            return result;
        }

        private bool StartCore(Entry entry, PortForwardRule rule)
        {
            lock (entry.Operation)
            {
                string error = null;
                try
                {
                    bool cancelled;
                    lock (_sync) cancelled = entry.StopRequested || _disposed;
                    if (cancelled) return Finish(entry, null);
                    entry.Port = rule.Type == PortForwardType.Dynamic
                        ? (ForwardedPort)new ForwardedPortDynamic(rule.LocalHost, (uint)rule.LocalPort)
                        : rule.Type == PortForwardType.Remote
                            ? new ForwardedPortRemote(rule.RemoteHost, (uint)rule.RemotePort, rule.LocalHost, (uint)rule.LocalPort)
                            : (ForwardedPort)new ForwardedPortLocal(rule.LocalHost, (uint)rule.LocalPort, rule.RemoteHost, (uint)rule.RemotePort);
                    entry.ErrorHandler = (s, e) => RecordChannelError(entry, e.Exception.Message);
                    entry.Port.Exception += entry.ErrorHandler;
                    entry.Owner.AddForwardedPort(entry.Port);
                    entry.Port.Start();
                    bool publish;
                    lock (_sync)
                    {
                        publish = !entry.StopRequested && !_disposed && Connected(entry.Owner) && entry.Port.IsStarted;
                        if (publish) _statuses[entry.Id] = new PortForwardStatus(PortForwardState.Running);
                    }
                    if (publish) { Notify(entry.Id); return true; }
                    lock (_sync)
                        if (!entry.StopRequested && !_disposed) error = L10n.T("SSH 已断开或转发已停止，请重新连接后启动");
                }
                catch (Exception ex) { error = ex.Message; }
                return Finish(entry, error);
            }
        }

        private Task<bool> Failed(string id, string error)
        {
            if (!string.IsNullOrEmpty(id))
            {
                lock (_sync)
                {
                    if (!_disposed && !_entries.ContainsKey(id)) _statuses[id] = new PortForwardStatus(PortForwardState.Failed, error);
                }
                Notify(id);
            }
            return Task.FromResult(false);
        }

        public Task<bool> StopAsync(string id) => RequestStop(id, null);
        private Task<bool> RequestStop(string id, string error)
        {
            if (string.IsNullOrEmpty(id)) return Task.FromResult(true);
            Task<bool> result;
            lock (_sync)
            {
                Entry entry;
                if (!_entries.TryGetValue(id, out entry)) return Task.FromResult(true);
                if (entry.StopTask != null) return entry.StopTask;
                entry.StopRequested = true;
                entry.StopError = error;
                _statuses[id] = new PortForwardStatus(PortForwardState.Stopping, error);
                result = entry.StopTask = Task.Run(() =>
                {
                    lock (entry.Operation) { Finish(entry, entry.StopError); return entry.CleanupError == null; }
                });
            }
            Notify(id);
            return result;
        }
        public bool Stop(string id) => StopAsync(id).GetAwaiter().GetResult();
        // The caller-supplied client is deliberately ignored: cleanup always uses the original owner.
        public bool Stop(SshClient client, string id) => Stop(id);

        private bool Finish(Entry entry, string error)
        {
            if (entry.Cleaned) return false;
            entry.Cleaned = true;
            var port = entry.Port;
            if (port != null)
            {
                port.Exception -= entry.ErrorHandler;
                try { port.Stop(); } catch (Exception ex) { entry.CleanupError = entry.CleanupError ?? ex.Message; error = error ?? ex.Message; }
                try { entry.Owner.RemoveForwardedPort(port); } catch (ObjectDisposedException) { }
                catch (Exception ex) { entry.CleanupError = entry.CleanupError ?? ex.Message; error = error ?? ex.Message; }
                try { port.Dispose(); } catch (Exception ex) { entry.CleanupError = entry.CleanupError ?? ex.Message; error = error ?? ex.Message; }
            }
            lock (_sync)
            {
                _entries.Remove(entry.Id);
                error = entry.StopError ?? error;
                _statuses[entry.Id] = new PortForwardStatus(error == null ? PortForwardState.Stopped : PortForwardState.Failed, error);
            }
            Notify(entry.Id);
            return false;
        }

        private void RecordChannelError(Entry entry, string error)
        {
            lock (_sync)
            {
                if (entry.StopRequested || entry.Cleaned || !_statuses.ContainsKey(entry.Id)) return;
                _statuses[entry.Id] = new PortForwardStatus(_statuses[entry.Id].State, error);
            }
            Notify(entry.Id);
        }
        private static bool Connected(SshClient client)
        {
            try { return client != null && client.IsConnected; } catch (ObjectDisposedException) { return false; }
        }
        private static bool Alive(Entry entry)
        {
            try { return Connected(entry.Owner) && entry.Port != null && entry.Port.IsStarted; }
            catch (ObjectDisposedException) { return false; }
        }
        public PortForwardStatus GetStatus(string id)
        {
            if (string.IsNullOrEmpty(id)) return new PortForwardStatus(PortForwardState.Stopped);
            bool dead;
            lock (_sync)
            {
                Entry entry; PortForwardStatus status;
                dead = _entries.TryGetValue(id, out entry) && _statuses.TryGetValue(id, out status) &&
                    status.State == PortForwardState.Running && !Alive(entry);
            }
            if (dead) RequestStop(id, L10n.T("SSH 已断开或转发已停止，请重新连接后启动"));
            lock (_sync) { PortForwardStatus status; return _statuses.TryGetValue(id, out status) ? status : new PortForwardStatus(PortForwardState.Stopped); }
        }
        public bool IsActive(string id) => GetStatus(id).State == PortForwardState.Running;
        private void Monitor()
        {
            string[] ids;
            lock (_sync) { if (_disposed) return; ids = _entries.Keys.ToArray(); }
            foreach (var id in ids) GetStatus(id);
        }
        private void Notify(string id)
        {
            var handlers = ForwardStatusChanged;
            if (handlers == null) return;
            bool active;
            lock (_sync) { PortForwardStatus s; active = _statuses.TryGetValue(id, out s) && s.State == PortForwardState.Running; }
            foreach (Action<string, bool> handler in handlers.GetInvocationList())
                try { handler(id, active); } catch { /* observers cannot interrupt ownership cleanup */ }
        }
        public Task ShutdownAsync()
        {
            lock (_sync)
            {
                if (_shutdown != null) return _shutdown;
                _disposed = true; _boundHost = null; _monitor.Dispose();
                var pending = new List<Task>();
                foreach (var entry in _entries.Values.ToArray())
                {
                    pending.Add(RequestStop(entry.Id, null));
                    if (entry.StartTask != null) pending.Add(entry.StartTask);
                }
                return _shutdown = Task.WhenAll(pending);
            }
        }
        /// <summary>Revokes starts immediately; cleanup continues in the background. Await ShutdownAsync for quiescence.</summary>
        public void Dispose() { ShutdownAsync(); }
        private SshClient ResolveClient() { lock (_sync) return _boundHost?.GetUnderlyingClient() as SshClient; }
        public static PortForwardRule Copy(PortForwardRule r) => new PortForwardRule
        {
            Id = r.Id, Name = r.Name, Type = r.Type, LocalHost = r.LocalHost, LocalPort = r.LocalPort,
            RemoteHost = r.RemoteHost, RemotePort = r.RemotePort, ConnectionId = r.ConnectionId,
            ConnectionName = r.ConnectionName, Description = r.Description, Enabled = r.Enabled
        };
        public static bool IsPortAvailable(int port, string host = "127.0.0.1")
        {
            TcpListener listener = null;
            try { listener = new TcpListener(IPAddress.Parse(host), port); listener.Start(); return true; }
            catch { return false; }
            finally { listener?.Stop(); }
        }
        public static int FindAvailablePort(int startFrom = 10000)
        {
            for (int port = Math.Max(1, startFrom); port <= 65535; port++) if (IsPortAvailable(port)) return port;
            return -1;
        }
    }
}
