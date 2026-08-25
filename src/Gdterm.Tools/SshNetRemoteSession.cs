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
            return RunCommand(command, 0);
        }

        public RemoteCommandResult RunCommand(string command, int timeoutSeconds)
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
                if (timeoutSeconds <= 0)
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

                // 带超时执行：SSH.NET 同步 Execute 不响应 CommandTimeout，
                // 用 BeginExecute + WaitOne 超时后 CancelAsync 中断。
                var scoped = _client.CreateCommand(command ?? "");
                try
                {
                    var asyncResult = scoped.BeginExecute();
                    if (!asyncResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(timeoutSeconds)))
                    {
                        try { scoped.CancelAsync(); } catch { /* 中断失败不掩盖超时本身 */ }
                        sw.Stop();
                        return new RemoteCommandResult
                        {
                            Command = command,
                            ExitCode = -1,
                            Stderr = "远端命令超时（" + timeoutSeconds + " 秒），已中断",
                            Duration = sw.Elapsed
                        };
                    }
                    scoped.EndExecute(asyncResult);
                    sw.Stop();
                    return new RemoteCommandResult
                    {
                        Command = command,
                        ExitCode = scoped.ExitStatus.HasValue ? scoped.ExitStatus.Value : -1,
                        Stdout = scoped.Result,
                        Stderr = scoped.Error,
                        Duration = sw.Elapsed
                    };
                }
                finally
                {
                    try { scoped.Dispose(); } catch { /* 释放失败不影响返回 */ }
                }
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
