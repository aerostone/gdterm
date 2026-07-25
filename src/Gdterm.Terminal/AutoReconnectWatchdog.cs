using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 断线自动重连看门狗——检测连接断开后按指数退避策略自动重连
    /// 
    /// 特性：
    ///   - 连接断开时自动触发重连
    ///   - 指数退避（1s → 2s → 4s → 8s → 16s → 30s 上限）
    ///   - 最大重试次数可配置（默认 5 次）
    ///   - 手动断开不触发重连
    ///   - Reconnecting/Reconnected/ReconnectFailed 事件
    /// </summary>
    public class AutoReconnectWatchdog : IDisposable
    {
        private readonly Dictionary<string, WatchedSession> _sessions = new Dictionary<string, WatchedSession>();
        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>
        /// 最大重试次数（0 = 无限重试）
        /// </summary>
        public int MaxRetries { get; set; } = 5;

        /// <summary>
        /// 基础重连间隔（毫秒），指数退避以此为基础
        /// </summary>
        public int BaseIntervalMs { get; set; } = 1000;

        /// <summary>
        /// 最大重连间隔（毫秒）
        /// </summary>
        public int MaxIntervalMs { get; set; } = 30000;

        /// <summary>
        /// 开始重连事件
        /// </summary>
        public event EventHandler<ReconnectEventArgs> Reconnecting;

        /// <summary>
        /// 重连成功事件
        /// </summary>
        public event EventHandler<ReconnectEventArgs> Reconnected;

        /// <summary>
        /// 重连失败事件（达到最大重试次数）
        /// </summary>
        public event EventHandler<ReconnectEventArgs> ReconnectFailed;

        /// <summary>
        /// 监视一个会话的连接状态
        /// </summary>
        /// <summary>
        /// 外部重连委托：返回 true 表示重连成功。由 UI 负责 close+reopen。
        /// </summary>
        public Func<string, ITerminalSession, Task<bool>> DefaultReconnectFunc { get; set; }

        public void Watch(string sessionId, ITerminalSession session, Func<string, ITerminalSession, Task<bool>> reconnectFunc = null)
        {
            if (_disposed || string.IsNullOrEmpty(sessionId) || session == null) return;

            lock (_lock)
            {
                if (_sessions.ContainsKey(sessionId))
                    Unwatch(sessionId);

                var watched = new WatchedSession
                {
                    SessionId = sessionId,
                    Session = session,
                    ManualDisconnect = false,
                    ReconnectFunc = reconnectFunc ?? DefaultReconnectFunc
                };

                // 注意: 实际断开检测通过 ConnectionHealthMonitor.NotifyConnectionLost 或外部通知

                _sessions[sessionId] = watched;
            }
        }

        /// <summary>
        /// 停止监视一个会话
        /// </summary>
        public void Unwatch(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var watched))
                {
                    watched.ManualDisconnect = true;
                    watched.Cts?.Cancel();
                    _sessions.Remove(sessionId);
                }
            }
        }

        /// <summary>
        /// 手动标记连接丢失（由外部调用，如 ConnectionHealthMonitor 检测到断线）
        /// </summary>
        public void NotifyConnectionLost(string sessionId)
        {
            OnConnectionLost(sessionId);
        }

        /// <summary>
        /// 手动标记连接已恢复（停止重连计时器）
        /// </summary>
        public void NotifyConnectionRestored(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var watched))
                {
                    watched.Cts?.Cancel();
                    watched.RetryCount = 0;
                }
            }
        }

        /// <summary>
        /// 获取指定会话的重连状态
        /// </summary>
        public ReconnectState GetState(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var watched))
                {
                    return new ReconnectState
                    {
                        SessionId = sessionId,
                        IsReconnecting = watched.IsReconnecting,
                        RetryCount = watched.RetryCount,
                        MaxRetries = MaxRetries,
                        NextRetryAt = watched.NextRetryAt
                    };
                }
                return new ReconnectState { SessionId = sessionId };
            }
        }

        private void OnConnectionLost(string sessionId)
        {
            WatchedSession watched;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out watched) || watched.ManualDisconnect)
                    return;

                if (watched.IsReconnecting) return; // 已在重连中
                watched.IsReconnecting = true;
                watched.RetryCount = 0;
                watched.Cts?.Cancel();
                watched.Cts = new CancellationTokenSource();
            }

            // 启动后台重连任务
            Task.Run(() => ReconnectLoop(watched));
        }

        private async Task ReconnectLoop(WatchedSession watched)
        {
            var ct = watched.Cts.Token;

            while (!ct.IsCancellationRequested && !_disposed)
            {
                watched.RetryCount++;

                if (MaxRetries > 0 && watched.RetryCount > MaxRetries)
                {
                    // 达到最大重试次数
                    watched.IsReconnecting = false;
                    ReconnectFailed?.Invoke(this, new ReconnectEventArgs
                    {
                        SessionId = watched.SessionId,
                        RetryCount = watched.RetryCount - 1,
                        MaxRetries = MaxRetries,
                        ErrorMessage = watched.LastError
                    });
                    return;
                }

                // 计算指数退避间隔
                var delay = Math.Min(BaseIntervalMs * (1 << (watched.RetryCount - 1)), MaxIntervalMs);
                watched.NextRetryAt = DateTime.UtcNow.AddMilliseconds(delay);

                Reconnecting?.Invoke(this, new ReconnectEventArgs
                {
                    SessionId = watched.SessionId,
                    RetryCount = watched.RetryCount,
                    MaxRetries = MaxRetries,
                    NextRetryDelayMs = delay
                });

                try
                {
                    await Task.Delay(delay, ct);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (ct.IsCancellationRequested) return;

                // 尝试重连：通过外部委托（UI 层负责真正的 close+reopen）
                try
                {
                    bool ok = false;
                    if (watched.ReconnectFunc != null)
                    {
                        ok = await watched.ReconnectFunc(watched.SessionId, watched.Session);
                    }
                    else if (watched.Session != null && watched.Session.IsConnected)
                    {
                        // 会话已恢复
                        ok = true;
                    }

                    if (ok)
                    {
                        watched.IsReconnecting = false;
                        watched.RetryCount = 0;
                        Reconnected?.Invoke(this, new ReconnectEventArgs
                        {
                            SessionId = watched.SessionId,
                            RetryCount = watched.RetryCount
                        });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // 记录本轮失败原因，继续退避；达 MaxRetries 时再发 ReconnectFailed
                    watched.LastError = ex.Message;
                    Reconnecting?.Invoke(this, new ReconnectEventArgs
                    {
                        SessionId = watched.SessionId,
                        RetryCount = watched.RetryCount,
                        MaxRetries = MaxRetries,
                        ErrorMessage = ex.Message
                    });
                }
            }
        }

        private bool IsDisconnectMessage(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            var lower = output.ToLowerInvariant();
            return lower.Contains("connection closed") ||
                   lower.Contains("connection reset") ||
                   lower.Contains("connection refused") ||
                   lower.Contains("broken pipe") ||
                   lower.Contains("session timeout") ||
                   lower.Contains("eof") ||
                   lower.Contains("disconnected");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                foreach (var kv in _sessions)
                {
                    kv.Value.ManualDisconnect = true;
                    kv.Value.Cts?.Cancel();
                    kv.Value.Cts?.Dispose();
                }
                _sessions.Clear();
            }
        }

        private class WatchedSession
        {
            public string SessionId { get; set; }
            public ITerminalSession Session { get; set; }
            public bool ManualDisconnect { get; set; }
            public bool IsReconnecting { get; set; }
            public int RetryCount { get; set; }
            public DateTime NextRetryAt { get; set; }
            public CancellationTokenSource Cts { get; set; }
            public Func<string, ITerminalSession, Task<bool>> ReconnectFunc { get; set; }
            public string LastError { get; set; }
        }
    }

    /// <summary>
    /// 重连事件参数
    /// </summary>
    public class ReconnectEventArgs : EventArgs
    {
        public string SessionId { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; }
        public int NextRetryDelayMs { get; set; }
        /// <summary>最近一次失败原因（可选）</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 重连状态
    /// </summary>
    public class ReconnectState
    {
        public string SessionId { get; set; }
        public bool IsReconnecting { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; }
        public DateTime NextRetryAt { get; set; }
    }
}
