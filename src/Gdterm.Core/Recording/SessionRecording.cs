using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gdterm.Core.Recording
{
    /// <summary>录制帧方向：主机输出 / 本地输入。回放只喂输出（输入由主机回显，重放会双份）。</summary>
    public enum SessionFrameKind
    {
        Output = 0,
        Input = 1
    }

    /// <summary>录制文件头（回放时用来还原网格尺寸与来源信息）。</summary>
    public sealed class SessionRecordingHeader
    {
        public int Version { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public string Host { get; set; }
        public string ConnectionName { get; set; }
        public DateTime StartedUtc { get; set; }

        public SessionRecordingHeader()
        {
            Version = SessionRecording.FormatVersion;
            Columns = 80;
            Rows = 24;
            StartedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>一帧：相对起点的毫秒偏移 + 原始字节（不转码，转义序列必须原样回放）。</summary>
    public sealed class SessionFrame
    {
        public long OffsetMs { get; set; }
        public SessionFrameKind Kind { get; set; }
        public byte[] Data { get; set; }

        public SessionFrame() { Data = new byte[0]; }

        public SessionFrame(long offsetMs, SessionFrameKind kind, byte[] data)
        {
            OffsetMs = offsetMs;
            Kind = kind;
            Data = data ?? new byte[0];
        }
    }

    /// <summary>
    /// 会话录制（P1-2）。文本行式格式，能 grep、能 diff、坏了只丢一行：
    /// <code>
    /// GTR1
    /// {"v":1,"cols":120,"rows":30,"host":"10.0.0.11","name":"prod-web","started":"2026-11-12T02:00:00Z"}
    /// o 0 1b5b326a
    /// i 1234 6c73
    /// # end 2026-11-12T02:05:00Z duration=300000 frames=2
    /// </code>
    ///
    /// 为什么不用 asciinema 的 `.cast`：它的帧载荷带 JSON 转义，二进制控制序列要
    /// JSON 转义后再 base64，两层转义在 Win7 + net462 上手写解析器极易出边界 bug；
    /// 而"文本行 + base64"用 no-alloc 的 Convert 就能往返，且审计时可肉眼读时间轴。
    /// </summary>
    public sealed class SessionRecording
    {
        /// <summary>格式标识（首行）。改格式必须换标识，旧文件才不会被误读。</summary>
        public const string Magic = "GTR1";

        /// <summary>当前版本（头部 JSON 的 v）。</summary>
        public const int FormatVersion = 1;

        /// <summary>单个帧载荷上限：防坏文件里一个超长行把内存吃光。</summary>
        private const int MaxFrameBytes = 4 * 1024 * 1024;

        public SessionRecordingHeader Header { get; private set; }
        public List<SessionFrame> Frames { get; private set; }

        /// <summary>解析过程中跳过/丢弃的行数（0 表示文件完好）。</summary>
        public int SkippedLines { get; private set; }

        /// <summary>录制结束时间（文件尾注释里有则采用，否则取最后一帧偏移）。</summary>
        public DateTime? EndedUtc { get; set; }

        /// <summary>解析或读取失败的原因（TryLoad 用）。</summary>
        public string LoadError { get; private set; }

        public SessionRecording()
        {
            Header = new SessionRecordingHeader();
            Frames = new List<SessionFrame>();
        }

        /// <summary>总时长（毫秒）：最后一帧偏移与尾部 duration 取大者。</summary>
        public long DurationMs
        {
            get
            {
                long last = 0;
                if (Frames.Count > 0) last = Frames[Frames.Count - 1].OffsetMs;
                if (EndedUtc.HasValue)
                {
                    long span = (long)(EndedUtc.Value - Header.StartedUtc).TotalMilliseconds;
                    if (span > last) last = span;
                }
                return last < 0 ? 0 : last;
            }
        }

        /// <summary>输出字节总量（用于列表里显示"回放体量"）。</summary>
        public long OutputBytes
        {
            get
            {
                long n = 0;
                for (int i = 0; i < Frames.Count; i++)
                    if (Frames[i].Kind == SessionFrameKind.Output) n += Frames[i].Data.Length;
                return n;
            }
        }

        /// <summary>
        /// 读录制文件。失败不抛（UI 列表要能对每个坏文件给出原因而不是整体崩），
        /// 返回 null 并填 <paramref name="error"/>。
        /// </summary>
        public static SessionRecording TryLoad(string path, out string error)
        {
            error = null;
            try
            {
                var rec = Load(path);
                return rec;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static SessionRecording Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 为空", "path");
            if (!File.Exists(path)) throw new FileNotFoundException("录制文件不存在", path);

            var rec = new SessionRecording();
            using (var reader = new StreamReader(path, Encoding.UTF8, false))
            {
                string magic = reader.ReadLine();
                if (magic == null || magic.Trim() != Magic)
                    throw new InvalidDataException("不是 gdterm 录制文件（首行不是 " + Magic + "）");

                string headLine = reader.ReadLine();
                if (string.IsNullOrEmpty(headLine))
                    throw new InvalidDataException("录制文件缺少头信息行");
                rec.Header = ParseHeader(headLine);

                string line;
                int lineNo = 2;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNo++;
                    if (line.Length == 0) continue;
                    if (line[0] == '#')
                    {
                        if (line.StartsWith("# end", StringComparison.Ordinal))
                            rec.EndedUtc = ParseEndLine(line) ?? rec.EndedUtc;
                        continue;
                    }
                    var frame = ParseFrame(line);
                    if (frame == null) { rec.SkippedLines++; continue; }
                    rec.Frames.Add(frame);
                }
            }
            return rec;
        }

        private static SessionRecordingHeader ParseHeader(string json)
        {
            var h = new SessionRecordingHeader();
            // 头信息只有 6 个字段，专门写个极小 JSON 读取器比引第三方解析器划算
            // （Core 不依赖 System.Text.Json：net462 上要额外 NuGet，绿色版不引包）
            h.Version = (int)ReadNumber(json, "v", FormatVersion);
            h.Columns = (int)ReadNumber(json, "cols", 80);
            h.Rows = (int)ReadNumber(json, "rows", 24);
            h.Host = ReadString(json, "host");
            h.ConnectionName = ReadString(json, "name");
            string started = ReadString(json, "started");
            DateTime parsed;
            if (!string.IsNullOrEmpty(started) &&
                DateTime.TryParse(started, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                h.StartedUtc = parsed;
            if (h.Columns <= 0) h.Columns = 80;
            if (h.Rows <= 0) h.Rows = 24;
            if (h.Version > FormatVersion)
                throw new InvalidDataException("录制文件版本 " + h.Version + " 高于本程序支持的 " + FormatVersion);
            return h;
        }

        private static SessionFrame ParseFrame(string line)
        {
            // "<kind> <ms> <base64>"
            int sp1 = line.IndexOf(' ');
            if (sp1 <= 0) return null;
            int sp2 = line.IndexOf(' ', sp1 + 1);
            if (sp2 <= sp1) return null;

            char kindChar = line[0];
            SessionFrameKind kind;
            if (kindChar == 'o') kind = SessionFrameKind.Output;
            else if (kindChar == 'i') kind = SessionFrameKind.Input;
            else return null;

            long ms;
            if (!long.TryParse(line.Substring(sp1 + 1, sp2 - sp1 - 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out ms) || ms < 0)
                return null;

            string b64 = line.Substring(sp2 + 1).Trim();
            if (b64.Length > MaxFrameBytes * 2) return null;
            byte[] data;
            try { data = Convert.FromBase64String(b64); }
            catch (FormatException) { return null; }
            if (data.Length > MaxFrameBytes) return null;
            return new SessionFrame(ms, kind, data);
        }

        private static DateTime? ParseEndLine(string line)
        {
            // "# end 2026-11-12T02:05:00Z duration=300000 frames=2"
            var parts = line.Split(' ');
            if (parts.Length < 3) return null;
            DateTime dt;
            if (DateTime.TryParse(parts[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return dt;
            return null;
        }

        // ── 微型 JSON 取值（只服务头部，字段名靠引号定位，不处理嵌套） ──

        private static double ReadNumber(string json, string key, double fallback)
        {
            string raw = ReadRaw(json, key);
            double value;
            if (raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        private static string ReadString(string json, string key)
        {
            string raw = ReadRaw(json, key);
            if (raw == null) return null;
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return Unescape(raw.Substring(1, raw.Length - 2));
            return raw;
        }

        private static string ReadRaw(string json, string key)
        {
            string token = "\"" + key + "\"";
            int i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + token.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length) return null;

            if (json[i] == '"')
            {
                int end = i + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') return json.Substring(i, end - i + 1);
                    end++;
                }
                return null;
            }
            int stop = i;
            while (stop < json.Length && json[stop] != ',' && json[stop] != '}') stop++;
            return json.Substring(i, stop - i).Trim();
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        sb.Append('u');
                        break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 录制写入器：连接建立时开、断开时关。每帧一次 Write，不带缓冲延迟
    /// （崩溃时也要留下已发生的输出，这是审计价值的全部意义）。
    /// </summary>
    public sealed class SessionRecordingWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _lock = new object();
        private readonly string _path;
        private long _frames;
        private bool _disposed;

        public string Path { get { return _path; } }
        public long Frames { get { return _frames; } }
        public long BytesWritten { get; private set; }

        public SessionRecordingWriter(string path, SessionRecordingHeader header)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path 为空", "path");
            if (header == null) header = new SessionRecordingHeader();

            _path = path;
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var utf8 = new UTF8Encoding(false);
            _writer = new StreamWriter(path, false, utf8);
            _writer.NewLine = "\n";   // 固定 LF：跨工具 diff/grep 友好

            _writer.WriteLine(SessionRecording.Magic);
            _writer.WriteLine(BuildHeaderLine(header));
            _writer.Flush();
        }

        private static string BuildHeaderLine(SessionRecordingHeader h)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            sb.Append("\"v\":").Append(h.Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"cols\":").Append(h.Columns.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"rows\":").Append(h.Rows.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"host\":\"").Append(SessionRecording.Escape(h.Host)).Append('"');
            sb.Append(",\"name\":\"").Append(SessionRecording.Escape(h.ConnectionName)).Append('"');
            sb.Append(",\"started\":\"")
              .Append(h.StartedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
              .Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>追加一帧。空载荷直接忽略（心跳类空写会把文件撑出一堆废行）。</summary>
        public void Append(SessionFrameKind kind, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            Append(kind, data, 0, data.Length);
        }

        public void Append(SessionFrameKind kind, byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return;
            lock (_lock)
            {
                if (_disposed) return;
                long ms = _clock.ElapsedMilliseconds;
                string b64 = Convert.ToBase64String(data, offset, count);
                _writer.Write(kind == SessionFrameKind.Input ? 'i' : 'o');
                _writer.Write(' ');
                _writer.Write(ms.ToString(CultureInfo.InvariantCulture));
                _writer.Write(' ');
                _writer.Write(b64);
                _writer.Write('\n');
                _frames++;
                BytesWritten += count;
            }
        }

        /// <summary>批量刷新（终端输出是流式的，逐帧 Flush 太贵；调用方按 1s 节流）。</summary>
        public void Flush()
        {
            lock (_lock)
            {
                if (_disposed) return;
                try { _writer.Flush(); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    _writer.Write("# end ");
                    _writer.Write(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    _writer.Write(" duration=");
                    _writer.Write(_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                    _writer.Write(" frames=");
                    _writer.Write(_frames.ToString(CultureInfo.InvariantCulture));
                    _writer.Write('\n');
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch { }
            }
        }
    }
}
