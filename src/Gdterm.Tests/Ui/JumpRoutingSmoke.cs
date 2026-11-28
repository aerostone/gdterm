using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.Rdp;
using Gdterm.Rdp.Models;
using Gdterm.Sftp;
using Gdterm.Sftp.Models;
using Gdterm.Tunnel;
using Gdterm.Tunnel.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Services;

namespace Gdterm.Tests.Ui
{
    internal static class JumpRoutingSmoke
    {
        internal static void Run(Action<bool, string> check)
        {
            foreach (var mode in new[] { "direct", "jump-only", "tunnel" })
            {
                var cfg = new ConnectionConfig { Id = mode, Host = "target.test", Port = 22,
                    JumpChain = mode == "jump-only" ? new JumpChainConfig { Hops = new List<JumpHop> { new JumpHop { Host = "hop.test", CredentialRefId = "hop" } } } : null,
                    Tunnel = mode == "tunnel" ? new TunnelConfig() : null };
                bool tunneled = mode != "direct";
                var manager = new TunnelFake();
                var sftp = new SftpFake();
                using (var host = new Form())
                using (var panel = new SftpDualPanePanel(cfg, new CredentialPayload(), sftp, manager))
                {
                    host.Controls.Add(panel); host.Show();
                    Application.DoEvents();
                    check(sftp.Connected && sftp.ViaTunnel == tunneled && manager.Calls == (tunneled ? 1 : 0), "SFTP real UI entry routes " + mode);
                    check(!tunneled || ReferenceEquals(manager.Endpoint, sftp.Endpoint), "SFTP receives manager endpoint " + mode);
                }
                manager = new TunnelFake();
                using (var rdp = new RdpFake())
                {
                    cfg.Protocol = ProtocolType.RDP; cfg.Port = 3389;
                    cfg.Metadata = new Dictionary<string, string> { { "rdp_engine", "freerdp" } };
                    var opener = new ProtocolTabOpener(manager, null, rdp, null, null, null, null, null);
                    var opened = opener.CreateRdp(cfg, new CredentialPayload());
                    using (opened.Page)
                    {
                        opened.Session.PendingConnect();
                        check(rdp.IsConnected && rdp.ViaTunnel == tunneled && manager.Calls == (tunneled ? 1 : 0), "RDP real pending connect routes " + mode);
                        check(!tunneled || ReferenceEquals(manager.Endpoint, rdp.Endpoint), "RDP receives manager endpoint " + mode);
                    }
                }
            }
        }
        private sealed class TunnelFake : ITunnelManager
        {
            internal int Calls;
            internal TunnelEndpoint Endpoint = new TunnelEndpoint { LocalHost = "127.0.0.1", LocalPort = 43210 };
            public Task<TunnelEndpoint> EstablishAsync(ConnectionConfig c, CredentialPayload p, CancellationToken t) { Calls++; return Task.FromResult(Endpoint); }
            public Task CloseAsync(string id) => Task.CompletedTask;
            public TunnelStatus GetStatus(string id) => new TunnelStatus { IsActive = true };
            public void Dispose() { }
        }
        private sealed class RdpFake : IRdpClient, IRdpClientFactory
        {
            internal bool ViaTunnel;
            internal TunnelEndpoint Endpoint;
            public IRdpClient Create() => this;
            public IRdpClient CreateFor(ConnectionConfig c) => this;
            public void Connect(ConnectionConfig c, CredentialPayload p, RdpOptions o = null) { IsConnected = true; }
            public void ConnectViaTunnel(ConnectionConfig c, CredentialPayload p, TunnelEndpoint ep, RdpOptions o = null) { Endpoint = ep; ViaTunnel = IsConnected = true; }
            public void Disconnect() { IsConnected = false; }
            public bool IsConnected { get; private set; }
            public UserControl Control { get; } = new UserControl();
            public RdpOptions CurrentOptions => null;
            public event EventHandler<RdpStateChangedEventArgs> StateChanged { add { } remove { } }
            public event EventHandler<FileTransferEventArgs> FileTransferred { add { } remove { } }
            public void Dispose() { Control.Dispose(); }
        }
        private sealed class SftpFake : ISftpService, ISftpServiceFactory
        {
            internal bool Connected, ViaTunnel;
            internal TunnelEndpoint Endpoint;
            public ISftpService Create() => this;
            public Task ConnectAsync(ConnectionConfig c, CredentialPayload p, CancellationToken t) { Connected = true; return Task.CompletedTask; }
            public Task ConnectViaTunnelAsync(ConnectionConfig c, CredentialPayload p, TunnelEndpoint ep, CancellationToken t) { Endpoint = ep; ViaTunnel = Connected = true; return Task.CompletedTask; }
            public Task<IList<SftpFileInfo>> ListDirectoryAsync(string p, CancellationToken t) => Task.FromResult<IList<SftpFileInfo>>(new List<SftpFileInfo>());
            public Task<SftpFileInfo> GetEntryAsync(string p, CancellationToken t) => Task.FromResult<SftpFileInfo>(null);
            public Task<byte[]> ReadFilePrefixAsync(string p, int max, CancellationToken t) => Task.FromResult(new byte[0]);
            public Task UploadAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t) => throw new NotSupportedException();
            public Task DownloadAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t) => throw new NotSupportedException();
            public Task<bool> UploadResumeAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t) => throw new NotSupportedException();
            public Task<bool> DownloadResumeAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t) => throw new NotSupportedException();
            public Task DeleteAsync(string p, bool r, CancellationToken t) => throw new NotSupportedException();
            public Task CreateDirectoryAsync(string p, CancellationToken t) => throw new NotSupportedException();
            public Task RenameAsync(string a, string b, CancellationToken t) => throw new NotSupportedException();
            public Task ChmodAsync(string p, int m, CancellationToken t) => throw new NotSupportedException();
            public void Disconnect() { Connected = false; }
            public bool IsConnected => Connected;
            public void Dispose() { }
        }
    }
}
