using System;
using System.Collections.Generic;
using Gdterm.Core.Models;
using Renci.SshNet;
using Gdterm.Core;

namespace Gdterm.Tunnel.Models
{
    /// <summary>一条跳板链的客户端和转发共同存活，按反向依赖顺序释放。</summary>
    internal class TunnelSession : IDisposable
    {
        private readonly object _sync = new object();
        private readonly List<SshClient> _hopClients = new List<SshClient>();
        private readonly List<KeyValuePair<SshClient, ForwardedPortLocal>> _intermediateForwards
            = new List<KeyValuePair<SshClient, ForwardedPortLocal>>();
        private ForwardedPortLocal _forwardedPort;
        private SshClient _forwardOwner;
        private bool _disposed;
        private bool _ready;

        public string ConnectionId { get; }
        public DateTime EstablishedAt { get; }
        public string LastError { get; private set; }
        public int LocalPort { get; private set; }
        public string LocalHost { get; private set; }
        public bool IsActive
        {
            get
            {
                lock (_sync)
                {
                    if (_disposed || !_ready) return false;
                    try
                    {
                        bool alive = _forwardedPort != null && _forwardedPort.IsStarted;
                        foreach (var client in _hopClients) alive &= client.IsConnected;
                        foreach (var forward in _intermediateForwards) alive &= forward.Value.IsStarted;
                        if (alive) return true;
                    }
                    catch (ObjectDisposedException) { }
                    _ready = false;
                    if (string.IsNullOrEmpty(LastError)) LastError = L10n.T("跳板连接已断开，请重新连接。");
                    return false;
                }
            }
        }

        public TunnelSession(string connectionId)
        {
            ConnectionId = connectionId;
            EstablishedAt = DateTime.UtcNow;
        }

        public void ConnectHop(JumpHop hop, CredentialPayload credential, SshClient throughClient = null,
            ProxyConfig proxy = null)
        {
            if (hop == null || string.IsNullOrWhiteSpace(hop.Host) || hop.Port < 1 || hop.Port > 65535)
                throw new InvalidOperationException(L10n.T("跳板主机或端口无效，请编辑连接后重试。"));
            var password = CredentialPayload.ResolveHopPassword(hop, credential);
            var privateKey = CredentialPayload.ResolveHopPrivateKey(hop, credential);
            if (string.IsNullOrEmpty(hop.CredentialRefId) || (privateKey == null && password == null))
                throw new InvalidOperationException(L10n.T("跳板凭据未配置、已删除或密码库未解锁，请为该跳板选择凭据。"));

            var hopCredential = new CredentialPayload
            {
                Username = CredentialPayload.ResolveHopUsername(hop, credential),
                Password = password,
                SshPrivateKey = privateKey,
                SshPrivateKeyPassphrase = CredentialPayload.ResolveHopPrivateKeyPassphrase(hop, credential)
            };
            string host = hop.Host;
            int port = hop.Port;
            ForwardedPortLocal forward = null;
            SshClient client = null;
            try
            {
                if (throughClient != null)
                {
                    // Bind port 0 directly; no probe-then-bind race with other applications.
                    forward = new ForwardedPortLocal("127.0.0.1", 0, hop.Host, (uint)hop.Port);
                    throughClient.AddForwardedPort(forward);
                    forward.Start();
                    host = "127.0.0.1";
                    port = (int)forward.BoundPort;
                }
                var info = SshConnectionInfoFactory.Create(host, port, hopCredential.Username,
                    hopCredential, 30, throughClient == null ? proxy : null);
                client = SshHostKeyGuard.CreateClient(info, hop.Host, hop.Port);
                client.ErrorOccurred += (s, e) => SetError(e.Exception.Message);
                SshKeepAlive.Apply(client, SshKeepAlive.DefaultSeconds);
                try { client.Connect(); }
                catch (Exception ex) { throw SshHostKeyGuard.Describe(client, ex); }
                lock (_sync)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(TunnelSession));
                    _hopClients.Add(client);
                    if (forward != null)
                        _intermediateForwards.Add(new KeyValuePair<SshClient, ForwardedPortLocal>(throughClient, forward));
                }
                // Ownership transferred: the next hop depends on this forward for its whole lifetime.
            }
            catch
            {
                try { client?.Dispose(); } catch { }
                ReleaseForward(throughClient, forward);
                throw;
            }
        }

        public void ConnectDirect(ConnectionConfig config, CredentialPayload credential)
        {
            var user = credential?.Username ?? config.Username ?? "root";
            var info = SshConnectionInfoFactory.Create(config.Host, config.Port, user,
                credential ?? new CredentialPayload(), 30, config.Proxy);
            var client = SshHostKeyGuard.CreateClient(info, config.Host, config.Port);
            client.ErrorOccurred += (s, e) => SetError(e.Exception.Message);
            SshKeepAlive.Apply(client, config);
            try
            {
                try { client.Connect(); }
                catch (Exception ex) { throw SshHostKeyGuard.Describe(client, ex); }
                lock (_sync)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(TunnelSession));
                    _hopClients.Add(client);
                }
            }
            catch { client.Dispose(); throw; }
        }

        public void StartPortForwarding(ConnectionConfig config)
        {
            var lastClient = GetLastClient();
            var localPort = config.Tunnel?.LocalPort ?? 0;
            var remoteHost = string.IsNullOrWhiteSpace(config.Tunnel?.RemoteHost) ? config.Host : config.Tunnel.RemoteHost;
            var remotePort = config.Tunnel != null && config.Tunnel.RemotePort > 0 ? config.Tunnel.RemotePort : config.Port;
            var forward = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
            try
            {
                lastClient.AddForwardedPort(forward);
                forward.Start();
                lock (_sync)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(TunnelSession));
                    _forwardOwner = lastClient;
                    _forwardedPort = forward;
                    LocalPort = (int)forward.BoundPort;
                    LocalHost = "127.0.0.1";
                    _ready = true;
                }
            }
            catch { ReleaseForward(lastClient, forward); throw; }
        }

        public void SetError(string error)
        {
            lock (_sync) { LastError = error; _ready = false; }
        }

        public SshClient GetLastClient()
        {
            lock (_sync)
            {
                if (_hopClients.Count == 0) throw new InvalidOperationException(L10n.T("没有已连接的 hop"));
                return _hopClients[_hopClients.Count - 1];
            }
        }

        public void Dispose()
        {
            SshClient[] clients;
            KeyValuePair<SshClient, ForwardedPortLocal>[] forwards;
            ForwardedPortLocal lastForward;
            SshClient lastOwner;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _ready = false;
                clients = _hopClients.ToArray();
                forwards = _intermediateForwards.ToArray();
                lastForward = _forwardedPort;
                lastOwner = _forwardOwner;
                _hopClients.Clear();
                _intermediateForwards.Clear();
                _forwardedPort = null;
                _forwardOwner = null;
            }
            // Never hold the state lock while SSH.NET waits for callbacks/channels to finish.
            ReleaseForward(lastOwner, lastForward);
            for (int i = clients.Length - 1; i >= 0; i--)
            {
                try { clients[i].Dispose(); } catch { }
                if (i > 0 && i - 1 < forwards.Length)
                    ReleaseForward(forwards[i - 1].Key, forwards[i - 1].Value);
            }
        }

        private static void ReleaseForward(SshClient owner, ForwardedPortLocal forward)
        {
            if (forward == null) return;
            try { forward.Stop(); } catch { }
            try { owner?.RemoveForwardedPort(forward); } catch { }
            try { forward.Dispose(); } catch { }
        }
    }
}
