using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Tunnel.Models
{
    /// <summary>
    /// 一个活跃隧道会话——管理该 connectionId 下所有 hop 的 SshClient 和端口转发
    /// </summary>
    internal class TunnelSession : IDisposable
    {
        private readonly List<SshClient> _hopClients = new List<SshClient>();
        private ForwardedPortLocal _forwardedPort;
        private bool _disposed;

        public string ConnectionId { get; }
        public DateTime EstablishedAt { get; }
        public bool IsActive { get; private set; }
        public string LastError { get; private set; }
        public int LocalPort { get; private set; }
        public string LocalHost { get; private set; }

        public TunnelSession(string connectionId)
        {
            ConnectionId = connectionId;
            EstablishedAt = DateTime.UtcNow;
            IsActive = false;
        }

        /// <summary>
        /// 连接到一个 hop，建立 SshClient（go-live P1-10：支持 hop 私钥）。
        /// </summary>
        public void ConnectHop(JumpHop hop, CredentialPayload credential, SshClient throughClient = null)
        {
            var user = hop.Username ?? "root";
            var password = CredentialPayload.ResolveHopPassword(hop, credential) ?? "";
            var privateKey = CredentialPayload.ResolveHopPrivateKey(hop, credential);
            var passphrase = CredentialPayload.ResolveHopPrivateKeyPassphrase(hop, credential);

            string host;
            int port;
            ForwardedPortLocal tempForward = null;

            if (throughClient != null)
            {
                var tempPort = GetAvailablePort();
                tempForward = new ForwardedPortLocal("127.0.0.1", (uint)tempPort, hop.Host, (uint)hop.Port);
                throughClient.AddForwardedPort(tempForward);
                tempForward.Start();
                host = "127.0.0.1";
                port = tempPort;
            }
            else
            {
                host = hop.Host;
                port = hop.Port;
            }

            ConnectionInfo connInfo;
            if (privateKey != null && privateKey.Length > 0)
            {
                var ms = new System.IO.MemoryStream(privateKey, writable: false);
                PrivateKeyFile keyFile;
                try
                {
                    keyFile = string.IsNullOrEmpty(passphrase)
                        ? new PrivateKeyFile(ms)
                        : new PrivateKeyFile(ms, passphrase);
                }
                finally
                {
                    ms.Dispose();
                }
                connInfo = new PrivateKeyConnectionInfo(host, port, user, keyFile);
            }
            else
            {
                connInfo = new PasswordConnectionInfo(host, port, user, password);
            }

            connInfo.Timeout = TimeSpan.FromSeconds(30);
            var client = new SshClient(connInfo);
            try
            {
                client.Connect();
            }
            finally
            {
                if (tempForward != null)
                {
                    try
                    {
                        tempForward.Stop();
                        throughClient.RemoveForwardedPort(tempForward);
                        tempForward.Dispose();
                    }
                    catch { /* best-effort */ }
                }
            }

            _hopClients.Add(client);
        }

        /// <summary>兼容旧签名：仅密码。</summary>
        public void ConnectHop(JumpHop hop, string password, SshClient throughClient = null)
        {
            var payload = new CredentialPayload { Password = password };
            if (hop != null && !string.IsNullOrEmpty(hop.CredentialRefId))
            {
                payload.HopPasswordsByRefId = new System.Collections.Generic.Dictionary<string, string>
                {
                    { hop.CredentialRefId, password ?? "" }
                };
            }
            ConnectHop(hop, payload, throughClient);
        }

        /// <summary>
        /// 直连模式：连接到目标主机（无跳板链）；私钥优先
        /// </summary>
        public void ConnectDirect(ConnectionConfig config, CredentialPayload credential)
        {
            var user = credential?.Username ?? config.Username ?? "root";
            ConnectionInfo connInfo;

            if (credential?.SshPrivateKey != null && credential.SshPrivateKey.Length > 0)
            {
                var ms = new System.IO.MemoryStream(credential.SshPrivateKey, writable: false);
                PrivateKeyFile keyFile;
                try
                {
                    keyFile = string.IsNullOrEmpty(credential.SshPrivateKeyPassphrase)
                        ? new PrivateKeyFile(ms)
                        : new PrivateKeyFile(ms, credential.SshPrivateKeyPassphrase);
                }
                finally
                {
                    ms.Dispose();
                }
                connInfo = new PrivateKeyConnectionInfo(config.Host, config.Port, user, keyFile);
            }
            else
            {
                connInfo = new PasswordConnectionInfo(
                    config.Host, config.Port, user, credential?.Password ?? "");
            }

            connInfo.Timeout = TimeSpan.FromSeconds(30);
            var client = new SshClient(connInfo);
            client.Connect();
            _hopClients.Add(client);
        }

        /// <summary>
        /// 在最后一跳上建立端口转发
        /// </summary>
        public void StartPortForwarding(ConnectionConfig config)
        {
            var lastClient = _hopClients[_hopClients.Count - 1];
            var tunnelConfig = config.Tunnel;
            var localPort = tunnelConfig?.LocalPort ?? 0;
            var remoteHost = tunnelConfig?.RemoteHost ?? config.Host;
            var remotePort = tunnelConfig?.RemotePort ?? config.Port;

            if (localPort == 0)
            {
                localPort = GetAvailablePort();
            }

            var forwardedPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
            lastClient.AddForwardedPort(forwardedPort);
            forwardedPort.Start();

            _forwardedPort = forwardedPort;
            LocalPort = localPort;
            LocalHost = "127.0.0.1";
            IsActive = true;
        }

        public void SetError(string error)
        {
            LastError = error;
            IsActive = false;
        }

        /// <summary>
        /// 获取最后添加的 SshClient（用于跳板链中获取上一跳的 client）
        /// </summary>
        public SshClient GetLastClient()
        {
            if (_hopClients.Count == 0)
                throw new InvalidOperationException("没有已连接的 hop");
            return _hopClients[_hopClients.Count - 1];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_forwardedPort != null)
                {
                    _forwardedPort.Stop();
                    var client = _forwardedPort.Session as SshClient;
                    client?.RemoveForwardedPort(_forwardedPort);
                    _forwardedPort.Dispose();
                }
            }
            catch { /* best-effort cleanup */ }

            // 从后往前释放所有 hop client
            for (int i = _hopClients.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_hopClients[i].IsConnected)
                        _hopClients[i].Disconnect();
                    _hopClients[i].Dispose();
                }
                catch { /* best-effort cleanup */ }
            }

            _hopClients.Clear();
            IsActive = false;
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
