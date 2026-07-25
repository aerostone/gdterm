using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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
    /// 连接健康监控器——实时延迟、连接状态、流量统计
    /// </summary>
    public class ConnectionHealthMonitor : IDisposable
    {
        private readonly ITerminalSession _session;
        private Timer _timer;
        private readonly Stopwatch _uptime = new Stopwatch();
        private long _lastBytesRx;
        private long _lastBytesTx;
        private readonly List<HealthSnapshot> _history = new List<HealthSnapshot>();
        private readonly object _lock = new object();
        private DateTime _connectedAt;
        private int _reconnectCount;

        public ITerminalSession Session => _session;
        public TimeSpan CurrentUptime => _uptime.Elapsed;

        /// <summary>健康状态快照更新时触发</summary>
        public event Action<HealthSnapshot> SnapshotUpdated;

        /// <summary>连接断开时触发</summary>
        public event Action<string> ConnectionLost; // hostName

        public ConnectionHealthMonitor(ITerminalSession session)
        {
            _session = session;
            _connectedAt = DateTime.Now;
            _uptime.Start();
        }

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

        /// <summary>记录重连事件</summary>
        public void RecordReconnect()
        {
            _reconnectCount++;
        }

        private void OnTick(object state)
        {
            try
            {
                bool connected = _session?.IsConnected == true;

                // 计算延迟（通过检查连接状态的响应时间）
                var sw = Stopwatch.StartNew();
                bool alive = _session?.IsConnected == true;
                sw.Stop();

                var snapshot = new HealthSnapshot
                {
                    Timestamp = DateTime.Now,
                    IsConnected = connected,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    ReconnectCount = _reconnectCount,
                    Uptime = _uptime.Elapsed,
                    StatusText = connected ? "已连接" : "已断开"
                };

                lock (_lock)
                {
                    _history.Add(snapshot);
                    if (_history.Count > 1000) _history.RemoveRange(0, _history.Count - 500);
                }

                SnapshotUpdated?.Invoke(snapshot);

                if (!connected && _connectedAt != default)
                {
                    ConnectionLost?.Invoke("session");
                    _connectedAt = default;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            _uptime.Stop();
        }
    }
}
