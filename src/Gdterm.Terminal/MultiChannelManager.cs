using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Gdterm.Terminal
{
    /// <summary>
    /// 多通道输入管理器——WindTerm 风格，支持将同一命令同时发送到多个终端会话
    /// 运维场景：批量执行命令、对比多台服务器输出
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
                if (_sessions.ContainsKey(sessionId))
                    throw new InvalidOperationException($"会话 {sessionId} 已注册");

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
        /// 选择/取消选择会话用于多通道输入
        /// </summary>
        public void ToggleSelection(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    session.IsSelected = !session.IsSelected;

                    if (session.IsSelected && !_activeGroupIds.Contains(sessionId))
                        _activeGroupIds.Add(sessionId);
                    else if (!session.IsSelected)
                        _activeGroupIds.Remove(sessionId);

                    IsBroadcastMode = _activeGroupIds.Count > 1;
                }
            }
        }

        /// <summary>
        /// 选择会话
        /// </summary>
        public void Select(string sessionId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    session.IsSelected = true;
                    if (!_activeGroupIds.Contains(sessionId))
                        _activeGroupIds.Add(sessionId);
                    IsBroadcastMode = _activeGroupIds.Count > 1;
                }
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
        /// 全选某分组
        /// </summary>
        public void SelectGroup(string group)
        {
            lock (_lock)
            {
                foreach (var kvp in _sessions)
                {
                    if (kvp.Value.Group == group)
                    {
                        kvp.Value.IsSelected = true;
                        if (!_activeGroupIds.Contains(kvp.Key))
                            _activeGroupIds.Add(kvp.Key);
                    }
                }
                IsBroadcastMode = _activeGroupIds.Count > 1;
            }
        }

        /// <summary>
        /// 全选所有会话
        /// </summary>
        public void SelectAll()
        {
            lock (_lock)
            {
                foreach (var kvp in _sessions)
                {
                    kvp.Value.IsSelected = true;
                    if (!_activeGroupIds.Contains(kvp.Key))
                        _activeGroupIds.Add(kvp.Key);
                }
                IsBroadcastMode = _activeGroupIds.Count > 1;
            }
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
        /// 将命令发送到所有选中的会话
        /// </summary>
        /// <param name="command">要发送的命令</param>
        /// <param name="sourceSessionId">来源会话 ID（不发送给自己）</param>
        public void BroadcastCommand(string command, string sourceSessionId = null)
        {
            if (string.IsNullOrEmpty(command)) return;

            List<ChannelSession> targets;

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
                }
                catch
                {
                    // 发送失败不影响其他会话
                }
            })).ToArray();

            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
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
                    LastCommandAt = s.LastCommandAt
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
