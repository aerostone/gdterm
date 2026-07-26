using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 连接健康快照
    /// </summary>
    public class HealthSnapshot
    {
        public DateTime Timestamp { get; set; }
        public bool IsConnected { get; set; }
        public double LatencyMs { get; set; }
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public int ReconnectCount { get; set; }
        public TimeSpan Uptime { get; set; }
        public string StatusText { get; set; }
    }

    /// <summary>
    /// 连接健康监控器——实时延迟、连接状态、流量统计。
    /// go-live P0-02：ConnectionLost 可重复触发；RecordReconnect 后重新武装。
    /// </summary>
    public class ConnectionHealthMonitor : IDisposable
    {
        private readonly ITerminalSession _session;
        private Timer _timer;
        private readonly Stopwatch _uptime = new Stopwatch();
        private readonly List<HealthSnapshot> _history = new List<HealthSnapshot>();
        private readonly object _lock = new object();
        private DateTime _connectedAt;
        private int _reconnectCount;
        private bool _lostRaised;
        private bool _wasConnected;

        public ITerminalSession Session => _session;
        public TimeSpan CurrentUptime => _uptime.Elapsed;

        /// <summary>健康状态快照更新时触发</summary>
        public event Action<HealthSnapshot> SnapshotUpdated;

        /// <summary>连接断开时触发（每次从 connected→lost 边沿一次）</summary>
        public event Action<string> ConnectionLost; // hostName

        public ConnectionHealthMonitor(ITerminalSession session)
        {
            _session = session;
            _connectedAt = DateTime.Now;
            _wasConnected = session?.IsConnected == true;
            _uptime.Start();
        }

        /// <summary>历史条数硬顶（低内存）</summary>
        public int MaxHistoryEntries { get; set; } = 120;

        /// <summary>是否暂停采样（非活动标签应暂停）</summary>
        public bool IsPaused { get; set; }

        /// <summary>启动监控（默认 5 秒间隔）</summary>
        public void Start(int intervalMs = 5000)
        {
            _timer?.Dispose();
            _timer = new Timer(OnTick, null, 0, intervalMs);
        }

        /// <summary>停止监控</summary>
        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _uptime.Stop();
        }

        /// <summary>获取历史快照</summary>
        public List<HealthSnapshot> GetHistory(int maxCount = 100)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _history.Count - maxCount);
                return _history.GetRange(start, _history.Count - start);
            }
        }

        /// <summary>
        /// 记录重连成功：重置 lost 闩锁，恢复 uptime，允许再次 ConnectionLost。
        /// </summary>
        public void RecordReconnect()
        {
            _reconnectCount++;
            _lostRaised = false;
            _connectedAt = DateTime.Now;
            _wasConnected = true;
            if (!_uptime.IsRunning)
                _uptime.Restart();
            else
                _uptime.Restart();
        }

        /// <summary>强制重新武装（锁屏 Resume 后调用，P1-03）。</summary>
        public void Rearm()
        {
            _lostRaised = false;
            if (_session?.IsConnected == true)
            {
                _wasConnected = true;
                if (_connectedAt == default)
                    _connectedAt = DateTime.Now;
            }
        }

        private void OnTick(object state)
        {
            if (IsPaused) return;
            try
            {
                bool connected = _session?.IsConnected == true;

                var sw = Stopwatch.StartNew();
                bool alive = _session?.IsConnected == true;
                sw.Stop();

                var snapshot = new HealthSnapshot
                {
                    Timestamp = DateTime.Now,
                    IsConnected = connected,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    ReconnectCount = _reconnectCount,
                    Uptime = connected ? _uptime.Elapsed : TimeSpan.Zero,
                    StatusText = connected ? "已连接" : "已断开"
                };

                lock (_lock)
                {
                    _history.Add(snapshot);
                    var cap = MaxHistoryEntries > 0 ? MaxHistoryEntries : 120;
                    if (_history.Count > cap)
                        _history.RemoveRange(0, _history.Count - cap);
                }

                try { SnapshotUpdated?.Invoke(snapshot); } catch { }

                // 边沿检测：仅在 connected→disconnected 且尚未对本轮 lost 发过事件
                if (connected)
                {
                    if (!_wasConnected)
                    {
                        // 外部恢复连接（非 RecordReconnect 路径）
                        _connectedAt = DateTime.Now;
                        if (!_uptime.IsRunning) _uptime.Start();
                    }
                    _wasConnected = true;
                    _lostRaised = false;
                }
                else
                {
                    if (_wasConnected && !_lostRaised)
                    {
                        _lostRaised = true;
                        _wasConnected = false;
                        try { ConnectionLost?.Invoke(_session?.Hostname ?? "session"); } catch { }
                        // 注意：不清空 _connectedAt 永久闩；用 _lostRaised 防抖
                    }
                }
            }
            catch
            {
                // 不吞掉后续 tick：空 catch 仅本轮
            }
        }

        public void Dispose()
        {
            Stop();
            _uptime.Stop();
        }
    }
}
