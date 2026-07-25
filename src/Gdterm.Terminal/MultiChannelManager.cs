using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 多通道输入管理器——WindTerm 风格，支持将同一命令同时发送到多个终端会话
    /// 运维场景：批量执行命令、对比多台服务器输出
    /// 
    /// 安全策略：只有终端处于就绪状态（命令提示符 $、#、> 等）的会话才允许加入
    /// 正在执行 top/vim/编译/交互式程序的终端会被拒绝
    /// </summary>
    public class MultiChannelManager : IDisposable
    {
        private readonly Dictionary<string, ChannelSession> _sessions = new Dictionary<string, ChannelSession>();
        private readonly List<string> _activeGroupIds = new List<string>();
        private readonly object _lock = new object();
        private bool _isBroadcastMode;

        /// <summary>
        /// 会话注册事件
        /// </summary>
        public event EventHandler<ChannelSessionEventArgs> SessionRegistered;

        /// <summary>
        /// 会话注销事件
        /// </summary>
        public event EventHandler<ChannelSessionEventArgs> SessionUnregistered;

        /// <summary>
        /// 广播状态变化事件
        /// </summary>
        public event EventHandler<BroadcastStateChangedEventArgs> BroadcastStateChanged;

        /// <summary>
        /// 命令发送事件（用于命令日志记录）
        /// </summary>
        public event EventHandler<CommandSentEventArgs> CommandSent;

        /// <summary>
        /// 是否处于广播模式
        /// </summary>
        public bool IsBroadcastMode
        {
            get => _isBroadcastMode;
            private set
            {
                if (_isBroadcastMode != value)
                {
                    _isBroadcastMode = value;
                    BroadcastStateChanged?.Invoke(this, new BroadcastStateChangedEventArgs(value));
                }
            }
        }

        /// <summary>
        /// 注册终端会话到多通道管理
        /// </summary>
        /// <param name="sessionId">会话唯一标识（通常是标签页 ID）</param>
        /// <param name="session">终端会话实例</param>
        /// <param name="displayName">显示名称（主机名等）</param>
        /// <param name="group">分组名（可选，用于批量操作）</param>
        public void Register(string sessionId, ITerminalSession session, string displayName, string group = null)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentNullException(nameof(sessionId));
            if (session == null) throw new ArgumentNullException(nameof(session));

            lock (_lock)
            {
                // 幂等：重连/刷新时更新会话引用，避免 InvalidOperationException
                if (_sessions.TryGetValue(sessionId, out var existing))
                {
                    existing.Session = session;
                    existing.DisplayName = displayName ?? existing.DisplayName ?? sessionId;
                    if (!string.IsNullOrEmpty(group))
                        existing.Group = group;
                    return;
                }

                _sessions[sessionId] = new ChannelSession
                {
                    SessionId = sessionId,
                    Session = session,
                    DisplayName = displayName ?? sessionId,
                    Group = group ?? "默认",
                    IsSelected = false,
                    RegisteredAt = DateTime.UtcNow
                };

                SessionRegistered?.Invoke(this, new ChannelSessionEventArgs(sessionId, displayName, group));
            }
        }

        /// <summary>
        /// 注销终端会话
        /// </summary>
        public void Unregister(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.Remove(sessionId))
                {
                    _activeGroupIds.Remove(sessionId);
                    SessionUnregistered?.Invoke(this, new ChannelSessionEventArgs(sessionId, null, null));
                }
            }
        }

        /// <summary>
        /// 选择/取消选择会话用于多通道输入（选择时检测终端就绪状态）
        /// </summary>
        public SelectResult ToggleSelection(string sessionId)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var session))
                    return new SelectResult(false, "会话不存在");

                // 如果已选中，取消选择
                if (session.IsSelected)
                {
                    session.IsSelected = false;
                    _activeGroupIds.Remove(sessionId);
                    IsBroadcastMode = _activeGroupIds.Count > 1;
                    return new SelectResult(true, "已移出多通道");
                }

                // 未选中，尝试选择——检测终端就绪
                if (!session.Session.IsConnected)
                    return new SelectResult(false, "会话未连接");

                var readyState = CheckTerminalReady(session.Session);
                session.LastReadyState = readyState;

                if (!readyState.IsReady)
                    return new SelectResult(false, readyState.Description);

                session.IsSelected = true;
                if (!_activeGroupIds.Contains(sessionId))
                    _activeGroupIds.Add(sessionId);
                IsBroadcastMode = _activeGroupIds.Count > 1;

                return new SelectResult(true, "已加入多通道");
            }
        }

        /// <summary>
        /// 选择会话——只有终端就绪（命令提示符状态）的会话才允许加入
        /// </summary>
        public SelectResult Select(string sessionId)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(sessionId, out var session))
                    return new SelectResult(false, "会话不存在");

                if (!session.Session.IsConnected)
                    return new SelectResult(false, "会话未连接");

                // 检测终端是否就绪
                var readyState = CheckTerminalReady(session.Session);
                session.LastReadyState = readyState;

                if (!readyState.IsReady)
                    return new SelectResult(false, readyState.Description);

                session.IsSelected = true;
                if (!_activeGroupIds.Contains(sessionId))
                    _activeGroupIds.Add(sessionId);
                IsBroadcastMode = _activeGroupIds.Count > 1;

                return new SelectResult(true, "已加入多通道");
            }
        }

        /// <summary>
        /// 取消选择会话
        /// </summary>
        public void Deselect(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    session.IsSelected = false;
                    _activeGroupIds.Remove(sessionId);
                    IsBroadcastMode = _activeGroupIds.Count > 1;
                }
            }
        }

        /// <summary>
        /// 全选某分组（只选择终端就绪的会话）
        /// </summary>
        public List<SelectResult> SelectGroup(string group)
        {
            var results = new List<SelectResult>();
            lock (_lock)
            {
                foreach (var kvp in _sessions)
                {
                    if (kvp.Value.Group != group) continue;

                    if (!kvp.Value.Session.IsConnected)
                    {
                        results.Add(new SelectResult(false, $"{kvp.Value.DisplayName}: 未连接"));
                        continue;
                    }

                    var readyState = CheckTerminalReady(kvp.Value.Session);
                    kvp.Value.LastReadyState = readyState;

                    if (!readyState.IsReady)
                    {
                        results.Add(new SelectResult(false, $"{kvp.Value.DisplayName}: {readyState.Description}"));
                        continue;
                    }

                    kvp.Value.IsSelected = true;
                    if (!_activeGroupIds.Contains(kvp.Key))
                        _activeGroupIds.Add(kvp.Key);
                    results.Add(new SelectResult(true, $"{kvp.Value.DisplayName}: 已加入"));
                }
                IsBroadcastMode = _activeGroupIds.Count > 1;
            }
            return results;
        }

        /// <summary>
        /// 全选所有就绪会话
        /// </summary>
        public List<SelectResult> SelectAll()
        {
            var results = new List<SelectResult>();
            lock (_lock)
            {
                foreach (var kvp in _sessions)
                {
                    if (!kvp.Value.Session.IsConnected)
                    {
                        results.Add(new SelectResult(false, $"{kvp.Value.DisplayName}: 未连接"));
                        continue;
                    }

                    var readyState = CheckTerminalReady(kvp.Value.Session);
                    kvp.Value.LastReadyState = readyState;

                    if (!readyState.IsReady)
                    {
                        results.Add(new SelectResult(false, $"{kvp.Value.DisplayName}: {readyState.Description}"));
                        continue;
                    }

                    kvp.Value.IsSelected = true;
                    if (!_activeGroupIds.Contains(kvp.Key))
                        _activeGroupIds.Add(kvp.Key);
                    results.Add(new SelectResult(true, $"{kvp.Value.DisplayName}: 已加入"));
                }
                IsBroadcastMode = _activeGroupIds.Count > 1;
            }
            return results;
        }

        /// <summary>
        /// 取消所有选择
        /// </summary>
        public void DeselectAll()
        {
            lock (_lock)
            {
                foreach (var kvp in _sessions)
                    kvp.Value.IsSelected = false;
                _activeGroupIds.Clear();
                IsBroadcastMode = false;
            }
        }

        /// <summary>
        /// 广播前自动移除已非就绪的会话（如用户在某终端启动了 top）
        /// </summary>
        private void AutoRemoveNonReadySessions()
        {
            var toRemove = new List<string>();
            foreach (var id in _activeGroupIds)
            {
                if (_sessions.TryGetValue(id, out var session))
                {
                    var readyState = CheckTerminalReady(session.Session);
                    session.LastReadyState = readyState;
                    if (!readyState.IsReady)
                        toRemove.Add(id);
                }
            }
            foreach (var id in toRemove)
            {
                if (_sessions.TryGetValue(id, out var session))
                    session.IsSelected = false;
                _activeGroupIds.Remove(id);
            }
        }

        /// <summary>
        /// 将命令发送到所有选中的会话（广播前自动剔除非就绪终端）
        /// </summary>
        /// <param name="command">要发送的命令</param>
        /// <param name="sourceSessionId">来源会话 ID（不发送给自己）</param>
        public List<BroadcastResult> BroadcastCommand(string command, string sourceSessionId = null)
        {
            if (string.IsNullOrEmpty(command)) return new List<BroadcastResult>();

            // 广播前再次检测：移除已变为非就绪的会话
            AutoRemoveNonReadySessions();

            List<ChannelSession> targets;
            var results = new List<BroadcastResult>();

            lock (_lock)
            {
                targets = _activeGroupIds
                    .Where(id => id != sourceSessionId && _sessions.ContainsKey(id))
                    .Select(id => _sessions[id])
                    .Where(s => s.Session.IsConnected)
                    .ToList();
            }

            // 记录命令发送事件
            CommandSent?.Invoke(this, new CommandSentEventArgs
            {
                Command = command,
                SourceSessionId = sourceSessionId,
                TargetSessionIds = targets.Select(t => t.SessionId).ToList(),
                Timestamp = DateTime.UtcNow,
                IsBroadcast = targets.Count > 1
            });

            // 并行发送到所有目标会话
            var tasks = targets.Select(target => Task.Run(() =>
            {
                try
                {
                    target.Session.SendInput(command);
                    target.LastCommandAt = DateTime.UtcNow;
                    target.CommandCount++;
                    lock (results) { results.Add(new BroadcastResult(target.SessionId, target.DisplayName, true, null)); }
                }
                catch (Exception ex)
                {
                    lock (results) { results.Add(new BroadcastResult(target.SessionId, target.DisplayName, false, ex.Message)); }
                }
            })).ToArray();

            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
            return results;
        }

        /// <summary>
        /// 检查指定会话的终端就绪状态
        /// </summary>
        public ReadyState CheckSessionReady(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var session) && session.Session.IsConnected)
                {
                    var state = CheckTerminalReady(session.Session);
                    session.LastReadyState = state;
                    return state;
                }
                return new ReadyState(false, "会话不存在或未连接", ReadyReason.NoOutput);
            }
        }

        /// <summary>
        /// 获取所有注册的会话
        /// </summary>
        public IReadOnlyList<ChannelSessionInfo> GetAllSessions()
        {
            lock (_lock)
            {
                return _sessions.Values.Select(s => new ChannelSessionInfo
                {
                    SessionId = s.SessionId,
                    DisplayName = s.DisplayName,
                    Group = s.Group,
                    IsSelected = s.IsSelected,
                    IsConnected = s.Session?.IsConnected ?? false,
                    CommandCount = s.CommandCount,
                    LastCommandAt = s.LastCommandAt,
                    ReadyState = s.LastReadyState
                }).ToList();
            }
        }

        /// <summary>
        /// 获取所有分组名
        /// </summary>
        public IReadOnlyList<string> GetGroups()
        {
            lock (_lock)
            {
                return _sessions.Values
                    .Select(s => s.Group)
                    .Distinct()
                    .OrderBy(g => g)
                    .ToList();
            }
        }

        /// <summary>
        /// 获取选中的会话数
        /// </summary>
        public int SelectedCount
        {
            get
            {
                lock (_lock) { return _activeGroupIds.Count; }
            }
        }

        /// <summary>
        /// 检测终端就绪状态
        /// </summary>
        private static ReadyState CheckTerminalReady(ITerminalSession session)
        {
            try
            {
                var recentOutput = session.GetRecentOutput(5);
                return TerminalReadyDetector.Detect(recentOutput);
            }
            catch
            {
                return new ReadyState(false, "无法获取终端状态", ReadyReason.NoOutput);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _sessions.Clear();
                _activeGroupIds.Clear();
                IsBroadcastMode = false;
            }
        }

        /// <summary>
        /// 内部会话信息
        /// </summary>
        private class ChannelSession
        {
            public string SessionId { get; set; }
            public ITerminalSession Session { get; set; }
            public string DisplayName { get; set; }
            public string Group { get; set; }
            public bool IsSelected { get; set; }
            public DateTime RegisteredAt { get; set; }
            public DateTime? LastCommandAt { get; set; }
            public int CommandCount { get; set; }
            public ReadyState LastReadyState { get; set; }
        }
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
