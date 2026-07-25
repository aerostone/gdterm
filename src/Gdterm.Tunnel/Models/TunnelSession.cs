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
        /// 连接到一个 hop，建立 SshClient
        /// </summary>
        public void ConnectHop(JumpHop hop, string password, SshClient throughClient = null)
        {
            SshClient client;

            if (throughClient != null)
            {
                // 通过前一个 hop 的端口转发连接
                // 先在 throughClient 上建立一个临时端口转发到 hop.Host:hop.Port
                var tempPort = GetAvailablePort();
                var tempForward = new ForwardedPortLocal("127.0.0.1", (uint)tempPort, hop.Host, (uint)hop.Port);
                throughClient.AddForwardedPort(tempForward);
                tempForward.Start();

                var connInfo = new PasswordConnectionInfo("127.0.0.1", tempPort, hop.Username ?? "root", password ?? "");
                connInfo.Timeout = TimeSpan.FromSeconds(30);
                client = new SshClient(connInfo);
                client.Connect();

                // 移除临时转发（连接已建立，不再需要）
                tempForward.Stop();
                throughClient.RemoveForwardedPort(tempForward);
            }
            else
            {
                // 直连
                var connInfo = new PasswordConnectionInfo(hop.Host, hop.Port, hop.Username ?? "root", password ?? "");
                connInfo.Timeout = TimeSpan.FromSeconds(30);
                client = new SshClient(connInfo);
                client.Connect();
            }

            _hopClients.Add(client);
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
