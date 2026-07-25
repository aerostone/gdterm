using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 批量命令执行结果
    /// </summary>
    public class BatchCommandResult
    {
        public string SessionId { get; set; }
        public string HostName { get; set; }
        public string Command { get; set; }
        public List<string> Output { get; set; } = new List<string>();
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; }
        public double DurationMs { get; set; }
    }

    /// <summary>
    /// 批量命令执行器——选中多个会话同时执行同一命令，结果对比
    /// </summary>
    public class BatchCommandExecutor
    {
        /// <summary>单个会话执行进度</summary>
        public event Action<string, string> SessionProgress; // sessionId, status

        /// <summary>全部执行完成</summary>
        public event Action<List<BatchCommandResult>> AllCompleted;

        /// <summary>对多个会话执行同一命令</summary>
        public async Task<List<BatchCommandResult>> ExecuteAsync(
            Dictionary<string, ITerminalSession> sessions,
            string command,
            int timeoutMs = 30000)
        {
            var results = new List<BatchCommandResult>();
            var tasks = new List<Task<BatchCommandResult>>();

            foreach (var kvp in sessions)
            {
                tasks.Add(ExecuteOnSession(kvp.Key, kvp.Value, command, timeoutMs));
            }

            var completed = await Task.WhenAll(tasks);
            results.AddRange(completed);
            AllCompleted?.Invoke(results);
            return results;
        }

        private async Task<BatchCommandResult> ExecuteOnSession(string sessionId, ITerminalSession session, string command, int timeoutMs)
        {
            var result = new BatchCommandResult
            {
                SessionId = sessionId,
                HostName = sessionId,
                Command = command
            };

            try
            {
                if (session == null || !session.IsConnected)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = "会话未连接";
                    SessionProgress?.Invoke(sessionId, "跳过");
                    return result;
                }

                SessionProgress?.Invoke(sessionId, "执行中...");

                var output = new List<string>();
                var tcs = new TaskCompletionSource<bool>();

                Action<string> handler = null;
                handler = line =>
                {
                    output.Add(line);
                    // 检测命令结束（简单策略：等待 prompt 返回）
                    if (output.Count > 2 && string.IsNullOrEmpty(line))
                        tcs.TrySetResult(true);
                };

                session.OutputReceived += handler;
                session.SendInput(command + "\r");

                // 等待超时或完成
                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                session.OutputReceived -= handler;

                result.Output = output;
                result.IsSuccess = completedTask == tcs.Task;
                if (!result.IsSuccess) result.ErrorMessage = "执行超时";
                result.DurationMs = timeoutMs;

                SessionProgress?.Invoke(sessionId, result.IsSuccess ? "完成" : "超时");
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorMessage = ex.Message;
                SessionProgress?.Invoke(sessionId, "失败");
            }

            return result;
        }
    }
}
