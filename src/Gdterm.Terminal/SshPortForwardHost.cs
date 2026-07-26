using System;
using Gdterm.Tunnel;
using Renci.SshNet;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 将 TerminalSession 的底层 SshClient 适配为 ISshPortForwardHost。
    /// </summary>
    public sealed class SshPortForwardHost : ISshPortForwardHost
    {
        private readonly SshClient _client;

        public SshPortForwardHost(SshClient client)
        {
            _client = client;
        }

        internal static ISshPortForwardHost Wrap(SshClient client)
        {
            return client == null ? null : new SshPortForwardHost(client);
        }

        /// <summary>从 ITerminalSession.TryGetSshClient() 的 object 桥接（finding-11）。</summary>
        public static ISshPortForwardHost Wrap(object client)
        {
            return Wrap(client as SshClient);
        }

        public bool IsConnected
        {
            get { return _client != null && _client.IsConnected; }
        }

        public object GetUnderlyingClient()
        {
            return _client;
        }
    }
}
