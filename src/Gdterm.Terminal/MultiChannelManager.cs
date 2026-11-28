using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core;

namespace Gdterm.Terminal
{
    public sealed class BroadcastTarget
    {
        public string SessionId { get; private set; }
        public string DisplayName { get; private set; }
        public ITerminalSession Session { get; private set; }
        internal BroadcastTarget(string id, string name, ITerminalSession session)
        { SessionId = id; DisplayName = name; Session = session; }
    }

    /// <summary>Registry and explicit selections. Snapshot references never follow a reconnect.</summary>
    public class MultiChannelManager : IDisposable
    {
        private sealed class Channel
        {
            public ITerminalSession Session;
            public string Name, Group;
            public bool Selected;
            public int Count;
            public DateTime? LastCommand;
        }
        private readonly Dictionary<string, Channel> _sessions = new Dictionary<string, Channel>();
        private readonly object _lock = new object();
        public event EventHandler<ChannelSessionEventArgs> SessionRegistered;
        public event EventHandler<ChannelSessionEventArgs> SessionUnregistered;
        public event EventHandler<BroadcastStateChangedEventArgs> BroadcastStateChanged;
        public event EventHandler<CommandSentEventArgs> CommandSent;
        public bool IsBroadcastMode { get { return SelectedCount > 0; } }

