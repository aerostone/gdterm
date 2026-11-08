using System;
using System.Collections.Generic;
using Gdterm.Core;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 回收站（FEATURE-MAP §4「撤销/回收站」加分项）：页面删除条目先入这里，
    /// 「工具 → 回收站」页可恢复 / 删除 / 清空。
    ///
    /// 范围裁决（v1 = 会话级，应用重启后清空）：各域 store 的模型没有统一序列化契约
    /// （JSON 逐 store 手写，payload 若跨重启持久要逐域手写序列化，风险收益比差）。
    /// 回收站页面空态提示已写明「重启后清空」——不做假承诺。数据结构给持久化留了口子
    /// （Payload 为 object，将来接 JSON 只需页面侧补序列化）。
    ///
    /// 恢复分发：删除方页面在构造时为自己用的分类注册 restorer（闭包该页的 store/列表）。
    /// TryRestore 弹出条目交给 restorer；restorer 异常或条目已失效 → 条目放回回收站并报错。
    /// 条目上限 300（FIFO 淘汰最旧），防止回收站本身变垃圾堆。
    /// </summary>
    public sealed class TrashStore
    {
        public sealed class Entry
        {
            public string Id { get; set; }
            /// <summary>机器可读分类（restorer 分发键），如 "bookmark"。</summary>
            public string Category { get; set; }
            /// <summary>展示名，如「书签」。</summary>
            public string CategoryLabel { get; set; }
            /// <summary>条目描述（列表第二列），如书签名。</summary>
            public string Description { get; set; }
            /// <summary>被删原件（对象引用；restorer 取回后放回原 store）。</summary>
            public object Payload { get; set; }
            public DateTime DeletedAtUtc { get; set; }
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly object _lock = new object();
        private readonly Dictionary<string, Action<Entry>> _restorers =
            new Dictionary<string, Action<Entry>>();
        private static int _idCounter;

        private const int MaxEntries = 300;

        /// <summary>应用内唯一回收站（所有页面共用一个）。</summary>
        public static readonly TrashStore Default = new TrashStore();

        /// <summary>新条目入站（回收站页面据此刷新）。</summary>
        public event EventHandler<Entry> ItemAdded;

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>删除条目入站。payload 为空不入（无原件可恢复的删除不占回收站）。</summary>
        public void Add(string category, string categoryLabel, string description, object payload)
        {
            if (string.IsNullOrEmpty(category) || payload == null) return;
            var entry = new Entry
            {
                Id = "tr-" + (++_idCounter).ToString("x8"),
                Category = category,
                CategoryLabel = string.IsNullOrEmpty(categoryLabel) ? category : categoryLabel,
                Description = description ?? string.Empty,
                Payload = payload,
                DeletedAtUtc = DateTime.UtcNow
            };
            lock (_lock)
            {
                _entries.Insert(0, entry);
                while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
            }
            var h = ItemAdded;
            if (h != null) h(this, entry);
        }

        /// <summary>按时间倒序的条目快照（UI 渲染用，不暴露内部列表）。</summary>
        public IList<Entry> List()
        {
            lock (_lock) return new List<Entry>(_entries);
        }

        /// <summary>
        /// 恢复：弹出条目 → 交给该分类的 restorer。失败（无 restorer / 抛异常）条目放回。
        /// </summary>
        public bool TryRestore(string id, out string error)
        {
            Entry entry;
            lock (_lock)
            {
                entry = _entries.Find(x => x.Id == id);
                if (entry != null) _entries.Remove(entry);
            }
            if (entry == null)
            {
                error = L10n.T("该条目已不在回收站");
                return false;
            }

            Action<Entry> handler;
            lock (_lock)
            {
                _restorers.TryGetValue(entry.Category, out handler);
            }
            if (handler == null)
            {
                lock (_lock) _entries.Insert(0, entry);
                error = L10n.T("该类型没有可用的恢复通道");
                return false;
            }

            try
            {
                handler(entry);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                lock (_lock) _entries.Insert(0, entry);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>彻底丢弃（不进恢复流程）。</summary>
        public void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock) _entries.RemoveAll(x => x.Id == id);
        }

        public void RemoveMany(IEnumerable<string> ids)
        {
            if (ids == null) return;
            var set = new HashSet<string>();
            foreach (var i in ids) if (!string.IsNullOrEmpty(i)) set.Add(i);
            if (set.Count == 0) return;
            lock (_lock) _entries.RemoveAll(x => set.Contains(x.Id));
        }

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }

        /// <summary>页面在构造时注册自己分类的恢复器（幂等：后注册覆盖）。</summary>
        public void RegisterRestorer(string category, Action<Entry> handler)
        {
            if (string.IsNullOrEmpty(category) || handler == null) return;
            lock (_lock) _restorers[category] = handler;
        }
    }
}
