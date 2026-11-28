using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Gdterm.Terminal
{
    public enum BatchCommandStatus { Succeeded, Failed, TimedOut, Cancelled }

    public class BatchCommandResult
    {
        public string SessionId { get; set; }
        public string HostName { get; set; }
        public string Command { get; set; }
        public List<string> Output { get; set; } = new List<string>();
        public string StandardError { get; set; }
        public int? ExitCode { get; set; }
        public BatchCommandStatus Status { get; set; } = BatchCommandStatus.Failed;
        public bool IsSuccess { get { return Status == BatchCommandStatus.Succeeded; } }
        public bool OutputTruncated { get; set; }
        public string ErrorMessage { get; set; }
        public double DurationMs { get; set; }
    }

    /// <summary>Shared SSH clients, independent exec channels. Never writes into the interactive shell.</summary>
    public class BatchCommandExecutor
    {
        public const int MaxOutputChars = 128 * 1024;
        public event Action<string, string> SessionProgress;
        public event Action<BatchCommandResult> SessionCompleted;
        public event Action<List<BatchCommandResult>> AllCompleted;

        public async Task<List<BatchCommandResult>> ExecuteAsync(
            Dictionary<string, ITerminalSession> sessions, string command,
            int timeoutMs = 30000, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException(nameof(command));
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            // Freeze object references before the first await. Reconnect must never redirect a queued command.
            var targets = sessions.ToArray();
            using (var slots = new SemaphoreSlim(8))
            {
                var tasks = targets.Select(target => Task.Run(async () =>
                {
                    var result = new BatchCommandResult {
                        SessionId = target.Key, HostName = target.Value?.Hostname ?? target.Key, Command = command
                    };
                    var elapsed = Stopwatch.StartNew();
                    bool entered = false;
                    try
                    {
                        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                        entered = true;
                        cancellationToken.ThrowIfCancellationRequested();
                        var session = target.Value;
                        var client = session?.TryGetSshClient() as SshClient;
                        if (session == null || !session.IsConnected || client == null || !client.IsConnected)
                            throw new InvalidOperationException(L10n.T("需要已连接的 SSH 会话"));
                        SessionProgress?.Invoke(target.Key, L10n.T("执行中..."));
                        using (var scoped = client.CreateCommand(command.Replace("\r\n", "\n").Replace('\r', '\n')))
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            scoped.CommandTimeout = TimeSpan.FromMilliseconds(timeoutMs);
                            deadline.CancelAfter(timeoutMs);
                            try
                            {
                                await scoped.ExecuteAsync(deadline.Token).ConfigureAwait(false);
                                result.ExitCode = scoped.ExitStatus;
                                result.Status = scoped.ExitStatus == 0 ? BatchCommandStatus.Succeeded : BatchCommandStatus.Failed;
                                if (!result.IsSuccess)
                                    result.ErrorMessage = scoped.ExitStatus.HasValue
                                        ? L10n.Tf("命令退出码：{0}", scoped.ExitStatus.Value)
                                        : L10n.T("服务器未返回退出码");
                            }
                            catch (OperationCanceledException)
                            {
                                result.Status = cancellationToken.IsCancellationRequested
                                    ? BatchCommandStatus.Cancelled : BatchCommandStatus.TimedOut;
                            }
                            catch (SshOperationTimeoutException) { result.Status = BatchCommandStatus.TimedOut; }
                            finally
                            {
                                var stdout = Limit(scoped.Result, result);
                                result.Output = stdout.Replace("\r\n", "\n").Split('\n').ToList();
                                result.StandardError = Limit(scoped.Error, result);
                            }
                        }
                    }
                    catch (OperationCanceledException) { result.Status = BatchCommandStatus.Cancelled; }
                    catch (Exception ex) { result.ErrorMessage = ex.Message; }
                    finally
                    {
                        result.DurationMs = elapsed.Elapsed.TotalMilliseconds;
                        if (entered) slots.Release();
                    }
                    if (result.Status == BatchCommandStatus.Cancelled)
                        result.ErrorMessage = L10n.T("已取消；远端进程是否停止取决于服务器");
                    if (result.Status == BatchCommandStatus.TimedOut)
                        result.ErrorMessage = L10n.T("执行超时；远端进程是否停止取决于服务器");
                    SessionCompleted?.Invoke(result);
                    return result;
                })).ToArray();
                var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
                AllCompleted?.Invoke(results);
                return results;
            }
        }

        private static string Limit(string value, BatchCommandResult result)
        {
            value = value ?? "";
            if (value.Length <= MaxOutputChars) return value;
            result.OutputTruncated = true;
            return value.Substring(0, MaxOutputChars);
        }
    }
}