        public void Register(string sessionId, ITerminalSession session, string displayName, string group = null)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));
            if (session == null) throw new ArgumentNullException(nameof(session));
            bool changed;
            lock (_lock)
            {
                Channel current;
                changed = !_sessions.TryGetValue(sessionId, out current) ||
                    !ReferenceEquals(current.Session, session) || current.Name != displayName;
                if (current == null || !ReferenceEquals(current.Session, session))
                    _sessions[sessionId] = current = new Channel { Session = session };
                current.Name = displayName ?? sessionId;
                current.Group = group ?? L10n.T("默认");
            }
            if (changed) SessionRegistered?.Invoke(this, new ChannelSessionEventArgs(sessionId, displayName, group));
        }

        public void Unregister(string sessionId)
        {
            bool removed;
            lock (_lock) removed = _sessions.Remove(sessionId);
            if (removed)
            {
                SessionUnregistered?.Invoke(this, new ChannelSessionEventArgs(sessionId, null, null));
                NotifySelection();
            }
        }

        public SelectResult Select(string sessionId)
        {
            SelectResult result;
            lock (_lock)
            {
                Channel current;
                if (!_sessions.TryGetValue(sessionId, out current)) return new SelectResult(false, L10n.T("会话不存在"));
                var state = CheckTerminalReady(current.Session);
                current.Selected = state.IsReady;
                result = new SelectResult(state.IsReady, state.IsReady ? L10n.T("已加入多通道") : state.Description);
            }
            NotifySelection();
            return result;
        }

        public SelectResult ToggleSelection(string sessionId)
        {
            bool selected;
            lock (_lock) { Channel c; selected = _sessions.TryGetValue(sessionId, out c) && c.Selected; }
            if (!selected) return Select(sessionId);
            Deselect(sessionId);
            return new SelectResult(true, L10n.T("已移出多通道"));
        }

        public void Deselect(string sessionId)
        {
            lock (_lock) { Channel c; if (_sessions.TryGetValue(sessionId, out c)) c.Selected = false; }
            NotifySelection();
        }
        public List<SelectResult> SelectAll() { return SelectIds(GetAllSessions().Select(s => s.SessionId)); }
        public List<SelectResult> SelectGroup(string group)
        { return SelectIds(GetAllSessions().Where(s => s.Group == group).Select(s => s.SessionId)); }
        private List<SelectResult> SelectIds(IEnumerable<string> ids) { return ids.Select(Select).ToList(); }
        public void DeselectAll()
        {
            lock (_lock) foreach (var c in _sessions.Values) c.Selected = false;
            NotifySelection();
        }

        public IReadOnlyList<BroadcastTarget> GetSelectedTargets()
        {
            lock (_lock) return _sessions.Where(kv => kv.Value.Selected)
                .Select(kv => new BroadcastTarget(kv.Key, kv.Value.Name, kv.Value.Session)).ToList();
        }

        public async Task<List<BroadcastResult>> BroadcastCommandAsync(string command,
            IReadOnlyList<BroadcastTarget> targets, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(command) || targets == null) return new List<BroadcastResult>();
            // Submit one normalized command, retaining embedded newlines and control characters.
            var payload = command.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Replace('\n', '\r') + "\r";
            var tasks = targets.Select(target => Task.Run(() =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    lock (_lock)
                    {
                        Channel current;
                        if (!_sessions.TryGetValue(target.SessionId, out current) ||
                            !ReferenceEquals(current.Session, target.Session))
                            return new BroadcastResult(target.SessionId, target.DisplayName, false, L10n.T("会话已更换，请重新选择"));
                    }
                    var ready = CheckTerminalReady(target.Session);
                    if (!ready.IsReady) return new BroadcastResult(target.SessionId, target.DisplayName, false, ready.Description);
                    ct.ThrowIfCancellationRequested();
                    target.Session.SendInput(payload);
                    lock (_lock)
                    {
                        Channel current;
                        if (_sessions.TryGetValue(target.SessionId, out current) && ReferenceEquals(current.Session, target.Session))
                        { current.Count++; current.LastCommand = DateTime.UtcNow; }
                    }
                    return new BroadcastResult(target.SessionId, target.DisplayName, true, null);
                }
                catch (OperationCanceledException)
                { return new BroadcastResult(target.SessionId, target.DisplayName, false, L10n.T("已取消")); }
                catch (Exception ex)
                { return new BroadcastResult(target.SessionId, target.DisplayName, false, ex.Message); }
            })).ToArray();
            var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
            var sent = results.Where(r => r.Success).Select(r => r.SessionId).ToList();
            if (sent.Count > 0) CommandSent?.Invoke(this, new CommandSentEventArgs {
                Command = payload, TargetSessionIds = sent, Timestamp = DateTime.UtcNow, IsBroadcast = true
            });
            return results;
        }

        public ReadyState CheckSessionReady(string sessionId) { return CheckTerminalReady(GetSession(sessionId)); }
        private static ReadyState CheckTerminalReady(ITerminalSession session)
        {
            try
            {
                if (session == null || !session.IsConnected) return new ReadyState(false, L10n.T("会话未连接"), ReadyReason.NoOutput);
                if (session.IsZmodemReceiving) return new ReadyState(false, L10n.T("终端正在传输文件"), ReadyReason.BusyProgram);
                return TerminalReadyDetector.Detect(session.GetRecentOutput(5));
            }
            catch { return new ReadyState(false, L10n.T("无法获取终端状态"), ReadyReason.NoOutput); }
        }

        public IReadOnlyList<ChannelSessionInfo> GetAllSessions()
        {
            lock (_lock) return _sessions.Select(kv => new ChannelSessionInfo {
                SessionId = kv.Key, DisplayName = kv.Value.Name, Group = kv.Value.Group,
                IsConnected = kv.Value.Session.IsConnected, IsSelected = kv.Value.Selected,
                CommandCount = kv.Value.Count, LastCommandAt = kv.Value.LastCommand,
                ReadyState = CheckTerminalReady(kv.Value.Session)
            }).ToList();
        }
        public ITerminalSession GetSession(string id)
        { lock (_lock) { Channel c; return _sessions.TryGetValue(id, out c) ? c.Session : null; } }
        public IReadOnlyList<string> GetGroups() { return GetAllSessions().Select(s => s.Group).Distinct().OrderBy(s => s).ToList(); }
        public int SelectedCount { get { lock (_lock) return _sessions.Values.Count(s => s.Selected); } }
        private void NotifySelection() { BroadcastStateChanged?.Invoke(this, new BroadcastStateChangedEventArgs(IsBroadcastMode)); }
        public void Dispose() { lock (_lock) _sessions.Clear(); NotifySelection(); }
    }

    /// <summary>
    /// 选择操作结果
    /// </summary>
    public class SelectResult
    {
        public bool Success { get; }
        public string Message { get; }

        public SelectResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }

    /// <summary>
    /// 广播操作结果
    /// </summary>
    public class BroadcastResult
    {
        public string SessionId { get; }
        public string DisplayName { get; }
        public bool Success { get; }
        public string Error { get; }

        public BroadcastResult(string sessionId, string displayName, bool success, string error)
        {
            SessionId = sessionId;
            DisplayName = displayName;
            Success = success;
            Error = error;
        }
    }

    /// <summary>
    /// 会话信息（对外暴露）
    /// </summary>
    public class ChannelSessionInfo
    {
        public string SessionId { get; set; }
        public string DisplayName { get; set; }
        public string Group { get; set; }
        public bool IsSelected { get; set; }
        public bool IsConnected { get; set; }
        public int CommandCount { get; set; }
        public DateTime? LastCommandAt { get; set; }
        public ReadyState ReadyState { get; set; }
    }

    /// <summary>
    /// 会话事件参数
    /// </summary>
    public class ChannelSessionEventArgs : EventArgs
    {
        public string SessionId { get; }
        public string DisplayName { get; }
        public string Group { get; }

        public ChannelSessionEventArgs(string sessionId, string displayName, string group)
        {
            SessionId = sessionId;
            DisplayName = displayName;
            Group = group;
        }
    }

    /// <summary>
    /// 广播状态变化事件参数
    /// </summary>
    public class BroadcastStateChangedEventArgs : EventArgs
    {
        public bool IsBroadcasting { get; }

        public BroadcastStateChangedEventArgs(bool isBroadcasting)
        {
            IsBroadcasting = isBroadcasting;
        }
    }

    /// <summary>
    /// 命令发送事件参数（用于命令日志记录）
    /// </summary>
    public class CommandSentEventArgs : EventArgs
    {
        public string Command { get; set; }
        public string SourceSessionId { get; set; }
        public List<string> TargetSessionIds { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsBroadcast { get; set; }
    }
}
