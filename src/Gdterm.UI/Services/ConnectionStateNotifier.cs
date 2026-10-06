using System;
using System.Collections.Generic;
using Gdterm.Terminal;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 连接状态观测扇出（A1）：订阅自动重连看门狗三事件 → DiagLog 轮次记录 + 状态栏连接段 + Toast。
    /// Toast 策略：每次断连只在首轮提示一次（_outageToasted 闩），恢复/放弃各一次；每轮次不弹。
    /// 状态段策略：文本按 sessionId 记账，只对活动会话落地；切换标签时 RefreshStatusBar 重放。
    /// 线程：看门狗事件在后台线程触发——ToastNotifier 自带 UI marshal，状态段回调由 BottomBarPanel marshal，DiagLog 任意线程安全。
    /// </summary>
    public sealed class ConnectionStateNotifier : IDisposable
    {
        private readonly AutoReconnectWatchdog _watchdog;
        private readonly Func<string, string> _resolveName;
        private readonly Func<string, bool> _isActiveSession;
        private readonly Action<string> _setStatusBar;
        private readonly HashSet<string> _outageToasted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();
        private bool _disposed;

        public ConnectionStateNotifier(
            AutoReconnectWatchdog watchdog,
            Func<string, string> resolveName,
            Func<string, bool> isActiveSession,
            Action<string> setStatusBar)
        {
            _watchdog = watchdog;
            _resolveName = resolveName ?? (id => id);
            _isActiveSession = isActiveSession ?? (id => false);
            _setStatusBar = setStatusBar;
            if (_watchdog == null) return;
            _watchdog.Reconnecting += OnRetrying;
            _watchdog.Reconnected += OnRecovered;
            _watchdog.ReconnectFailed += OnFailed;
        }

        private string Name(string sessionId)
        {
            try { return _resolveName(sessionId) ?? sessionId ?? ""; }
            catch { return sessionId ?? ""; }
        }

        private void Apply(string sessionId, string text)
        {
            lock (_sync) _lastText[sessionId ?? ""] = text;
            if (_isActiveSession(sessionId))
            {
                try { _setStatusBar?.Invoke(text); }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ConnectionStateNotifier", exSwallowed); } catch { } }
            }
        }

        private void OnRetrying(object sender, ReconnectEventArgs e)
        {
            if (_disposed || e == null) return;
            try
            {
                var name = Name(e.SessionId);
                var err = string.IsNullOrEmpty(e.ErrorMessage) ? "" : " err=" + e.ErrorMessage;
                DiagLog.Info("ConnectionState", "retrying session=" + e.SessionId +
                    " " + e.RetryCount + "/" + e.MaxRetries + " in " + e.NextRetryDelayMs + "ms" + err);
                Apply(e.SessionId, "重连中 " + e.RetryCount + "/" + e.MaxRetries + " · " + name);
                bool first;
                lock (_sync) first = _outageToasted.Add(e.SessionId ?? "");
                if (first)
                    ToastNotifier.Show("连接断开，正在重连: " + name, ToastNotifier.Level.Warning);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ConnectionStateNotifier", exSwallowed); } catch { } }
        }

        private void OnRecovered(object sender, ReconnectEventArgs e)
        {
            if (_disposed || e == null) return;
            try
            {
                lock (_sync) _outageToasted.Remove(e.SessionId ?? "");
                var name = Name(e.SessionId);
                DiagLog.Info("ConnectionState", "recovered session=" + e.SessionId + " name=" + name);
                Apply(e.SessionId, "");
                ToastNotifier.Show("连接已恢复: " + name, ToastNotifier.Level.Success);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ConnectionStateNotifier", exSwallowed); } catch { } }
        }

        private void OnFailed(object sender, ReconnectEventArgs e)
        {
            if (_disposed || e == null) return;
            try
            {
                lock (_sync) _outageToasted.Remove(e.SessionId ?? "");
                var name = Name(e.SessionId);
                DiagLog.Info("ConnectionState", "give up session=" + e.SessionId +
                    " attempts=" + e.RetryCount + " err=" + (e.ErrorMessage ?? ""));
                Apply(e.SessionId, "重连失败 · " + name);
                ToastNotifier.Show("重连失败: " + name + "（可手动重连）", ToastNotifier.Level.Error);
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ConnectionStateNotifier", exSwallowed); } catch { } }
        }

        /// <summary>
        /// 活动标签切换：重放新活动会话的连接状态（无残留则复位为就绪）。
        /// </summary>
        public void RefreshStatusBar()
        {
            if (_disposed || _setStatusBar == null) return;
            string replay = null;
            lock (_sync)
            {
                foreach (var kv in _lastText)
                {
                    if (_isActiveSession(kv.Key)) { replay = kv.Value; break; }
                }
            }
            try { _setStatusBar(replay ?? ""); }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ConnectionStateNotifier", exSwallowed); } catch { } }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_watchdog != null)
            {
                try { _watchdog.Reconnecting -= OnRetrying; } catch { }
                try { _watchdog.Reconnected -= OnRecovered; } catch { }
                try { _watchdog.ReconnectFailed -= OnFailed; } catch { }
            }
        }
    }
}
