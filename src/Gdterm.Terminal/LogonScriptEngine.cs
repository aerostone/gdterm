using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 登录脚本执行引擎——延时发送 + 关键词等待（expect/send 模式）
    /// </summary>
    public class LogonScriptEngine : IDisposable
    {
        private CancellationTokenSource _cts;
        private bool _running;

        public bool IsRunning => _running;

        /// <summary>脚本执行进度</summary>
        public event Action<int, int, string> StepProgress; // current, total, description

        /// <summary>脚本完成</summary>
        public event Action<bool, string> Completed; // success, message

        /// <summary>执行登录脚本</summary>
        public async Task ExecuteAsync(LogonScript script, ITerminalSession session)
        {
            if (script == null || session == null || !session.IsConnected) return;
            if (_running) return;

            _running = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                var steps = script.Steps ?? new List<LogonStep>();
                string outputBuffer = "";

                // 订阅输出用于 Wait 匹配
                Action<string> outputHandler = null;
                if (steps.Exists(s => s.Type == LogonStepType.Wait))
                {
                    outputHandler = line => { lock (this) { outputBuffer += line + "\n"; } };
                    session.OutputReceived += outputHandler;
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var step = steps[i];
                    StepProgress?.Invoke(i + 1, steps.Count, step.Description ?? step.Value ?? step.Type.ToString());

                    switch (step.Type)
                    {
                        case LogonStepType.Send:
                            session.SendInput(step.Value + "\r");
                            await Task.Delay(500, token); // 发送后短暂等待
                            break;

                        case LogonStepType.Wait:
                            await WaitForPattern(session, step.Value, step.TimeoutMs, token);
                            lock (this) { outputBuffer = ""; } // 清空缓冲
                            break;

                        case LogonStepType.Delay:
                            await Task.Delay(Math.Max(100, step.TimeoutMs), token);
                            break;
                    }
                }

                // 取消订阅
                if (outputHandler != null)
                    session.OutputReceived -= outputHandler;

                Completed?.Invoke(true, "登录脚本执行完成");
            }
            catch (OperationCanceledException)
            {
                Completed?.Invoke(false, "脚本已取消");
            }
            catch (Exception ex)
            {
                Completed?.Invoke(false, "脚本执行失败: " + ex.Message);
            }
            finally
            {
                _running = false;
            }
        }

        private async Task WaitForPattern(ITerminalSession session, string pattern, int timeoutMs, CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var tcs = new TaskCompletionSource<bool>();

            Action<string> handler = null;
            handler = line =>
            {
                if (line != null && line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    tcs.TrySetResult(true);
            };

            session.OutputReceived += handler;
            try
            {
                var timeoutTask = Task.Delay(timeoutMs, token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completed != tcs.Task)
                {
                    // 超时，继续执行（不阻塞）
                }
            }
            finally
            {
                session.OutputReceived -= handler;
            }
        }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
