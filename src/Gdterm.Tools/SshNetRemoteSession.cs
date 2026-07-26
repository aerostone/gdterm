using System;
using System.Diagnostics;
using Gdterm.Tools.Models;
using Renci.SshNet;

namespace Gdterm.Tools
{
    /// <summary>
    /// SSH.NET 适配器——唯一允许持有 SshClient 的 Tools 实现类。
    /// </summary>
    public sealed class SshNetRemoteSession : ISshRemoteSession
    {
        private readonly SshClient _client;

        public SshNetRemoteSession(SshClient client)
        {
            _client = client;
        }

        /// <summary>从已有 SshClient 包装；client 为 null 时返回 null。</summary>
        internal static ISshRemoteSession Wrap(SshClient client)
        {
            return client == null ? null : new SshNetRemoteSession(client);
        }

        /// <summary>从 ITerminalSession.TryGetSshClient() 的 object 桥接（finding-11）。</summary>
        public static ISshRemoteSession Wrap(object client)
        {
            return Wrap(client as SshClient);
        }

        public bool IsConnected
        {
            get { return _client != null && _client.IsConnected; }
        }

        public RemoteCommandResult RunCommand(string command)
        {
            var sw = Stopwatch.StartNew();
            if (_client == null || !_client.IsConnected)
            {
                sw.Stop();
                return new RemoteCommandResult
                {
                    Command = command,
                    ExitCode = -1,
                    Stderr = "SSH 未连接",
                    Duration = sw.Elapsed
                };
            }

            try
            {
                var cmd = _client.RunCommand(command ?? "");
                sw.Stop();
                return new RemoteCommandResult
                {
                    Command = command,
                    // SSH.NET 2024: ExitStatus 为 int?
                    ExitCode = cmd.ExitStatus.HasValue ? cmd.ExitStatus.Value : -1,
                    Stdout = cmd.Result,
                    Stderr = cmd.Error,
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new RemoteCommandResult
                {
                    Command = command,
                    ExitCode = -1,
                    Stderr = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        public IRemoteFileTransfer CreateFileTransfer()
        {
            if (_client == null) throw new InvalidOperationException("SSH 客户端为空");
            return new SshRemoteFileTransfer(_client);
        }

        /// <summary>供仍需 SshClient 的遗留桥接（端口转发）。调用方不得 Dispose。</summary>
        internal SshClient UnderlyingClient { get { return _client; } }
    }
}
