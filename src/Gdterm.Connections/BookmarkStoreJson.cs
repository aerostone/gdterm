using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// JSON 文件存储的会话书签实现
    /// </summary>
    public class BookmarkStoreJson : IBookmarkStore
    {
        private readonly string _bookmarksPath;
        private readonly string _recentPath;
        private readonly object _lock = new object();

        private const int MaxRecentConnections = 50;

        public BookmarkStoreJson(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory))
                throw new ArgumentNullException(nameof(dataDirectory));

            _bookmarksPath = Path.Combine(dataDirectory, "bookmarks.json");
            _recentPath = Path.Combine(dataDirectory, "recent-connections.json");

            // 确保目录存在
            var dir = Path.GetDirectoryName(_bookmarksPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        public IList<SessionBookmark> LoadAll()
        {
            lock (_lock)
            {
                if (!File.Exists(_bookmarksPath))
                    return new List<SessionBookmark>();

                try
                {
                    var json = File.ReadAllText(_bookmarksPath, Encoding.UTF8);
                    return DeserializeBookmarks(json);
                }
                catch
                {
                    return new List<SessionBookmark>();
                }
            }
        }

        public void SaveAll(IList<SessionBookmark> bookmarks)
        {
            lock (_lock)
            {
                var json = SerializeBookmarks(bookmarks);
                File.WriteAllText(_bookmarksPath, json, Encoding.UTF8);
            }
        }

        public void Add(SessionBookmark bookmark)
        {
            if (bookmark == null) throw new ArgumentNullException(nameof(bookmark));
            if (string.IsNullOrEmpty(bookmark.Id))
                bookmark.Id = Guid.NewGuid().ToString("N");
            if (bookmark.CreatedAt == default)
                bookmark.CreatedAt = DateTime.UtcNow;

            var bookmarks = LoadAll().ToList();
            bookmarks.Add(bookmark);
            SaveAll(bookmarks);
        }

        public void Delete(string bookmarkId)
        {
            if (string.IsNullOrEmpty(bookmarkId))
                throw new ArgumentNullException(nameof(bookmarkId));

            var bookmarks = LoadAll().ToList();
            var index = bookmarks.FindIndex(b => b.Id == bookmarkId);
            if (index < 0)
                throw new KeyNotFoundException($"未找到 ID 为 {bookmarkId} 的书签");

            bookmarks.RemoveAt(index);
            SaveAll(bookmarks);
        }

        public void Update(SessionBookmark bookmark)
        {
            if (bookmark == null) throw new ArgumentNullException(nameof(bookmark));

            var bookmarks = LoadAll().ToList();
            var index = bookmarks.FindIndex(b => b.Id == bookmark.Id);
            if (index < 0)
                throw new KeyNotFoundException($"未找到 ID 为 {bookmark.Id} 的书签");

            bookmarks[index] = bookmark;
            SaveAll(bookmarks);
        }

        public void AddRecentConnection(RecentConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            lock (_lock)
            {
                var recent = GetRecentConnectionsInternal();

                // 移除重复记录
                recent.RemoveAll(r => r.ConnectionId == connection.ConnectionId);

                // 插入到最前面
                recent.Insert(0, connection);

                // 限制数量
                while (recent.Count > MaxRecentConnections)
                    recent.RemoveAt(recent.Count - 1);

                SaveRecentConnections(recent);
            }
        }

        public IList<RecentConnection> GetRecentConnections(int limit = 20)
        {
            lock (_lock)
            {
                var recent = GetRecentConnectionsInternal();
                return recent.Take(limit).ToList();
            }
        }

        private List<RecentConnection> GetRecentConnectionsInternal()
        {
            if (!File.Exists(_recentPath))
                return new List<RecentConnection>();

            try
            {
                var json = File.ReadAllText(_recentPath, Encoding.UTF8);
                return DeserializeRecentConnections(json);
            }
            catch
            {
                return new List<RecentConnection>();
            }
        }

        private void SaveRecentConnections(List<RecentConnection> recent)
        {
            var json = SerializeRecentConnections(recent);
            File.WriteAllText(_recentPath, json, Encoding.UTF8);
        }

        // ===== 手动 JSON 序列化（无外部依赖） =====

        private static string SerializeBookmarks(IList<SessionBookmark> bookmarks)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < bookmarks.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var b = bookmarks[i];
                sb.Append('{');
                sb.Append($"\"id\":\"{Escape(b.Id)}\"");
                sb.Append($",\"name\":\"{Escape(b.Name)}\"");
                sb.Append($",\"connectionId\":\"{Escape(b.ConnectionId)}\"");
                sb.Append($",\"tags\":\"{Escape(b.Tags ?? "")}\"");
                sb.Append($",\"createdAt\":\"{b.CreatedAt:O}\"");
                if (b.LastConnectedAt.HasValue)
                    sb.Append($",\"lastConnectedAt\":\"{b.LastConnectedAt.Value:O}\"");
                sb.Append($",\"connectCount\":{b.ConnectCount}");
                sb.Append($",\"isFavorite\":{(b.IsFavorite ? "true" : "false")}");
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static List<SessionBookmark> DeserializeBookmarks(string json)
        {
            var result = new List<SessionBookmark>();
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return result;

            // 简化解析：查找每个对象
            int i = 0;
            while (i < json.Length)
            {
                int objStart = json.IndexOf('{', i);
                if (objStart < 0) break;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                var obj = json.Substring(objStart, objEnd - objStart + 1);
                result.Add(ParseBookmark(obj));
                i = objEnd + 1;
            }

            return result;
        }

        private static SessionBookmark ParseBookmark(string obj)
        {
            return new SessionBookmark
            {
                Id = ExtractString(obj, "id"),
                Name = ExtractString(obj, "name"),
                ConnectionId = ExtractString(obj, "connectionId"),
                Tags = ExtractString(obj, "tags"),
                CreatedAt = DateTime.TryParse(ExtractString(obj, "createdAt"), out var ca) ? ca : DateTime.UtcNow,
                LastConnectedAt = DateTime.TryParse(ExtractString(obj, "lastConnectedAt"), out var lca) ? lca : (DateTime?)null,
                ConnectCount = int.TryParse(ExtractString(obj, "connectCount"), out var cc) ? cc : 0,
                IsFavorite = ExtractString(obj, "isFavorite") == "true"
            };
        }

        private static string SerializeRecentConnections(IList<RecentConnection> recent)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < recent.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var r = recent[i];
                sb.Append('{');
                sb.Append($"\"connectionId\":\"{Escape(r.ConnectionId)}\"");
                sb.Append($",\"host\":\"{Escape(r.Host)}\"");
                sb.Append($",\"protocol\":\"{Escape(r.Protocol)}\"");
                sb.Append($",\"connectedAt\":\"{r.ConnectedAt:O}\"");
                sb.Append($",\"success\":{(r.Success ? "true" : "false")}");
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static List<RecentConnection> DeserializeRecentConnections(string json)
        {
            var result = new List<RecentConnection>();
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return result;

            int i = 0;
            while (i < json.Length)
            {
                int objStart = json.IndexOf('{', i);
                if (objStart < 0) break;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0) break;

                var obj = json.Substring(objStart, objEnd - objStart + 1);
                result.Add(new RecentConnection
                {
                    ConnectionId = ExtractString(obj, "connectionId"),
                    Host = ExtractString(obj, "host"),
                    Protocol = ExtractString(obj, "protocol"),
                    ConnectedAt = DateTime.TryParse(ExtractString(obj, "connectedAt"), out var ca) ? ca : DateTime.UtcNow,
                    Success = ExtractString(obj, "success") == "true"
                });
                i = objEnd + 1;
            }

            return result;
        }

        private static string ExtractString(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0)
            {
                // 尝试非字符串值
                pattern = $"\"{key}\":";
                start = json.IndexOf(pattern, StringComparison.Ordinal);
                if (start < 0) return "";
                start += pattern.Length;
                int end = json.IndexOfAny(new[] { ',', '}' }, start);
                return end < 0 ? json.Substring(start).Trim() : json.Substring(start, end - start).Trim();
            }
            start += pattern.Length;
            int endIdx = start;
            while (endIdx < json.Length)
            {
                if (json[endIdx] == '\\') { endIdx += 2; continue; }
                if (json[endIdx] == '"') break;
                endIdx++;
            }
            return Unescape(json.Substring(start, endIdx - start));
        }

        private static int FindMatchingBrace(string json, int start)
        {
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                if (depth == 0) return i;
            }
            return -1;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\\\", "\\");
        }
    }
}
