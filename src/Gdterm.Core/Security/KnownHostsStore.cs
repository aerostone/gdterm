using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Gdterm.Core.Security
{
    /// <summary>
    /// known_hosts 里的一条记录（OpenSSH 文本格式：hosts keytype blob [comment]）。
    /// </summary>
    public sealed class KnownHostEntry
    {
        /// <summary>hosts 字段原文（可能是 host1,[host2]:2222 这样的逗号列表）。</summary>
        public string HostField { get; set; }

        /// <summary>算法名：ssh-rsa / ssh-ed25519 / ecdsa-sha2-nistp256 …</summary>
        public string KeyType { get; set; }

        /// <summary>公钥 blob 的 OpenSSH 文本编码。</summary>
        public string BlobText { get; set; }

        /// <summary>来源文件（诊断用：区分本程序签发的与系统 known_hosts 里的）。</summary>
        public string SourcePath { get; set; }
    }

    /// <summary>
    /// known_hosts 存储（TOFU 首次信任）——本程序自己写一份，同时可只读叠加系统 ~/.ssh/known_hosts。
    ///
    /// 为什么不做成直接复用系统文件：gdterm 是绿色版（U 盘便携，见 attention.md），
    /// 不该去改用户 ~/.ssh 下的东西；写成 data/config/known_hosts 才符合便携约定。
    /// 但读取时把系统那份叠进来——用户用命令行 SSH 连过的机器，进 gdterm 就不再被问一遍，
    /// 这才是降低误报的正确做法（误报多到用户无脑点信任，这套校验就等于没有）。
    ///
    /// 明确不支持的形态（写在这里，避免以后被当 bug 反复排查）：
    ///  - 哈希化条目 |1|salt|hash：没有 salt 无法反查主机名，只能跳过（OpenSSH 默认哈希化，
    ///    所以系统文件多数条目读不到——读不到就当"未见过"，会问一次，而不是放行）。
    ///  - 通配条目 *.example.com / !host：OpenSSH 的 pattern 语义，本实现不做匹配。
    /// </summary>
    public sealed class KnownHostsStore
    {
        private readonly string _writablePath;
        private readonly string[] _readOnlyPaths;
        private readonly object _lock = new object();
        private List<KnownHostEntry> _cache;

        /// <summary>可写文件（data/config/known_hosts）+ 额外只读来源（系统 known_hosts）。</summary>
        public KnownHostsStore(string writablePath, params string[] readOnlyPaths)
        {
            _writablePath = writablePath;
            _readOnlyPaths = readOnlyPaths ?? new string[0];
        }

        /// <summary>可写文件路径（诊断/UI 展示用）。</summary>
        public string WritablePath { get { return _writablePath; } }

        /// <summary>按 主机+端口+算法 精确查一条（22 端口的条目也匹配裸主机名）。</summary>
        public KnownHostEntry FindExact(string host, int port, string keyType)
        {
            if (string.IsNullOrEmpty(host)) return null;
            EnsureLoaded();
            lock (_lock)
            {
                for (int i = 0; i < _cache.Count; i++)
                {
                    var e = _cache[i];
                    if (!string.Equals(e.KeyType, keyType, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Matches(e.HostField, host, port)) return e;
                }
            }
            return null;
        }

        /// <summary>该主机（任意算法）已记录的条目——用于区分"从没见过"与"见过但密钥变了"。</summary>
        public List<KnownHostEntry> FindAny(string host, int port)
        {
            var list = new List<KnownHostEntry>();
            if (string.IsNullOrEmpty(host)) return list;
            EnsureLoaded();
            lock (_lock)
            {
                for (int i = 0; i < _cache.Count; i++)
                {
                    if (Matches(_cache[i].HostField, host, port)) list.Add(_cache[i]);
                }
            }
            return list;
        }

        /// <summary>写入/更新一条记录（同主机同算法只保留最新一条，旧的先删掉）。</summary>
        public void Save(string host, int port, string keyType, string blobText)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(keyType) || string.IsNullOrEmpty(blobText))
                throw new ArgumentException("host/keyType/blob 不能为空");

            lock (_lock)
            {
                EnsureLoaded();
                // 密钥轮换后不能留两条互相矛盾的记录
                _cache.RemoveAll(e => string.Equals(e.KeyType, keyType, StringComparison.OrdinalIgnoreCase)
                                      && Matches(e.HostField, host, port)
                                      && string.Equals(e.SourcePath, _writablePath, StringComparison.OrdinalIgnoreCase));
                _cache.Add(new KnownHostEntry
                {
                    HostField = FormatHostField(host, port),
                    KeyType = keyType,
                    BlobText = blobText,
                    SourcePath = _writablePath
                });
                WriteWritableFile();
            }
        }

        /// <summary>丢弃缓存，下次查询重新读盘（用户手工改了 known_hosts 之后调用）。</summary>
        public void Invalidate()
        {
            lock (_lock) { _cache = null; }
        }

        // ── 文件读写 ──

        private void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_cache != null) return;
                var all = new List<KnownHostEntry>();
                // 先读系统文件（只读），再读自己的：自己的后读 → FindExact 命中自己的那条优先
                for (int i = 0; i < _readOnlyPaths.Length; i++) ReadInto(all, _readOnlyPaths[i]);
                ReadInto(all, _writablePath);
                _cache = all;
            }
        }

        private static void ReadInto(List<KnownHostEntry> into, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    var e = ParseLine(lines[i]);
                    if (e == null) continue;
                    e.SourcePath = path;
                    into.Add(e);
                }
            }
            catch
            {
                // 读不到就当没有：宁可多问一次，也不能因为一个读权限问题把校验关掉
            }
        }

        /// <summary>解析一行；注释/空行/哈希化/通配条目返回 null。</summary>
        public static KnownHostEntry ParseLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') return null;
            var parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            var hosts = parts[0];
            if (hosts.IndexOf('|') >= 0) return null;     // 哈希化：|1|salt|hash
            if (hosts.IndexOf('*') >= 0 || hosts.IndexOf('?') >= 0 || hosts.IndexOf('!') >= 0) return null;
            return new KnownHostEntry { HostField = hosts, KeyType = parts[1], BlobText = parts[2] };
        }

        private void WriteWritableFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(_writablePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# gdterm known_hosts —— 首次连接时用户确认过的主机公钥（TOFU）");
                sb.AppendLine("# 格式与 OpenSSH known_hosts 相同；删除某行即等于『忘掉这台机器』，下次连接会重新询问。");
                for (int i = 0; i < _cache.Count; i++)
                {
                    var e = _cache[i];
                    if (!string.Equals(e.SourcePath, _writablePath, StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(e.HostField).Append(' ').Append(e.KeyType).Append(' ').Append(e.BlobText)
                      .AppendLine(" # gdterm:" + DateTime.Now.ToString("yyyy-MM-dd"));
                }
                // 原子替换：先写临时文件再 Move——断电/并发写出半个文件等于校验全废
                var tmp = _writablePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(_writablePath)) File.Delete(_writablePath);
                File.Move(tmp, _writablePath);
            }
            catch
            {
                // 写盘失败不能把连接搞挂：本次信任仍生效（内存 + 会话缓存），只是重启后再问一次
            }
        }

        // ── 主机字段匹配 ──

        /// <summary>生成 hosts 字段：22 端口写裸主机名，非 22 写 [host]:port（OpenSSH 惯例）。</summary>
        public static string FormatHostField(string host, int port)
        {
            var h = (host ?? "").Trim();
            if (h.StartsWith("[")) return h;                        // 已经是 [v6]:port 形态
            if (h.IndexOf(':') >= 0) return "[" + h + "]:" + port;   // IPv6 必须加方括号
            return port == 22 ? h : "[" + h + "]:" + port;
        }

        /// <summary>hosts 字段（可能是逗号列表）是否包含该主机:端口。</summary>
        public static bool Matches(string hostField, string host, int port)
        {
            if (string.IsNullOrEmpty(hostField) || string.IsNullOrEmpty(host)) return false;
            var tokens = hostField.Split(',');
            for (int i = 0; i < tokens.Length; i++)
            {
                var tok = tokens[i].Trim();
                if (tok.Length == 0) continue;
                if (tok.StartsWith("["))
                {
                    int close = tok.IndexOf(']');
                    if (close <= 1) continue;
                    var inner = tok.Substring(1, close - 1);
                    int p = 22;
                    if (close + 1 < tok.Length && tok[close + 1] == ':')
                        int.TryParse(tok.Substring(close + 2), out p);
                    if (p == port && string.Equals(inner, host, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (port == 22 && string.Equals(tok, host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ── 指纹 ──

        /// <summary>OpenSSH 风格 SHA256 指纹：SHA256:xxx（末尾 = 去掉）。</summary>
        public static string FingerprintSha256(byte[] publicKey)
        {
            if (publicKey == null) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(publicKey);
                return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
            }
        }

        /// <summary>传统 MD5 指纹 aa:bb:cc:…（老文档/老设备控制台里常见这种）。</summary>
        public static string FingerprintMd5(byte[] publicKey)
        {
            if (publicKey == null) return "";
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(publicKey);
                var sb = new StringBuilder(hash.Length * 3);
                for (int i = 0; i < hash.Length; i++)
                {
                    if (i > 0) sb.Append(':');
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>公钥 blob → OpenSSH 文本。</summary>
        public static string ToBlobText(byte[] publicKey)
        {
            return publicKey == null ? "" : Convert.ToBase64String(publicKey);
        }
    }
}
