using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 命令面板使用频率持久化（data/config/palette-usage.json）——F10。
    /// 行式存储（与 CommandHistoryStore 的 JSON Lines 同思路，比嵌套 JSON 更抗损坏）：
    ///   每行 <key>\t<n>\t<t>；key = Label + \u001f + Category（控制符分隔，正常菜单文本不可能含 \t）。
    ///   n = 累计使用次数，t = 最近使用 Unix 毫秒。; 开头为注释行。
    /// 文件不存在/损坏 → 空统计（面板退回名称排序），绝不抛异常：
    /// 频率统计是锦上添花，面板关闭路径不能因它失败。
    /// </summary>
    public sealed class PaletteUsageStore
    {
        private readonly string _filePath;
        private readonly Dictionary<string, Usage> _items = new Dictionary<string, Usage>(StringComparer.Ordinal);
        private static readonly object _fileLock = new object();

        private sealed class Usage
        {
            public int N;
            public long T;
        }

        public PaletteUsageStore() : this(DefaultPath()) { }

        public PaletteUsageStore(string filePath)
        {
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Load();
        }

        private static string DefaultPath()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config");
            return Path.Combine(dir, "palette-usage.json");
        }

        /// <summary>记录一次使用（n+1、t=当前时间）并立即落盘。吞异常——统计失败不影响面板。</summary>
        public void Record(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                Usage u;
                if (!_items.TryGetValue(key, out u)) { u = new Usage(); _items[key] = u; }
                u.N += 1;
                u.T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Save();
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("PaletteUsageStore.Record", ex); } catch { }
            }
        }

        /// <summary>取排序分：(累计次数, 最近使用时间)；无记录 = (0, 0)。</summary>
        public void Score(string key, out int count, out long lastUsed)
        {
            count = 0;
            lastUsed = 0;
            if (key == null) return;
            Usage u;
            if (_items.TryGetValue(key, out u))
            {
                count = u.N;
                lastUsed = u.T;
            }
        }

        private void Load()
        {
            _items.Clear();
            if (!File.Exists(_filePath)) return;
            string[] lines;
            try { lines = File.ReadAllLines(_filePath); }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("PaletteUsageStore.Load", ex); } catch { }
                return;
            }
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                var p = line.Split('\t');
                if (p.Length < 3) continue;
                int n;
                long t;
                if (!int.TryParse(p[1], out n) || n < 0) continue;
                if (!long.TryParse(p[2], out t)) continue;
                _items[p[0]] = new Usage { N = n, T = t };
            }
        }

        private void Save()
        {
            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.Append("; 命令面板使用频率统计（自动生成，勿手改）  key\tn\t最近使用ms\n");
                foreach (var kv in _items)
                    sb.Append(kv.Key).Append('\t').Append(kv.Value.N).Append('\t').Append(kv.Value.T).Append('\n');
                File.WriteAllText(_filePath, sb.ToString(), new UTF8Encoding(false));
            }
        }
    }
}
