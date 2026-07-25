using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Terminal.Models;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 登录脚本执行引擎——延时发送 + 关键词等待（expect/send 模式）
    /// </summary>
    public class LogonScriptEngine : IDisposable
    {
        private CancellationTokenSource _cts;
        private bool _running;
        private EventHandler<TerminalOutputEventArgs> _waitHandler;

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

                for (int i = 0; i < steps.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var step = steps[i];
                    StepProgress?.Invoke(i + 1, steps.Count, step.Description ?? step.Value ?? step.Type.ToString());

                    switch (step.Type)
                    {
                        case LogonStepType.Send:
                            session.SendInput((step.Value ?? "") + "\r");
                            await Task.Delay(500, token);
                            break;

                        case LogonStepType.Wait:
                            await WaitForPattern(session, step.Value, step.TimeoutMs > 0 ? step.TimeoutMs : 15000, token);
                            break;

                        case LogonStepType.Delay:
                            await Task.Delay(Math.Max(100, step.TimeoutMs), token);
                            break;
                    }
                }

                Completed?.Invoke(!token.IsCancellationRequested, token.IsCancellationRequested ? "脚本已取消" : "登录脚本执行完成");
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
                if (_waitHandler != null && session != null)
                {
                    try { session.OutputReceived -= _waitHandler; } catch { }
                    _waitHandler = null;
                }
                _running = false;
            }
        }

        private async Task WaitForPattern(ITerminalSession session, string pattern, int timeoutMs, CancellationToken token)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                await Task.Delay(100, token);
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            EventHandler<TerminalOutputEventArgs> handler = null;
            handler = (s, e) =>
            {
                var line = e != null ? e.Text : null;
                if (line != null && line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    tcs.TrySetResult(true);
            };

            _waitHandler = handler;
            session.OutputReceived += handler;
            try
            {
                var timeoutTask = Task.Delay(Math.Max(100, timeoutMs), token);
                await Task.WhenAny(tcs.Task, timeoutTask);
            }
            finally
            {
                session.OutputReceived -= handler;
                if (ReferenceEquals(_waitHandler, handler))
                    _waitHandler = null;
            }
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
        }

        public void Dispose()
        {
            Cancel();
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }
}
