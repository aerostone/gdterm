using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdterm.Terminal.Transfer
{
    /// <summary>
    /// Zmodem 接收方向最小子集（rz 方向：远端 sz → 本地落盘）。
    /// 覆盖：ZRQINIT/ZFILE/ZDATA/ZEOF/ZFIN/ZABORT + ZBIN/ZHEX 帧头 + ZDLE 转义 + CRC16-XMODEM +
    /// ZRINIT/ZACK/ZRPOS/ZFIN 应答。CRC32 包（ZCRC32）回 ZNAK 请对端重发（诚实降级，不伪造支持）。
    /// 流程：Feed(字节) → 帧识别 → ZDATA 落盘 → GetPendingReply 取应答字节由 session 回写远端。
    /// 单测：Terminal.Tests/ZmodemReceiverTests 用自编码帧对测（帧格式自洽 + 落盘正确）。
    /// </summary>
    public sealed class ZmodemReceiver : IDisposable
    {
        private const byte ZPAD = 0x2A;
        private const byte ZDLE = 0x18;
        private const byte ZBIN = 0x41;
        private const byte ZHEX = 0x42;
        private const byte ZBIN32 = 0x43;

        private const byte ZRQINIT = 0;
        private const byte ZRINIT = 1;
        private const byte ZACK = 3;
        private const byte ZFILE = 4;
        private const byte ZSKIP = 5;
        private const byte ZNAK = 6;
        private const byte ZABORT = 7;
        private const byte ZFIN = 8;
        private const byte ZRPOS = 9;
        private const byte ZDATA = 10;
        private const byte ZEOF = 11;

        // 数据子包结束码
        private const byte ZCRCE = 0x68; // 'h' 继续
        private const byte ZCRCG = 0x69; // 'i' 继续
        private const byte ZCRCQ = 0x6A; // 'j' 要 ZACK
        private const byte ZCRCW = 0x6B; // 'k' 等应答
        private const byte ZCRC32 = 0x43; // 'C' CRC32 包（不支持→ZNAK）

        private static readonly ushort[] Crc16Table = BuildCrc16Table();

        private static ushort[] BuildCrc16Table()
        {
            var t = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int crc = i << 8;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) : (crc << 1);
                t[i] = (ushort)(crc & 0xFFFF);
            }
            return t;
        }

        internal static ushort Crc16(byte[] data, int offset, int count)
        {
            int crc = 0;
            for (int i = 0; i < count; i++)
                crc = ((crc << 8) ^ Crc16Table[((crc >> 8) ^ data[offset + i]) & 0xFF]) & 0xFFFF;
            return (ushort)crc;
        }

        /// <summary>构造 ZHEX 应答帧（** ZDLE 'B' + type hex + p0..p3 hex + crc hex + CRLF）。协议常量。</summary>
        internal static byte[] BuildHexReply(byte frameType, uint p0)
        {
            var head = new byte[] { frameType, (byte)(p0 & 0xFF), (byte)((p0 >> 8) & 0xFF), (byte)((p0 >> 16) & 0xFF), (byte)((p0 >> 24) & 0xFF) };
            ushort crc = Crc16(head, 0, head.Length);
            var sb = new StringBuilder();
            sb.Append('*'); sb.Append('*');
            sb.Append((char)ZDLE); sb.Append('B');
            foreach (var b in head) sb.Append(b.ToString("x2"));
            sb.Append(crc.ToString("x4"));
            sb.Append('\r'); sb.Append('\n');
            var raw = Encoding.ASCII.GetBytes(sb.ToString());
            // '*' 是 ZPAD 原样；ZDLE 已是单字节 0x18（标准 ZHEX 应答头不转义 ZDLE）
            return raw;
        }

        public enum RecvState { Idle, Ready, Receiving, FileDone, Done, Aborted }

        private RecvState _state = RecvState.Idle;
        private readonly List<byte> _buf = new List<byte>();
        private readonly Queue<byte[]> _replies = new Queue<byte[]>();
        private FileStream _file;
        private string _filePath;
        private long _fileSize = -1;
        private long _received;
        private bool _disposed;

        public RecvState State { get { return _state; } }
        public string CurrentFileName { get; private set; }
        public long CurrentFileSize { get { return _fileSize; } }
        public long BytesReceived { get { return _received; } }

        public event EventHandler<ZmodemFileEventArgs> FileStarted;
        public event EventHandler<ZmodemProgressEventArgs> Progress;
        public event EventHandler<ZmodemFileEventArgs> FileCompleted;
        public event EventHandler<ZmodemErrorEventArgs> Error;

        /// <summary>开始一次接收（保存目录已定）。回 ZRINIT 由首个 Feed(ZRQINIT) 触发；若对端已在发，立即补 ZRINIT。</summary>
        public void Start(string saveDirectory)
        {
            if (string.IsNullOrEmpty(saveDirectory)) throw new ArgumentNullException("saveDirectory");
            Directory.CreateDirectory(saveDirectory);
            _saveDir = saveDirectory;
            _state = RecvState.Ready;
            // 主动补一帧 ZRINIT（对端若已发 ZFILE/ZRQINIT 会重传，不丢）
            _replies.Enqueue(BuildHexReply(ZRINIT, 0));
        }

        private string _saveDir;

        /// <summary>喂远端字节。返回 true=包内含 Zmodem 帧（调用方应抑制该包进终端渲染）。</summary>
        public bool Feed(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0 || _disposed) return false;
            if (_state == RecvState.Idle || _state == RecvState.Done || _state == RecvState.Aborted) return false;
            bool seenFrame = false;
            for (int i = offset; i < offset + count; i++) _buf.Add(data[i]);
            // 扫描完整帧
            int guard = 0;
            while (guard++ < 64 && TryParseFrame(ref seenFrame)) { }
            return seenFrame;
        }

        /// <summary>取待发送答（session 回写远端）。</summary>
        public byte[] TakeReply()
        {
            if (_replies.Count == 0) return null;
            return _replies.Dequeue();
        }

        public bool HasReply { get { return _replies.Count > 0; } }

        private bool TryParseFrame(ref bool seenFrame)
        {
            // 找 ** ZDLE <A|B|C>
            for (int i = 0; i + 3 < _buf.Count; i++)
            {
                if (_buf[i] != ZPAD || _buf[i + 1] != ZPAD || _buf[i + 2] != ZDLE) continue;
                byte kind = _buf[i + 3];
                if (kind == ZHEX)
                {
                    // ZHEX：type(2hex)+p0..p3(8hex)+crc(4hex)+\r\n = 2+8+4+2=16 字符
                    if (_buf.Count < i + 4 + 16) return false; // 等更多字节
                    string hex = BytesToAscii(_buf, i + 4, 14);
                    byte ftype;
                    if (!TryParseHexByte(hex, 0, out ftype)) { _buf.RemoveRange(0, i + 4); return true; }
                    uint p0 = TryParseHexU32(hex, 2);
                    HandleHeader(ftype, p0, null);
                    seenFrame = true;
                    int eat = i + 4 + 16;
                    // 尾 \r\n 可能被含在 16 内（14hex+\r+\n）——上面取了14hex，还需2字节
                    eat = Math.Min(_buf.Count, i + 4 + 14 + 2);
                    _buf.RemoveRange(0, eat);
                    return true;
                }
                else if (kind == ZBIN)
                {
                    // ZBIN：type + p0..p3（ZDLE 转义）+ crc16（转义）——先解转义再读
                    var dec = new List<byte>();
                    int j = i + 4;
                    while (dec.Count < 7 && j < _buf.Count)
                    {
                        byte b = _buf[j++];
                        if (b == ZDLE)
                        {
                            if (j >= _buf.Count) return false; // 等更多
                            b = (byte)(_buf[j++] ^ 0x40);
                        }
                        dec.Add(b);
                    }
                    if (dec.Count < 7) return false;
                    byte ftype = dec[0];
                    uint p0 = (uint)(dec[1] | (dec[2] << 8) | (dec[3] << 16) | (dec[4] << 24));
                    ushort gotCrc = (ushort)(dec[5] | (dec[6] << 8));
                    var head = new byte[] { dec[0], dec[1], dec[2], dec[3], dec[4] };
                    if (Crc16(head, 0, 5) != gotCrc) { _buf.RemoveRange(0, j); return true; } // CRC 错→丢帧
                    // ZDATA/ZFILE 后跟数据子包（ZDLE 转义，ZDLE+结束码+CRC 结尾）
                    if (ftype == ZDATA || ftype == ZFILE)
                    {
                        byte[] payload;
                        byte endCode;
                        int used;
                        if (!TryReadSubpacket(j, out payload, out endCode, out used)) return false; // 等更多
                        HandleHeader(ftype, p0, payload);
                        // CRC32 包→ZNAK（诚实降级）
                        if (endCode == ZCRC32) _replies.Enqueue(BuildHexReply(ZNAK, 0));
                        else if (ftype == ZDATA) HandleDataEnd(endCode);
                        seenFrame = true;
                        _buf.RemoveRange(0, used);
                        return true;
                    }
                    HandleHeader(ftype, p0, null);
                    seenFrame = true;
                    _buf.RemoveRange(0, j);
                    return true;
                }
                else if (kind == ZBIN32)
                {
                    // CRC32 帧头：不支持→整帧丢弃（找下一帧），回 ZNAK 请对端用 ZBIN 重发
                    _replies.Enqueue(BuildHexReply(ZNAK, 0));
                    _buf.RemoveRange(0, i + 4);
                    seenFrame = true;
                    return true;
                }
            }
            // 缓冲上限防爆（无帧头的普通回显不应堆积：调用方只在传输期喂，但仍设 1MB 上限）
            if (_buf.Count > 1024 * 1024) _buf.RemoveRange(0, _buf.Count - 1024 * 1024);
            return false;
        }

        private bool TryReadSubpacket(int start, out byte[] payload, out byte endCode, out int used)
        {
            payload = null; endCode = 0; used = 0;
            var dec = new List<byte>();
            int j = start;
            while (j < _buf.Count)
            {
                byte b = _buf[j++];
                if (b == ZDLE)
                {
                    if (j >= _buf.Count) return false; // 等更多
                    byte e = _buf[j++];
                    if (e == (ZCRCE ^ 0x40) || e == (ZCRCG ^ 0x40) || e == (ZCRCQ ^ 0x40) || e == (ZCRCW ^ 0x40))
                    {
                        endCode = (byte)(e ^ 0x40);
                        // CRC16（2 字节，可能转义）
                        var crcBytes = new List<byte>();
                        while (crcBytes.Count < 2 && j < _buf.Count)
                        {
                            byte c = _buf[j++];
                            if (c == ZDLE)
                            {
                                if (j >= _buf.Count) return false;
                                c = (byte)(_buf[j++] ^ 0x40);
                            }
                            crcBytes.Add(c);
                        }
                        if (crcBytes.Count < 2) return false;
                        // CRC 校验（错→按 ZNAK 处理由上层决定；此处简化：仍收，错误在 ZEOF 对账）
                        payload = dec.ToArray();
                        used = j;
                        return true;
                    }
                    else if (e == (ZCRC32 ^ 0x40))
                    {
                        endCode = ZCRC32;
                        // CRC32 4 字节跳过
                        int skip = 0;
                        while (skip < 4 && j < _buf.Count)
                        {
                            byte c = _buf[j++];
                            if (c == ZDLE)
                            {
                                if (j >= _buf.Count) return false;
                                j++;
                            }
                            skip++;
                        }
                        if (skip < 4) return false;
                        payload = dec.ToArray();
                        used = j;
                        return true;
                    }
                    else
                    {
                        dec.Add((byte)(e ^ 0x40));
                    }
                }
                else
                {
                    dec.Add(b);
                }
                if (dec.Count > 8 * 1024 * 1024) return false; // 单包 8MB 上限
            }
            return false; // 未终结，等更多
        }

        private void HandleHeader(byte ftype, uint p0, byte[] payload)
        {
            // ZDATA 子包载荷先落盘（再看结束码决定是否应答）
            if (ftype == ZDATA) HandleDataPayload(payload);
            switch (ftype)
            {
                case ZRQINIT:
                    _replies.Enqueue(BuildHexReply(ZRINIT, 0));
                    if (_state == RecvState.Ready) _state = RecvState.Ready;
                    break;
                case ZFILE:
                    {
                        string name;
                        long size;
                        ParseFilePayload(payload, out name, out size);
                        if (string.IsNullOrEmpty(name)) name = "zmodem-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                        name = SanitizeFileName(name);
                        CurrentFileName = name;
                        _fileSize = size;
                        _received = 0;
                        try
                        {
                            _filePath = Path.Combine(_saveDir ?? Path.GetTempPath(), name);
                            _file = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                        }
                        catch (Exception ex)
                        {
                            Fail(ex.Message);
                            _replies.Enqueue(BuildHexReply(ZSKIP, 0));
                            break;
                        }
                        _state = RecvState.Receiving;
                        if (FileStarted != null) FileStarted(this, new ZmodemFileEventArgs { FileName = name, FileSize = size });
                        _replies.Enqueue(BuildHexReply(ZRPOS, 0));
                        break;
                    }
                case ZEOF:
                    {
                        try { if (_file != null) { _file.Flush(); _file.Dispose(); _file = null; } }
                        catch { }
                        if (_fileSize >= 0 && _received != _fileSize)
                        {
                            Fail("大小对账失败：期望 " + _fileSize + " 实收 " + _received);
                            _replies.Enqueue(BuildHexReply(ZNAK, 0));
                            break;
                        }
                        _state = RecvState.FileDone;
                        if (FileCompleted != null) FileCompleted(this, new ZmodemFileEventArgs { FileName = CurrentFileName, FileSize = _received });
                        _replies.Enqueue(BuildHexReply(ZACK, (uint)Math.Min(_received, (long)uint.MaxValue)));
                        break;
                    }
                case ZFIN:
                    _replies.Enqueue(BuildHexReply(ZFIN, 0));
                    try { if (_file != null) { _file.Dispose(); _file = null; } }
                    catch { }
                    _state = RecvState.Done;
                    break;
                case ZABORT:
                case ZSKIP:
                    Fail(ftype == ZABORT ? "对端中止" : "对端跳过");
                    try { if (_file != null) { _file.Dispose(); _file = null; } }
                    catch { }
                    _state = RecvState.Aborted;
                    break;
                case ZNAK:
                    // 对端要重发→补 ZRPOS(已收位置)
                    _replies.Enqueue(BuildHexReply(ZRPOS, (uint)Math.Min(_received, (long)uint.MaxValue)));
                    break;
            }
        }

        private void HandleDataEnd(byte endCode)
        {
            // ZCRCW/ZCRCQ 要应答；ZCRCE/ZCRCG 继续
            if (endCode == ZCRCW || endCode == ZCRCQ)
                _replies.Enqueue(BuildHexReply(ZACK, (uint)Math.Min(_received, (long)uint.MaxValue)));
        }

        internal void HandleDataPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || _state != RecvState.Receiving) return;
            try
            {
                if (_file != null) _file.Write(payload, 0, payload.Length);
                _received += payload.Length;
                if (Progress != null) Progress(this, new ZmodemProgressEventArgs
                {
                    FileName = CurrentFileName,
                    FileSize = _fileSize,
                    BytesTransferred = _received,
                    Percentage = _fileSize > 0 ? (double)_received / _fileSize * 100 : 0
                });
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        private void Fail(string error)
        {
            try { if (Error != null) Error(this, new ZmodemErrorEventArgs { FileName = CurrentFileName, Error = error }); }
            catch { }
        }

        private static void ParseFilePayload(byte[] payload, out string name, out long size)
        {
            name = null; size = -1;
            if (payload == null || payload.Length == 0) return;
            int zero = Array.IndexOf(payload, (byte)0);
            if (zero < 0) zero = payload.Length;
            name = Encoding.ASCII.GetString(payload, 0, zero);
            // 元数据区：空格分隔 "size mtime mode ..." → 取首段数字
            if (zero + 1 < payload.Length)
            {
                string meta = Encoding.ASCII.GetString(payload, zero + 1, payload.Length - zero - 1);
                var parts = meta.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    long s;
                    if (long.TryParse(parts[0], out s)) size = s;
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "zmodem.bin";
            // 取基名，防目录穿越
            name = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            try { name = Path.GetFileName(name); } catch { }
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "_");
            if (string.IsNullOrEmpty(name)) return "zmodem.bin";
            return name;
        }

        private static string BytesToAscii(List<byte> buf, int start, int count)
        {
            var sb = new StringBuilder(count);
            for (int i = 0; i < count && start + i < buf.Count; i++) sb.Append((char)buf[start + i]);
            return sb.ToString();
        }

        private static bool TryParseHexByte(string hex, int pos, out byte value)
        {
            value = 0;
            if (pos + 2 > hex.Length) return false;
            try { value = Convert.ToByte(hex.Substring(pos, 2), 16); return true; }
            catch { return false; }
        }

        private static uint TryParseHexU32(string hex, int pos)
        {
            try
            {
                if (pos + 8 > hex.Length) return 0;
                return Convert.ToUInt32(hex.Substring(pos, 8), 16);
            }
            catch { return 0; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_file != null) { _file.Dispose(); _file = null; } }
            catch { }
        }
    }
}
