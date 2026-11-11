using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>
    /// 外部终端工具的连接格式导入（对比 nyaterm 吸收项 A2）：
    /// Xshell（.xsh INI，一连接一文件）、MobaXterm（MobaXterm.ini，#CODE# 书签格式，GBK）、
    /// WindTerm（user.sessions，JSON）。纯解析无 UI 依赖，Gdterm.Tests fixture 回归覆盖。
    /// 约定：源格式中的明文密码一律不导入（gdterm 凭据统一走 KeePass，connections.json 无 password 字段）。
    /// 键名按大小写不敏感宽松匹配，解析不了的条目跳过不报错。
    /// </summary>
    public static class ExternalFormatImporter
    {
        // ========== Xshell ==========

        /// <summary>
        /// 导入 Xshell 会话。path 可为单个 .xsh 文件或目录（递归 *.xsh）。
        /// 文件名（不含扩展名）即连接名；Type 缺省按 SSH。
        /// </summary>
        public static List<ConnectionConfig> ImportXshell(string path)
        {
            var result = new List<ConnectionConfig>();
            if (string.IsNullOrEmpty(path)) return result;

            var files = Directory.Exists(path)
                ? Directory.GetFiles(path, "*.xsh", SearchOption.AllDirectories)
                : new[] { path };
            foreach (var f in files)
            {
                ConnectionConfig c;
                if (TryParseXshellFile(f, out c))
                    result.Add(c);
            }
            return result;
        }

        internal static bool TryParseXshellFile(string filePath, out ConnectionConfig config)
        {
            config = null;
            if (!File.Exists(filePath)) return false;

            string host = null, port = null, user = null, type = null;
            foreach (var raw in ReadLinesSmartEncoding(filePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "host":
                    case "hostname": host = val; break;
                    case "port": port = val; break;
                    case "username": user = val; break;
                    case "type": type = val; break;
                    // Password/KeyFileName 等有意不读：明文密码不入 connections.json（KeePass 约定）
                }
            }
            if (string.IsNullOrEmpty(host)) return false;

            config = Build(host, port, user, type,
                Path.GetFileNameWithoutExtension(filePath), "", "Xshell",
                isSerialGuess: false);
            return config != null;
        }

        // ========== MobaXterm ==========

        /// <summary>
        /// 导入 MobaXterm.ini（便携版在 exe 同目录；安装版在 %APPDATA%\MobaXterm\）。
        /// 书签区 [Bookmarks]/[Bookmarks_N]，条目形如 <连接名>=#109#1%HOST%PORT%USER%...
        /// 协议码：109=SSH 98=Telnet 1=RDP 131=串口；VNC/WSL/FTP 等 gdterm 不支持的跳过。
        /// 编码：MobaXterm 按系统 ANSI 代码页写入（中文 Windows=GBK/cp936），UTF-8 BOM 时按 UTF-8。
        /// </summary>
        public static List<ConnectionConfig> ImportMobaXterm(string filePath)
        {
            var result = new List<ConnectionConfig>();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return result;

            string content;
            try { content = DecodeMobaXtermBytes(File.ReadAllBytes(filePath)); }
            catch { return result; }

            string section = null, folder = null;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.TrimEnd('\r').TrimStart();
                if (line.Length == 0 || line[0] == ';') continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    folder = null;
                    continue;
                }
                if (section == null) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1);

                if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
                {
                    folder = val.Trim();
                    continue;
                }
                if (!IsMobaXtermBookmarkSection(section) || val.Length < 3 || val[0] != '#')
                    continue;

                ConnectionConfig c;
                if (TryParseMobaXtermEntry(key, val.Trim(), folder ?? "", out c))
                    result.Add(c);
            }
            return result;
        }

        internal static bool IsMobaXtermBookmarkSection(string section)
        {
            if (string.IsNullOrEmpty(section)) return false;
            if (section == "Bookmarks") return true;
            // Bookmarks_1 / Bookmarks_2 ...
            const string p = "Bookmarks_";
            if (section.Length <= p.Length || !section.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return false;
            int n;
            return int.TryParse(section.Substring(p.Length), out n) && n >= 1;
        }

        internal static bool TryParseMobaXtermEntry(string name, string value, string folder, out ConnectionConfig config)
        {
            config = null;
            if (string.IsNullOrEmpty(value) || value[0] != '#') return false;

            // 前缀 #<协议码>#<连接类型>%...
            int hash2 = value.IndexOf('#', 1);
            if (hash2 < 0) return false;
            int code;
            if (!int.TryParse(value.Substring(1, hash2 - 1), out code)) return false;

            ProtocolType proto;
            switch (code)
            {
                case 109: proto = ProtocolType.SSH; break;
                case 98: proto = ProtocolType.Telnet; break;
                case 1: proto = ProtocolType.RDP; break;
                case 131: proto = ProtocolType.Serial; break;
                default: return false; // VNC=2 / WSL=105 / FTP=4 等：gdterm 不支持，跳过
            }

            int pct = value.IndexOf('%', hash2);
            if (pct < 0) return false;
            var fields = value.Substring(pct + 1).Split('%');
            if (fields.Length == 0 || string.IsNullOrEmpty(fields[0].Trim())) return false;

            var host = fields[0].Trim();
            string port = fields.Length > 1 ? fields[1].Trim() : "";
            string user = fields.Length > 2 ? fields[2].Trim() : "";

            config = Build(host, port, user, proto.ToString(), name, folder, "MobaXterm",
                isSerialGuess: false);
            if (config != null && proto == ProtocolType.Serial)
                ApplySerialDetails(config, host, port);
            return config != null;
        }

        // ========== WindTerm ==========

        /// <summary>
        /// 导入 WindTerm 会话文件 user.sessions（位于 WindTerm 安装目录
        /// profiles\default.v10\terminal\user.sessions，JSON）。
        /// 会话字段：session.label / session.protocol / session.target(user@host|host) /
        /// session.port / session.group。本地 Shell 会话跳过。
        /// </summary>
        public static List<ConnectionConfig> ImportWindTerm(string filePath)
        {
            var result = new List<ConnectionConfig>();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return result;

            string json;
            try { json = ReadAllTextSmartEncoding(filePath); }
            catch { return result; }

            int pos = 0;
            while (pos < json.Length)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0) break;
                int objEnd = FindSessionObjectEnd(json, objStart);
                if (objEnd < 0) break;
                var obj = json.Substring(objStart, objEnd - objStart + 1);

                // 只看对象顶层键：顶层容器 {"sessions":[...]} 虽含嵌套的 session.* 文本，
                // 但顶层无 session.protocol，不会误判为会话对象
                var fields = ParseTopLevelFields(obj);
                if (fields.ContainsKey("session.protocol") || fields.ContainsKey("session.label"))
                {
                    var proto = fields["session.protocol"];
                    if (!string.IsNullOrEmpty(proto) &&
                        !proto.Trim().Equals("Shell", StringComparison.OrdinalIgnoreCase))
                    {
                        var target = fields.TryGetValue("session.target", out var t2) ? t2 : "";
                        var label = fields.TryGetValue("session.label", out var l2) ? l2 : "";
                        var group = fields.TryGetValue("session.group", out var g2) ? g2 : "";
                        var portStr = fields.TryGetValue("session.port", out var p2) ? p2 : "";
                        var user = fields.TryGetValue("session.authUser", out var u2) ? u2 : "";

                        var host = target;
                        int at = host.IndexOf('@');
                        if (at > 0)
                        {
                            if (string.IsNullOrEmpty(user)) user = host.Substring(0, at);
                            host = host.Substring(at + 1);
                        }
                        if (!string.IsNullOrEmpty(host))
                        {
                            var c = Build(host, portStr, user, proto,
                                string.IsNullOrEmpty(label) ? host : label,
                                group, "WindTerm",
                                isSerialGuess: proto.IndexOf("Serial", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (c != null) result.Add(c);
                        }
                    }
                    pos = objEnd + 1; // 会话对象整体跳过（内含嵌套配置对象）
                }
                else
                {
                    pos = objStart + 1; // 容器对象：继续向内找
                }
            }
            return result;
        }

        /// <summary>
        /// 解析 JSON 对象的顶层键值对（深度感知，跳过嵌套对象/数组块，跳过字符串内的花括号）。
        /// 嵌套块与数组不存入；标量去引号去空白。用于区分顶层容器与真正携带 session.* 的会话对象。
        /// </summary>
        internal static Dictionary<string, string> ParseTopLevelFields(string obj)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(obj) || obj[0] != '{') return fields;

            int n = obj.Length;
            int i = 1;
            while (i < n)
            {
                char c = obj[i];
                if (c == ',' || char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '}') break;
                if (c != '"') { i++; continue; }

                // 顶层键（审计 F1：结束引号用 JsonText.SkipString 判定，避免 \\" 被当成结束）
                int keyEnd = Gdterm.Core.JsonText.SkipString(obj, i);
                if (keyEnd <= i || keyEnd >= n || obj[keyEnd] != '"') break;
                var key = obj.Substring(i + 1, keyEnd - i - 1);

                int j = keyEnd + 1;
                while (j < n && char.IsWhiteSpace(obj[j])) j++;
                if (j >= n || obj[j] != ':') { i = keyEnd + 1; continue; }

                int v = j + 1;
                while (v < n && char.IsWhiteSpace(obj[v])) v++;
                if (v >= n) break;

                if (obj[v] == '"')
                {
                    // string 值
                    int e = Gdterm.Core.JsonText.SkipString(obj, v);
                    if (e <= v || e >= n) break;
                    fields[key] = Gdterm.Core.JsonText.Unescape(obj.Substring(v + 1, e - v - 1));
                    i = e + 1;
                }
                else if (obj[v] == '{' || obj[v] == '[')
                {
                    // 嵌套对象/数组：整块跳过（不存）
                    char open = obj[v];
                    char close = open == '{' ? '}' : ']';
                    int d = 0, e = v;
                    bool inStr = false;
                    for (e = v; e < n; e++)
                    {
                        char cc = obj[e];
                        if (inStr)
                        {
                            if (cc == '\\') e++;
                            else if (cc == '"') inStr = false;
                            continue;
                        }
                        if (cc == '"') inStr = true;
                        else if (cc == open) d++;
                        else if (cc == close && --d == 0) break;
                    }
                    i = e + 1;
                }
                else
                {
                    // 标量（数字/true/false/null）
                    int e = v;
                    while (e < n && obj[e] != ',' && obj[e] != '}' && obj[e] != ']') e++;
                    fields[key] = obj.Substring(v, e - v).Trim();
                    i = e;
                }
            }
            return fields;
        }

        // ========== 公共构建 ==========

        /// <summary>
        /// 由原始字段构建 ConnectionConfig；host 为空返回 null。
        /// 串口协议（Serial/COM 开头 host）自动填 SerialConfig（PortName + 波特率）。
        /// </summary>
        internal static ConnectionConfig Build(string host, string port, string user, string type,
            string name, string group, string source, bool isSerialGuess)
        {
            if (string.IsNullOrEmpty(host)) return null;
            var proto = ParseProtocolType(type, host, isSerialGuess);

            var c = new ConnectionConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrEmpty(name) ? host : name,
                Protocol = proto,
                Host = host,
                Username = string.IsNullOrEmpty(user) ? null : user,
                GroupPath = string.IsNullOrEmpty(group) ? "" : group,
                Metadata = new Dictionary<string, string> { { "source", source } }
            };

            if (proto == ProtocolType.Serial)
            {
                ApplySerialDetails(c, host, port);
                c.Port = 9600;
            }
            else
            {
                c.Port = ParsePort(port, proto);
            }
            return c;
        }

        private static void ApplySerialDetails(ConnectionConfig c, string portName, string baudOrPort)
        {
            c.Host = portName;
            var serial = c.Serial ?? new SerialConfig();
            serial.PortName = portName;
            int baud;
            if (!string.IsNullOrEmpty(baudOrPort) && int.TryParse(baudOrPort, out baud)
                && baud >= 1200 && baud <= 921600)
                serial.BaudRate = baud;
            c.Serial = serial;
        }

        internal static ProtocolType ParseProtocolType(string type, string host, bool isSerialGuess)
        {
            if (isSerialGuess || (!string.IsNullOrEmpty(host) &&
                host.StartsWith("COM", StringComparison.OrdinalIgnoreCase)))
                return ProtocolType.Serial;
            if (!string.IsNullOrEmpty(type))
            {
                var t = type.Trim().ToUpperInvariant();
                if (t.Contains("RDP")) return ProtocolType.RDP;
                if (t.Contains("TELNET")) return ProtocolType.Telnet;
                if (t.Contains("SERIAL") || t.Contains("COM")) return ProtocolType.Serial;
                if (t.Contains("SSH")) return ProtocolType.SSH;
            }
            return ProtocolType.SSH;
        }

        internal static int ParsePort(string s, ProtocolType proto)
        {
            int port;
            if (!string.IsNullOrEmpty(s) && int.TryParse(s, out port) && port > 0 && port <= 65535)
                return port;
            switch (proto)
            {
                case ProtocolType.RDP: return 3389;
                case ProtocolType.Telnet: return 23;
                case ProtocolType.Serial: return 9600;
                default: return 22;
            }
        }

        // ========== 编码与 JSON 辅助 ==========

        /// <summary>
        /// 通用文件读取（Xshell/WindTerm 用）：BOM 优先；无 BOM 时先严格 UTF-8 解码，
        /// 失败再回退 GBK(cp936)。MobaXterm 专用走 DecodeMobaXtermBytes（其恒按 ANSI 页写盘）。
        /// </summary>
        internal static string ReadAllTextSmartEncoding(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            return DecodeBytes(bytes);
        }

        internal static string[] ReadLinesSmartEncoding(string filePath)
        {
            return DecodeBytes(File.ReadAllBytes(filePath)).Split(new[] { '\r', '\n' });
        }

        internal static string DecodeBytes(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                // 严格校验：合法 UTF-8（含纯 ASCII）直接用；否则按中文 ANSI 页解码
                var strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes);
            }
            catch (ArgumentException)
            {
            }
            try
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        /// <summary>MobaXterm 专用：UTF-8 BOM 按 UTF-8，否则一律 cp936（MobaXterm 按 ANSI 页写盘）。</summary>
        internal static string DecodeMobaXtermBytes(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            return Encoding.GetEncoding(936).GetString(bytes);
        }

        /// <summary>从 openPos 的 '{' 找配对 '}'（跳过字符串字面量内的花括号）。</summary>
        /// <summary>从 openPos 的 { 找配对 }（跳过字符串字面量内的花括号）。</summary>
        internal static int FindSessionObjectEnd(string json, int openPos)
        {
            // 审计 F1 去重：括号配对只留 JsonText 一份实现
            return Gdterm.Core.JsonText.FindMatchingBracket(json, openPos);
        }
    }
}
