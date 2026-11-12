using System;
using System.Collections.Generic;
using System.Text;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>
    /// 喂给 lib\VtNetCore.dll 之前的流预处理器。单遍扫描，做三件事：
    ///
    /// 1) SGR 归一化（原来 SgrNormalizer 的职责，逻辑未变）：
    ///    VtNetCore 1.0.30 的 SetCharacterAttribute 只按 “Parameters[0] == 38/48/58” 取扩展色，
    ///    且 256 色要求 Count==3、RGB 要求 Count==5。于是：
    ///      \e[0;38;5;196m       → 0 被当 reset，38 落到 default，5 = 慢闪，196 未知 → 前景发黑
    ///      \e[1;38;2;58;150;221m → 同样失效
    ///      \e[38;5;196;1m       → Count=4，直接 “SGR 38 must be longer than 1 option” 丢弃
    ///      \e[38:2::58;150;221m → CSI 解析器把 ':' 当终止符，剩余字节漏成正文（tmux/nvim 会用）
    ///    归一化后拆成多条 SGR 透传。
    ///
    /// 2) 私有模式跟踪（?1000/?1006/?2004/?1/?7 …）：库的 MouseTrackingEnabled 只升不降，
    ///    收到 ?1000l 也不回零，导致 TUI 退出后本地拖选永久失效；这里自己记一份。
    ///
    /// 3) 主机查询应答（库不答或答错的）：OSC 10/11/12 颜色、OSC 4 调色板、OSC 52 剪贴板、
    ///    XTGETTCAP、XTVERSION、CSI 14t/16t/18t 像素与文本区尺寸、xterm modifyOtherKeys。
    ///    应答攒在 <see cref="TakeReplies"/> 里由引擎统一回写，避免在扫描过程中重入发送。
    ///
    /// 只重写/丢弃可识别的序列，其余字节原样透传；跨 chunk 的半截序列暂存到下一次调用。
    /// </summary>
    internal sealed class VtStreamPreprocessor
    {
        private const int MaxPending = 1024;
        private byte[] _pending = new byte[0];
        private int _pendingLen;
        private byte[] _out = new byte[256];

        // DSR（CSI 6n / CSI 5n）：库完全不答，且答案依赖“喂到这一字节为止的光标位置”，
        // 所以这里只记下查询在 _out 里的偏移，由引擎分段喂库后在分界处算光标再回写
        private readonly List<int> _dsrAt = new List<int>();
        private readonly List<int> _dsrKind = new List<int>();

        // 鼠标上报模式位（?9/?1000/?1001/?1002/?1003）
        private int _mouseModes;
        private bool _sgrMouse;
        private bool _bracketedPaste;
        private bool _focusReporting;
        private int _modifyOtherKeys;

        // 宽字符展开的 UTF-8 跨包残留（正文可能被拆包，半截序列必须留到下一包）
        // ===== 反显（SGR 7）颜色交换状态 =====
        // 库把反显实现成"前景固定黑、背景取原前景"，主题默认前景/背景完全不参与，
        // 结果 \e[31;7m 变成黑底红上的黑字（Δ≈43 难读），默认反显也永远是纯黑而非主题前景。
        // 修法在预处理层：见到 7 就把逻辑 fg/bg 交换后以真彩显式下发，27 还原。
        private bool _sgrReverse;
        private string _logicalFg;    // "#rrggbb"；null = 默认前景
        private string _logicalBg;    // "#rrggbb"；null = 默认背景

        private readonly byte[] _utf8Pend = new byte[4];
        private int _utf8PendLen;

        private readonly StringBuilder _replies = new StringBuilder();

        /// <summary>应用当前是否要求鼠标上报。</summary>
        public bool MouseTracking { get { return _mouseModes != 0; } }

        /// <summary>鼠标上报是否用 SGR 编码（?1006）；否则 X10（ESC[M + 3 字节，坐标上限 223）。</summary>
        public bool SgrMouseEncoding { get { return _sgrMouse; } }

        /// <summary>应用是否要求 bracketed paste（?2004）。粘贴时必须用 ESC[200~ / ESC[201~ 包住。</summary>
        public bool BracketedPaste { get { return _bracketedPaste; } }

        /// <summary>xterm modifyOtherKeys 级别（0=关，1=Ctrl/Alt 组合，2=全部）。</summary>
        public int ModifyOtherKeys { get { return _modifyOtherKeys; } }

        /// <summary>光标形状（DECSCUSR CSI Ps SP q）：0/1 闪烁块 2 稳态块 3 闪烁下划线 4 稳态下划线 5 闪烁竖线 6 稳态竖线。</summary>
        public int CursorShape { get { return _cursorShape; } }
        private int _cursorShape;

        /// <summary>应用是否请求焦点事件上报（?1004）：UI 在获得/失去焦点时发 ESC[I / ESC[O。</summary>
        public bool FocusReporting { get { return _focusReporting; } }

        /// <summary>是否启用了应用光标键模式（DECCKM ?1）。</summary>
        public bool ApplicationCursorKeys { get { return (_mouseModes & 0x10000) != 0; } }

        /// <summary>终端报的名（XTGETTCAP TN / XTVERSION 用）。</summary>
        public string TerminalName = "xterm-256color";

        /// <summary>客户端版本号（XTVERSION 用）。</summary>
        public string ClientVersion = "1.0.0";

        /// <summary>默认前景 (#RRGGBB)。OSC 10 查询回它。</summary>
        public string DefaultForeground = "#c0c0c0";

        /// <summary>默认背景 (#RRGGBB)。OSC 11 查询回它。</summary>
        public string DefaultBackground = "#000000";

        /// <summary>光标色 (#RRGGBB)。OSC 12 查询回它。</summary>
        public string CursorColorValue = "#ffffff";

        /// <summary>调色板查询回调（0-255 → #RRGGBB）；为 null 时用内置 xterm 256 公式。</summary>
        public Func<int, string> PaletteLookup;

        /// <summary>文本区字符尺寸（CSI 18t 用）。</summary>
        public int Columns = 80;
        public int Rows = 24;

        /// <summary>单元像素尺寸（CSI 14t / 16t 用）。</summary>
        public int CellPixelWidth = 8;
        public int CellPixelHeight = 16;

        /// <summary>
        /// 窗口标题（OSC 0 / OSC 2，UTF-8）。库用 ASCII 解 OSC，中文标题会变成乱码，
        /// 所以标题在这里自己解、自己吞，不再透传给库。
        /// </summary>
        public Action<string> TitleSink;

        /// <summary>OSC 52 写入请求（base64 文本），由 UI 落到系统剪贴板。</summary>
        public Action<string> ClipboardWrite;

        /// <summary>
        /// OSC 7：shell 上报的当前工作目录（已百分号解码的绝对路径）。
        /// SFTP 面板「跟随终端目录」靠它；不处理的话 lib 会把这串当正文或直接丢掉。
        /// </summary>
        public Action<string> CwdSink;

        /// <summary>OSC 9 / 777 / 99：应用请求的桌面通知。</summary>
        public Action<TerminalNotification> NotificationSink;

        /// <summary>kitty OSC 99 分片通知的标题暂存（d=0 先来、d=1 补正文）。</summary>
        private string _kittyNotifyTitle;

        /// <summary>解码成功的图形（sixel / kitty graphics），由渲染器决定画在哪。</summary>
        public Action<TerminalImage> ImageSink;

        /// <summary>删除图形（kitty a=d）；id=0 表示全部，placementOnly=只删放置。</summary>
        public Action<int, bool> ImageDeleteSink;

        private readonly KittyGraphicsParser _kitty = new KittyGraphicsParser();

        public VtStreamPreprocessor()
        {
            _kitty.OnImage = img => { var h = ImageSink; if (h != null) { try { h(img); } catch { } } };
            _kitty.OnDelete = (id, placementOnly) => { var h = ImageDeleteSink; if (h != null) { try { h(id, placementOnly); } catch { } } };
            _kitty.OnReply = ReplyKitty;
        }

        /// <summary>把 kitty 应答塞进待发队列（和别的查询应答一起回写）。</summary>
        private void ReplyKitty(string text)
        {
            if (!string.IsNullOrEmpty(text)) _replies.Append(text);
        }

        /// <summary>取出并清空待发应答（引擎在 Feed 结束后统一回写主机）。</summary>
        public string TakeReplies()
        {
            if (_replies.Length == 0) return string.Empty;
            var s = _replies.ToString();
            _replies.Length = 0;
            return s;
        }

        /// <summary>本批输出里的光标查询：偏移（_out 内）与类型（6=位置，5=状态）。</summary>
        public void CopyCursorQueries(List<int> offsets, List<int> kinds)
        {
            offsets.Clear();
            kinds.Clear();
            for (int k = 0; k < _dsrAt.Count; k++)
            {
                offsets.Add(_dsrAt[k]);
                kinds.Add(_dsrKind[k]);
            }
            _dsrAt.Clear();
            _dsrKind.Clear();
        }

        /// <summary>清空跨包残留（会话重连/重置时调用）。</summary>
        public void Reset()
        {
            _pendingLen = 0;
            _mouseModes = 0;
            _sgrMouse = false;
            _bracketedPaste = false;
            _focusReporting = false;
            _cursorShape = 0;
            _modifyOtherKeys = 0;
            _utf8PendLen = 0;
            _replies.Length = 0;
            _kitty.Reset();
        }

        /// <summary>预处理并返回可直接喂给 DataConsumer 的字节。</summary>
        public byte[] Process(byte[] data, int offset, int count, out int outCount)
        {
            outCount = 0;
            _dsrAt.Clear();
            _dsrKind.Clear();
            if (data == null || count <= 0) return null;

            int total = _pendingLen + count;
            if (_out.Length < total * 2 + 32) _out = new byte[total * 2 + 64];

            int w = 0;
            int i = 0;
            byte[] src;
            if (_pendingLen > 0)
            {
                src = new byte[total];
                Array.Copy(_pending, 0, src, 0, _pendingLen);
                Array.Copy(data, offset, src, _pendingLen, count);
            }
            else
            {
                src = data;
            }
            int srcOffset = _pendingLen > 0 ? 0 : offset;
            int srcLen = total;
            _pendingLen = 0;

            while (i < srcLen)
            {
                byte b = src[srcOffset + i];
                if (b != 0x1B)
                {
                    // 正文成段透传：宽字符在这里补占位列（见 WideText），转义序列分支不受影响
                    int textStart = i;
                    while (i < srcLen && src[srcOffset + i] != 0x1B) i++;
                    w = AppendText(src, srcOffset + textStart, i - textStart, w);
                    continue;
                }
                if (i + 1 >= srcLen)
                {
                    // ESC 在包尾：不知道后面跟什么，必须缓到下包（否则跨包的 \e[ 会被当成两个字符）
                    Stash(src, srcOffset + i, 1);
                    i = srcLen;
                    break;
                }

                byte kind = src[srcOffset + i + 1];
                if (kind == (byte)']')
                {
                    int end, termLen;
                    if (!FindOscEnd(src, srcOffset, i + 2, srcLen, out end, out termLen))
                    {
                        int rest = srcLen - i;
                        if (rest > MaxPending) { for (int k = i; k < srcLen; k++) _out[w++] = src[srcOffset + k]; }
                        else Stash(src, srcOffset + i, rest);
                        i = srcLen;
                        break;
                    }
                    string payload = Encoding.ASCII.GetString(src, srcOffset + i + 2, end - (i + 2));
                    // 标题（OSC 0 / 2）：UTF-8 自己解，库里会变乱码
                    if (payload.Length >= 2 && (payload[1] == ';') && (payload[0] == '0' || payload[0] == '2'))
                    {
                        int titleOff = srcOffset + i + 4;
                        int titleLen = end - (i + 4);
                        if (titleLen > 0)
                        {
                            var sink = TitleSink;
                            if (sink != null)
                            {
                                string title;
                                try { title = Encoding.UTF8.GetString(src, titleOff, titleLen); }
                                catch { title = string.Empty; }
                                if (!string.IsNullOrEmpty(title))
                                {
                                    try { sink(title); } catch { }
                                }
                            }
                        }
                        i = end + termLen;
                        continue;
                    }
                    // OSC 7 / 9 / 99 / 777：路径和通知正文都是 UTF-8，库里按 ASCII 解会乱码 -> 自己解自己吞
                    if (TryHandleUtf8Osc(src, srcOffset + i + 2, end - (i + 2)))
                    {
                        i = end + termLen;
                        continue;
                    }
                    bool drop = HandleOsc(payload);
                    if (!drop) { for (int k = i; k < end + termLen; k++) _out[w++] = src[srcOffset + k]; }
                    i = end + termLen;
                    continue;
                }
                if (kind == (byte)'P')
                {
                    // DCS：... ESC \（只处理 XTGETTCAP，其余透传）
                    int st = FindSt(src, srcOffset, i + 2, srcLen);
                    if (st < 0)
                    {
                        int rest = srcLen - i;
                        if (rest > MaxPending) { for (int k = i; k < srcLen; k++) _out[w++] = src[srcOffset + k]; }
                        else Stash(src, srcOffset + i, rest);
                        i = srcLen;
                        break;
                    }
                    int dcsOff = srcOffset + i + 2;
                    int dcsLen = st - (i + 2);
                    // sixel：DCS 的第一个“参数段”以 q 结尾（ESC P q ... / ESC P 0;0;0q ...）
                    int qAt = -1;
                    for (int k = dcsOff; k < dcsOff + dcsLen; k++)
                    {
                        byte c = src[k];
                        if (c == (byte)'q') { qAt = k; break; }
                        if (!((c >= (byte)'0' && c <= (byte)'9') || c == (byte)';')) break;
                    }
                    if (qAt >= 0)
                    {
                        int dataOff = qAt + 1;
                        int dataLen = dcsOff + dcsLen - dataOff;
                        var sink = ImageSink;
                        if (sink != null && dataLen > 0)
                        {
                            var img = SixelDecoder.Decode(src, dataOff, dataLen);
                            if (img != null) { try { sink(img); } catch { } }
                        }
                        i = st + 2;
                        continue;
                    }
                    string payload = Encoding.ASCII.GetString(src, dcsOff, dcsLen);
                    bool drop = HandleDcs(payload);
                    if (!drop) { for (int k = i; k < st + 2; k++) _out[w++] = src[srcOffset + k]; }
                    i = st + 2;
                    continue;
                }
                if (kind == (byte)'_')
                {
                    // APC：kitty graphics 图形协议走这里（ESC _ G... ESC \）。
                    // 无论能不能画都必须吞掉——lib 会把 APC 参数当正文打到屏幕上
                    // （实测 “ESC_Gf=32,s=2,v=2;AAAA ST” → 屏幕出现 “Gf=32,s=2,v=2;AAAA”）。
                    int st = FindSt(src, srcOffset, i + 2, srcLen);
                    if (st < 0)
                    {
                        int rest = srcLen - i;
                        if (rest > MaxPending) { for (int k = i; k < srcLen; k++) _out[w++] = src[srcOffset + k]; }
                        else Stash(src, srcOffset + i, rest);
                        i = srcLen;
                        break;
                    }
                    if (st > i + 2 && src[srcOffset + i + 2] == (byte)'G')
                    {
                        _kitty.HandlePayload(src, srcOffset + i + 2, st - (i + 2));
                    }
                    i = st + 2;
                    continue;
                }
                if (kind != (byte)'[')
                {
                    if (kind == (byte)'c') ResetReverseTracking();   // RIS：引擎整屏复位，反显状态跟着复位
                    _out[w++] = b;   // 不是 CSI（例如单 ESC 键、ESC(0、ESC=）：原样透传
                    i++;
                    continue;
                }

                // CSI：找终止符 0x40..0x7E
                int j = i + 2;
                while (j < srcLen)
                {
                    byte c = src[srcOffset + j];
                    if (c >= 0x40 && c <= 0x7E) break;
                    if (c == 0x1B) break; // 序列被截断，重启
                    j++;
                }
                if (j >= srcLen)
                {
                    // 半截序列：暂存（限制长度，超长就原样吐出免得卡住）
                    int rest = srcLen - i;
                    if (rest > MaxPending)
                    {
                        for (int k = i; k < srcLen; k++) _out[w++] = src[srcOffset + k];
                        i = srcLen;
                        break;
                    }
                    Stash(src, srcOffset + i, rest);
                    i = srcLen;
                    break;
                }

                byte final = src[srcOffset + j];
                int bodyOff = i + 2;
                int bodyLen = j - bodyOff;
                byte bodyFirst = bodyLen > 0 ? src[srcOffset + bodyOff] : (byte)0;

                if ((final == (byte)'h' || final == (byte)'l') && bodyLen > 1 && bodyFirst == (byte)'?')
                {
                    bool setMode = final == (byte)'h';
                    UpdatePrivateModes(src, srcOffset + bodyOff + 1, bodyLen - 1, setMode);
                    // ?1047（备用屏，退出时清屏）库不认 → 改写成 ?47，行为最接近
                    if (HasMode(src, srcOffset + bodyOff + 1, bodyLen - 1, 1047))
                    {
                        string modes = RewriteMode(src, srcOffset + bodyOff + 1, bodyLen - 1, 1047, 47);
                        var rb = Encoding.ASCII.GetBytes("\u001b[?" + modes + (setMode ? "h" : "l"));
                        Grow(w + rb.Length + 8);
                        Array.Copy(rb, 0, _out, w, rb.Length);
                        w += rb.Length;
                        i = j + 1;
                        continue;
                    }
                }

                // DSR：CSI 6n 要光标位置、CSI 5n 要“一切正常”。库两者都不答（实测无任何回写）
                if (final == (byte)'n' && bodyLen == 1
                    && (src[srcOffset + bodyOff] == (byte)'6' || src[srcOffset + bodyOff] == (byte)'5'))
                {
                    _dsrAt.Add(w);
                    _dsrKind.Add(src[srcOffset + bodyOff] == (byte)'6' ? 6 : 5);
                    i = j + 1;
                    continue;
                }

                // 库不答 / 答错的查询：自己答（不改字节，某些直接原样透传让它忽略）
                if (HandleCsi(final, src, srcOffset + bodyOff, bodyLen))
                {
                    i = j + 1;
                    continue;
                }

                if (final != (byte)'m' || bodyLen == 0)
                {
                    for (int k = i; k <= j; k++) _out[w++] = src[srcOffset + k];
                    i = j + 1;
                    continue;
                }

                string body = Encoding.ASCII.GetString(src, srcOffset + bodyOff, bodyLen);
                string rewritten = RewriteReverse(body);      // 反显交换优先（顺带跟踪逻辑颜色）
                if (rewritten == null) rewritten = Rewrite(body);
                if (rewritten == null)
                {
                    for (int k = i; k <= j; k++) _out[w++] = src[srcOffset + k];
                }
                else
                {
                    byte[] rb = Encoding.ASCII.GetBytes(rewritten);
                    Grow(w + rb.Length);
                    Array.Copy(rb, 0, _out, w, rb.Length);
                    w += rb.Length;
                }
                i = j + 1;
            }

            outCount = w;
            return _out;
        }

        // ================= 序列定位 =================

        /// <summary>OSC 终止符：BEL 或 ST(ESC \)。返回终止符起始下标与长度。</summary>
        private static bool FindOscEnd(byte[] src, int srcOffset, int from, int srcLen, out int end, out int termLen)
        {
            end = -1;
            termLen = 0;
            for (int k = from; k < srcLen; k++)
            {
                byte c = src[srcOffset + k];
                if (c == 0x07) { end = k; termLen = 1; return true; }
                if (c == 0x1B)
                {
                    if (k + 1 >= srcLen) return false;      // 半截 ST，等下一包
                    if (src[srcOffset + k + 1] == (byte)'\\') { end = k; termLen = 2; return true; }
                    return false;                            // 非法/截断
                }
                if (k - from > 8192) return false;           // 超长 OSC：放弃识别，交给库
            }
            return false;
        }

        private static int FindSt(byte[] src, int srcOffset, int from, int srcLen)
        {
            for (int k = from; k < srcLen; k++)
            {
                if (src[srcOffset + k] == (byte)0x1B)
                {
                    if (k + 1 >= srcLen) return -1;
                    if (src[srcOffset + k + 1] == (byte)'\\') return k;
                    return -1;
                }
                if (k - from > 8192) return -1;
            }
            return -1;
        }

        // ================= 正文宽字符展开 =================

        /// <summary>
        /// 正文透传：UTF-8 解码（含跨包半截序列）→ 宽字符补占位列 → 重新编码。
        /// 纯 ASCII 段直接拷字节，不建字符串（吞吐热路径）。
        /// </summary>
        private int AppendText(byte[] src, int start, int len, int w)
        {
            if (len <= 0) return w;

            byte[] buf;
            int total;
            if (_utf8PendLen > 0)
            {
                total = _utf8PendLen + len;
                buf = new byte[total];
                Array.Copy(_utf8Pend, 0, buf, 0, _utf8PendLen);
                Array.Copy(src, start, buf, _utf8PendLen, len);
                _utf8PendLen = 0;
            }
            else
            {
                buf = src;
                total = start + len;
            }
            int off = buf == src ? start : 0;
            int end = total;

            // 1) 找出可安全解码的完整前缀（末尾可能是半截 UTF-8 序列）
            int p = off;
            int validEnd = off;
            bool allAscii = true;
            while (p < end)
            {
                byte b = buf[p];
                if (b < 0x80) { p++; validEnd = p; continue; }
                allAscii = false;
                int need = b >= 0xF0 ? 3 : b >= 0xE0 ? 2 : b >= 0xC0 ? 1 : -1;
                if (need < 0) { p++; validEnd = p; continue; }   // 非法首字节：交给解码器出替换符
                if (p + need >= end) break;                       // 半截序列：留到下一包
                p += need + 1;
                validEnd = p;
            }
            if (validEnd < end)
            {
                _utf8PendLen = end - validEnd;
                if (_utf8PendLen > _utf8Pend.Length) _utf8PendLen = _utf8Pend.Length;
                Array.Copy(buf, validEnd, _utf8Pend, 0, _utf8PendLen);
            }

            int n = validEnd - off;
            if (n <= 0) return w;

            if (allAscii)
            {
                Grow(w + n + 8);
                Array.Copy(buf, off, _out, w, n);
                return w + n;
            }

            string text;
            try { text = Encoding.UTF8.GetString(buf, off, n); }
            catch { text = null; }
            if (string.IsNullOrEmpty(text)) return w;

            string expanded = WideText.Expand(text);
            int blen = Encoding.UTF8.GetByteCount(expanded);
            Grow(w + blen + 8);
            w += Encoding.UTF8.GetBytes(expanded, 0, expanded.Length, _out, w);
            return w;
        }

        private void Stash(byte[] src, int srcStart, int len)
        {
            EnsurePending(len);
            Array.Copy(src, srcStart, _pending, 0, len);
            _pendingLen = len;
        }

        // ================= 查询应答 =================

        private enum OscQuery { None, Foreground, Background, Cursor, Palette, ClipboardWrite, ClipboardRead }

        /// <summary>返回 true 表示该 OSC 已被消费（不再透传给库）。</summary>
        private bool HandleOsc(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return false;
            int semi = payload.IndexOf(';');
            string head = semi < 0 ? payload : payload.Substring(0, semi);

            int code;
            if (!int.TryParse(head, out code)) return false;

            // 颜色类 OSC（4/5/10~19 设色、104/105/110~119 重置）一律自己吞掉，不透传给库：
            // ① 我们的调色板是固定的，设色对渲染没有意义；
            // ② 库对这些序列的解析有 bug——实测 “ESC]112 BEL”（无参数重置）会把之后整段输出吃掉，
            //    屏幕上什么都不剩（tmux 3.0a 重绘时正好发 “ESC]112 BEL”，整屏变空）。
            // 查询仍由我们应答（10/11/12/4;N;?），不依赖库。
            if (IsColorOsc(code)) return HandleColorOsc(code, payload, semi);

            switch (code)
            {
                case 8:
                    // OSC 8 超链接：lib 会把 URL 当正文打印（实测 “ESC]8;;http://x BEL” → 屏幕上出现 http://x）。
                    // 我们不渲染可点击链接，直接吞掉，只留链接文字。
                    return true;
                case 52:
                    {
                        if (semi < 0) return false;
                        // 52;<sel>;<data>；data 为 "?" 表示读剪贴板（拒绝，防信息外泄）
                        var rest = payload.Substring(semi + 1);
                        int sc = rest.IndexOf(';');
                        if (sc < 0) return false;
                        string data = rest.Substring(sc + 1);
                        if (data == "?") return true;                    // 静默拒绝
                        if (data.Length == 0) return true;               // 清空剪贴板：忽略
                        var cb = ClipboardWrite;
                        if (cb != null) { try { cb(data); } catch { } }
                        return true;                                     // 吞掉，不让库去猜
                    }
                default:
                    return false;
            }
        }

        /// <summary>
        /// OSC 7（cwd）/ 9 / 777 / 99（桌面通知）—— 统一走 UTF-8 自解。
        /// 返回 true 表示已消费（不再透传给库）。
        /// </summary>
        private bool TryHandleUtf8Osc(byte[] src, int off, int len)
        {
            if (len < 2) return false;
            int semi = -1;
            int digits = 0;
            for (int k = 0; k < len && k < 6; k++)
            {
                byte c = src[off + k];
                if (c == (byte)';') { semi = k; break; }
                if (c < (byte)'0' || c > (byte)'9') return false;
                digits++;
            }
            if (semi < 0 || digits == 0) return false;
            int code = 0;
            for (int k = 0; k < semi; k++) code = code * 10 + (src[off + k] - (byte)'0');
            if (code != 7 && code != 9 && code != 99 && code != 777) return false;

            string rest;
            try { rest = Encoding.UTF8.GetString(src, off + semi + 1, len - semi - 1); }
            catch { return true; }

            if (code == 7)
            {
                string path = ParseOsc7Path(rest);
                if (!string.IsNullOrEmpty(path))
                {
                    var sink = CwdSink;
                    if (sink != null) { try { sink(path); } catch { } }
                }
                return true;
            }

            if (code == 9)
            {
                // OSC 9;<message>（iTerm2 简写，无标题）。
                // 例外：Windows Terminal / ConEmu 用 9;4;<state>;<pct> 画进度条，那不是通知。
                var msg = (rest ?? "").Trim();
                if (msg.StartsWith("4;", StringComparison.Ordinal)) return true;
                RaiseNotification(null, msg, null);
                return true;
            }

            if (code == 777)
            {
                // OSC 777;notify;<title>;<body>（urxvt）；其它 777 子类型（window-title 等）直接吞掉
                var parts = (rest ?? "").Split(new[] { ';' }, 3);
                if (parts.Length >= 2 && string.Equals(parts[0], "notify", StringComparison.Ordinal))
                    RaiseNotification(parts[1], parts.Length >= 3 ? parts[2] : null, null);
                return true;
            }

            // OSC 99（kitty 通知协议）：metadata;payload，metadata 内以 ':' 分隔 key=value；
            // d=0 标题 / d=1 正文-补全 / d=2 正文-独立；u=0/1/2 紧急度。
            {
                string head = rest ?? "";
                string payload = "";
                int sc = head.IndexOf(';');
                if (sc >= 0) { payload = head.Substring(sc + 1); head = head.Substring(0, sc); }

                // 容错：有实现把 metadata 之间也用 ';' 分隔（u=2;d=1;正文）。把 payload 开头
                // 仍长得像 metadata 的段逐段剥回 head，最多 4 段，避免把正文吃掉。
                for (int guard = 0; guard < 4; guard++)
                {
                    int ns = payload.IndexOf(';');
                    if (ns < 0) break;
                    string cand = payload.Substring(0, ns);
                    if (!LooksLikeKittyMeta(cand)) break;
                    head = head.Length == 0 ? cand : head + ":" + cand;
                    payload = payload.Substring(ns + 1);
                }

                head = ":" + head + ":";
                if (head.IndexOf(":p=?:", StringComparison.Ordinal) >= 0
                    || head.IndexOf(":p=close:", StringComparison.Ordinal) >= 0) return true;   // 能力查询/关闭：静默

                string urgency = KittyMetaValue(head, "u");
                string done = KittyMetaValue(head, "d");
                if (done == "1" || done == "2")
                {
                    string title = _kittyNotifyTitle;
                    _kittyNotifyTitle = null;
                    RaiseNotification(title, payload, urgency);
                }
                else
                {
                    _kittyNotifyTitle = payload;   // d=0（或缺省，按标题算）
                }
                return true;
            }
        }

        /// <summary>OSC 99 metadata 的 key 白名单——用于把被 ';' 分隔的 metadata 段从 payload 里剥回。</summary>
        private static bool LooksLikeKittyMeta(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment.Length < 3) return false;
            char k = segment[0];
            if ("diupoaft".IndexOf(k) < 0) return false;
            if (segment[1] != '=') return false;
            for (int i = 2; i < segment.Length; i++)
            {
                char c = segment[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/') continue;
                return false;
            }
            return true;
        }

        /// <summary>紧急度归一：kitty 的 u=0/1/2（低/普通/严重）映射到 UI 的三档。</summary>
        private static string NormalizeUrgency(string urgency)
        {
            if (string.IsNullOrEmpty(urgency)) return "info";
            switch (urgency.Trim())
            {
                case "2": return "error";    // kitty critical
                case "1": return "warn";     // kitty normal
                case "0": return "info";     // kitty low
                default: return urgency;
            }
        }

        /// <summary>取 ":key=value:" 形式里的值。</summary>
        private static string KittyMetaValue(string headWithColons, string key)
        {
            string token = ":" + key + "=";
            int i = headWithColons.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + token.Length;
            int end = headWithColons.IndexOf(':', start);
            if (end < 0) return null;
            return headWithColons.Substring(start, end - start);
        }

        /// <summary>OSC 7 的路径部分：file://host/path 或裸路径，百分号解码后返回绝对路径。</summary>
        private static string ParseOsc7Path(string rest)
        {
            if (string.IsNullOrEmpty(rest)) return null;
            string path = rest;
            int scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                // 只认 file://（OSC 7 的规范形式）；http:// 之类是别的协议，别把 URL 路径当 cd 目标
                var proto = rest.Substring(0, scheme);
                if (!string.Equals(proto, "file", StringComparison.OrdinalIgnoreCase)) return null;
                int slash = rest.IndexOf('/', scheme + 3);
                if (slash < 0) return null;
                path = rest.Substring(slash);
            }
            path = PercentDecode(path);
            if (string.IsNullOrEmpty(path)) return null;
            if (path[0] != '/' && path[0] != '~') return null;   // 非绝对路径不认（防误导航）
            if (path.Length > 1 && path[path.Length - 1] == '/') path = path.Substring(0, path.Length - 1);
            return path;
        }

        /// <summary>%XX 解码（OSC 7 的路径按 RFC 3986 转义，中文目录常见）。</summary>
        private static string PercentDecode(string s)
        {
            if (s == null) return null;
            if (s.IndexOf('%') < 0) return s;
            var bytes = new List<byte>(s.Length);
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == '%' && k + 2 < s.Length && IsHex(s[k + 1]) && IsHex(s[k + 2]))
                {
                    bytes.Add((byte)(HexVal(s[k + 1]) * 16 + HexVal(s[k + 2])));
                    k += 2;
                    continue;
                }
                bytes.AddRange(Encoding.UTF8.GetBytes(s[k].ToString()));
            }
            try { return Encoding.UTF8.GetString(bytes.ToArray()); } catch { return s; }
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return c - 'A' + 10;
        }

        private void RaiseNotification(string title, string body, string urgency)
        {
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) return;
            var sink = NotificationSink;
            if (sink == null) return;
            var n = new TerminalNotification
            {
                Title = title ?? "",
                Body = body ?? "",
                Urgency = NormalizeUrgency(urgency)
            };
            try { sink(n); } catch { }
        }

        /// <summary>xterm 里跟调色板/动态颜色有关的 OSC 码。</summary>
        private static bool IsColorOsc(int code)
        {
            if (code >= 10 && code <= 19) return true;      // 10/11/12 前景/背景/光标色，13~19 其它动态色
            if (code >= 104 && code <= 105) return true;    // 重置调色板项 / 特殊项
            if (code >= 110 && code <= 119) return true;    // 重置动态色
            if (code == 4 || code == 5) return true;        // 设置调色板项 / 特殊项
            return false;
        }

        /// <summary>颜色类 OSC：只应答查询，其余（设色/重置）直接丢弃。</summary>
        private bool HandleColorOsc(int code, string payload, int semi)
        {
            string arg = semi < 0 ? "" : payload.Substring(semi + 1);
            if (code == 10 && IsQuery(arg)) { ReplyOscColor(10, DefaultForeground); return true; }
            if (code == 11 && IsQuery(arg)) { ReplyOscColor(11, DefaultBackground); return true; }
            if (code == 12 && IsQuery(arg)) { ReplyOscColor(12, CursorColorValue); return true; }
            if (code == 4 && semi >= 0)
            {
                var parts = arg.Split(';');
                if (parts.Length >= 2 && parts[1] == "?")
                {
                    int index;
                    if (int.TryParse(parts[0], out index))
                    {
                        string rgb = LookupPalette(index);
                        if (rgb != null) ReplyOscColor(4, index, rgb);
                    }
                }
            }
            return true;   // 设色 / 重置：对固定调色板无意义，吞掉最安全
        }

        private static bool IsQuery(string s)
        {
            return s == "?" || s == "?;";
        }

        private string LookupPalette(int index)
        {
            if (index < 0 || index > 255) return null;
            var f = PaletteLookup;
            if (f != null)
            {
                try
                {
                    var v = f(index);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                catch { }
            }
            return XtermPalette(index);
        }

        /// <summary>xterm 默认 16 色（主题未提供时兜底，保证 OSC 4;0..15;? 一定有应答）。</summary>
        private static readonly string[] XtermBase16 =
        {
            "#000000", "#cd0000", "#00cd00", "#cdcd00", "#0000ee", "#cd00cd", "#00cdcd", "#e5e5e5",
            "#7f7f7f", "#ff0000", "#00ff00", "#ffff00", "#5c5cff", "#ff00ff", "#00ffff", "#ffffff",
        };

        /// <summary>xterm 256 色：0-15 默认色、16-231 6x6x6 立方、232-255 24 级灰阶。</summary>
        internal static string XtermPalette(int index)
        {
            if (index < 16) return XtermBase16[index];
            if (index < 232)
            {
                int n = index - 16;
                int r = n / 36;
                int g = (n % 36) / 6;
                int b = n % 6;
                int R = r == 0 ? 0 : 55 + r * 40;
                int G = g == 0 ? 0 : 55 + g * 40;
                int B = b == 0 ? 0 : 55 + b * 40;
                return Hex(R, G, B);
            }
            int level = 8 + (index - 232) * 10;
            return Hex(level, level, level);
        }

        private static string Hex(int r, int g, int b)
        {
            return "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        private void ReplyOscColor(int code, string rgb)
        {
            ReplyOscColor(code, -1, rgb);
        }

        private void ReplyOscColor(int code, int index, string rgb)
        {
            var sb = new StringBuilder();
            sb.Append('\u001b').Append(']').Append(code);
            if (index >= 0) sb.Append(';').Append(index);
            sb.Append(';').Append(ToX11Rgb(rgb));
            sb.Append('\u001b').Append('\\');
            _replies.Append(sb);
        }

        /// <summary>#RRGGBB → rgb:RRRR/GGGG/BBBB（xterm 用 16 位分量）。</summary>
        private static string ToX11Rgb(string rgb)
        {
            int r, g, b;
            if (!TryParseHex(rgb, out r, out g, out b)) { r = g = b = 0; }
            return "rgb:" + (r * 257).ToString("x4") + "/" + (g * 257).ToString("x4") + "/" + (b * 257).ToString("x4");
        }

        private static bool TryParseHex(string s, out int r, out int g, out int b)
        {
            r = g = b = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s[0] == '#') s = s.Substring(1);
            if (s.Length != 6) return false;
            return int.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
                && int.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
                && int.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
        }

        /// <summary>DCS 处理：只认 XTGETTCAP（+q/+p）。返回 true 表示已消费。</summary>
        private bool HandleDcs(string payload)
        {
            if (string.IsNullOrEmpty(payload) || payload.Length < 3) return false;
            if (payload[0] != '+') return false;
            char kind = payload[1];
            if (kind != 'q' && kind != 'p') return false;

            // payload: "+q<hexname>[;<hexname>...]"
            var items = payload.Substring(2).Split(';');
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                if (item.Length == 0) continue;
                string name = HexToString(item);
                if (name == null) continue;
                string value;
                bool supported = CapabilityTable.TryGetValue(name, out value);
                // 应答格式严格是 DCS 1+r<hexname>=<hexvalue> ST：xterm/kitty/neovim 的解析器
                // 都把 "1+r" 和名字当连在一起的前缀，中间加空格会被当成非法名字
                sb.Append('\u001b').Append('P').Append(supported ? "1+r" : "0+r").Append(StringToHex(name));
                if (supported && !string.IsNullOrEmpty(value))
                    sb.Append('=').Append(StringToHex(value));
                sb.Append('\u001b').Append('\\');
            }
            if (sb.Length > 0) _replies.Append(sb);
            return true;
        }

        /// <summary>XTGETTCAP 能力表（够 tmux / nvim / vim 判断真彩与剪贴板用）。</summary>
        private Dictionary<string, string> CapabilityTable
        {
            get
            {
                if (_caps == null)
                {
                    _caps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "TN", TerminalName ?? "xterm-256color" },
                        { "Co", "256" },
                        { "RGB", "8/8/8" },
                        { "Tc", "" },                    // 真彩（无值，存在即支持）
                        { "Ms", "\u001b]52;%p1%s;%p2%s\a" },
                        { "Se", "\u001b[0 q" },
                        { "Ss", "\u001b[%p1%d q" },
                    };
                }
                return _caps;
            }
        }
        private Dictionary<string, string> _caps;

        private static string HexToString(string hex)
        {
            if (hex.Length == 0 || (hex.Length & 1) != 0) return null;
            var sb = new StringBuilder(hex.Length / 2);
            for (int i = 0; i < hex.Length; i += 2)
            {
                int v;
                if (!int.TryParse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out v)) return null;
                sb.Append((char)v);
            }
            return sb.ToString();
        }

        private static string StringToHex(string s)
        {
            var sb = new StringBuilder(s.Length * 2);
            foreach (char c in s) sb.Append(((int)c).ToString("x2"));
            return sb.ToString();
        }

        /// <summary>CSI 查询分派。返回 true 表示已消费（不回灌给库）。</summary>
        private bool HandleCsi(byte final, byte[] src, int bodyOff, int bodyLen)
        {
            string body = bodyLen > 0 ? Encoding.ASCII.GetString(src, bodyOff, bodyLen) : string.Empty;

            if (final == (byte)'q' && bodyLen > 0 && src[bodyOff + bodyLen - 1] == (byte)' '
                && (bodyLen == 1 || char.IsDigit((char)src[bodyOff])))
            {
                // DECSCUSR：CSI Ps SP q —— 记录下来给渲染器画，别透传给库（库会当乱码）
                int shape;
                if (!int.TryParse(body.Substring(0, bodyLen - 1), out shape)) shape = 0;
                _cursorShape = (shape < 0 || shape > 6) ? 0 : shape;
                return true;
            }

            if (final == (byte)'q' && body.StartsWith(">", StringComparison.Ordinal))
            {
                // XTVERSION：DCS >|name(version) ST
                if (body == ">")
                {
                    _replies.Append("\u001bP>|").Append(TerminalName ?? "gdterm").Append('(').Append(ClientVersion ?? "1").Append(")\u001b\\");
                    return true;
                }
                return false;
            }

            if (final == (byte)'t' && bodyLen > 0 && char.IsDigit((char)src[bodyOff]))
            {
                int op;
                if (!int.TryParse(body, out op)) return false;
                switch (op)
                {
                    case 14:  // 文本区像素：CSI 4 ; height ; width t
                        _replies.Append("\u001b[4;").Append(Rows * CellPixelHeight).Append(';').Append(Columns * CellPixelWidth).Append('t');
                        return true;
                    case 16:  // 单元像素：CSI 6 ; height ; width t
                        _replies.Append("\u001b[6;").Append(CellPixelHeight).Append(';').Append(CellPixelWidth).Append('t');
                        return true;
                    case 18:  // 文本区字符：CSI 8 ; rows ; cols t
                        _replies.Append("\u001b[8;").Append(Rows).Append(';').Append(Columns).Append('t');
                        return true;
                    default:
                        return false;
                }
            }

            if (final == (byte)'c')
            {
                // 设备属性由我们统一作答（库的 DA1 里没有 sixel 的 4，sixel 应用会因此关闭图形）
                if (bodyLen == 0 || body == "0")
                {
                    _replies.Append("\u001b[?64;1;2;4;6;9;15;18;21;22c");
                    return true;
                }
                if (body == ">")
                {
                    _replies.Append("\u001b[>41;136;0c");
                    return true;
                }
                return false;
            }

            if (final == (byte)'m' && body.StartsWith(">4", StringComparison.Ordinal))
            {
                // xterm modifyOtherKeys：CSI > 4 [; Pm] m；CSI > 4 ; ? m 是查询，只答不改
                var parts = body.Substring(2).TrimStart(';').Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0] == "?")
                {
                    _replies.Append("\u001b[>4;").Append(_modifyOtherKeys).Append('m');
                    return true;
                }
                int level = 1;
                if (parts.Length > 0) int.TryParse(parts[0], out level);
                if (level < 0) level = 0;
                if (level > 2) level = 2;
                _modifyOtherKeys = level;
                return true;   // 纯能力协商，不让库看到
            }

            return false;
        }

        // ================= 模式跟踪 =================

        private void UpdatePrivateModes(byte[] src, int start, int len, bool set)
        {
            if (len <= 0) return;
            int value = 0;
            bool hasDigit = false;
            for (int k = 0; k < len; k++)
            {
                byte c = src[start + k];
                if (c >= (byte)'0' && c <= (byte)'9')
                {
                    hasDigit = true;
                    value = value * 10 + (c - (byte)'0');
                    if (value > 100000) value = 100000;
                    continue;
                }
                if (c == (byte)';' || c == (byte)':')
                {
                    if (hasDigit) ApplyMode(value, set);
                    value = 0;
                    hasDigit = false;
                }
            }
            if (hasDigit) ApplyMode(value, set);
        }

        /// <summary>参数串里是否含某个私有模式号。</summary>
        private static bool HasMode(byte[] src, int off, int len, int want)
        {
            int value = 0;
            bool has = false;
            for (int k = off; k < off + len; k++)
            {
                byte c = src[k];
                if (c >= (byte)'0' && c <= (byte)'9') { value = value * 10 + (c - (byte)'0'); has = true; }
                else if (c == (byte)';' || c == (byte)':')
                {
                    if (has && value == want) return true;
                    value = 0; has = false;
                }
            }
            return has && value == want;
        }

        /// <summary>把参数串里的 from 换写成 to（?1047 → ?47）。</summary>
        private static string RewriteMode(byte[] src, int off, int len, int from, int to)
        {
            var sb = new StringBuilder(len + 2);
            int value = 0;
            bool has = false;
            for (int k = off; k < off + len; k++)
            {
                byte c = src[k];
                if (c >= (byte)'0' && c <= (byte)'9') { value = value * 10 + (c - (byte)'0'); has = true; }
                else
                {
                    if (has) { sb.Append(value == from ? to : value); has = false; value = 0; }
                    sb.Append((char)c);
                }
            }
            if (has) sb.Append(value == from ? to : value);
            return sb.ToString();
        }

        private void ApplyMode(int mode, bool set)
        {
            if (mode == 1006) { _sgrMouse = set; return; }
            if (mode == 1004) { _focusReporting = set; return; }
            if (mode == 2004) { _bracketedPaste = set; return; }
            if (mode == 1) { if (set) _mouseModes |= 0x10000; else _mouseModes &= ~0x10000; return; }  // DECCKM
            int bit;
            switch (mode)
            {
                case 9: bit = 1; break;
                case 1000: bit = 2; break;
                case 1001: bit = 4; break;
                case 1002: bit = 8; break;
                case 1003: bit = 16; break;
                default: return;
            }
            if (set) _mouseModes |= bit;
            else _mouseModes &= ~bit;
        }

        // ================= 缓冲 =================

        private void EnsurePending(int n)
        {
            if (_pending.Length < n) _pending = new byte[Math.Max(n, 64)];
        }

        private void Grow(int need)
        {
            if (_out.Length < need)
            {
                var bigger = new byte[Math.Max(need, _out.Length * 2)];
                Array.Copy(_out, bigger, _out.Length);
                _out = bigger;
            }
        }

        // ================= 反显（SGR 7）颜色交换 =================

        /// <summary>重置反显/颜色跟踪（RIS 或整屏复位时）。</summary>
        private void ResetReverseTracking()
        {
            _sgrReverse = false;
            _logicalFg = null;
            _logicalBg = null;
        }

        /// <summary>
        /// SGR 反显补丁：把 `7` 翻译成"交换后的真彩色"，`27` 翻译回逻辑色。
        ///
        /// 只在序列里真的出现 7/27、或反显生效期间改了颜色时才改写；
        /// 其它情况返回 null（字节原样透传），因此不碰反显的流完全零行为变化。
        /// </summary>
        internal string RewriteReverse(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            string normalized = body.IndexOf(':') >= 0 ? ColonToSemicolon(body) : body;
            var parts = normalized.Split(';');

            var others = new List<string>();
            bool need = false;

            for (int p = 0; p < parts.Length; p++)
            {
                string token = parts[p];
                int v = 0;
                bool numeric = token.Length > 0 && int.TryParse(token, out v);
                if (!numeric)
                {
                    if (token.Length > 0) others.Add(token);
                    continue;
                }

                // 扩展色：38/48（前景/背景）、58（下划线色，引擎不支持 → 丢弃）
                if (v == 38 || v == 48 || v == 58)
                {
                    int mode = 0;
                    if (p + 1 < parts.Length) int.TryParse(parts[p + 1], out mode);
                    int need3 = mode == 5 ? 3 : mode == 2 ? 5 : 0;
                    if (need3 > 0 && p + need3 - 1 < parts.Length)
                    {
                        string color = ResolveExtendedColor(parts, p, mode);
                        if (v != 58)
                        {
                            if (v == 38) _logicalFg = color;
                            else _logicalBg = color;
                            if (_sgrReverse) need = true;
                        }
                        p += need3 - 1;
                        continue;
                    }
                    others.Add(token);
                    continue;
                }

                if (v == 0)
                {
                    bool wasReverse = _sgrReverse;
                    _logicalFg = null;
                    _logicalBg = null;
                    _sgrReverse = false;
                    if (wasReverse) need = true;
                    others.Add("0");
                    continue;
                }
                if (v == 7)
                {
                    if (!_sgrReverse) { _sgrReverse = true; need = true; }
                    continue;   // 不再让库自己处理反显
                }
                if (v == 27)
                {
                    if (_sgrReverse) { _sgrReverse = false; need = true; }
                    continue;
                }
                if (v == 39) { _logicalFg = null; if (_sgrReverse) need = true; continue; }
                if (v == 49) { _logicalBg = null; if (_sgrReverse) need = true; continue; }
                if ((v >= 30 && v <= 37) || (v >= 90 && v <= 97))
                {
                    _logicalFg = LookupPalette(v <= 37 ? v - 30 : v - 90 + 8);
                    if (_sgrReverse) need = true;
                    continue;
                }
                if ((v >= 40 && v <= 47) || (v >= 100 && v <= 107))
                {
                    _logicalBg = LookupPalette(v <= 47 ? v - 40 : v - 100 + 8);
                    if (_sgrReverse) need = true;
                    continue;
                }
                others.Add(token);   // 其它属性（粗体/下划线/闪烁…）原样保留
            }

            if (!need) return null;

            // 反显期间：显示前景 = 逻辑背景（缺省用主题背景），显示背景 = 逻辑前景（缺省用主题前景）
            string showFg = _sgrReverse ? (_logicalBg ?? DefaultBackground) : (_logicalFg ?? DefaultForeground);
            string showBg = _sgrReverse ? (_logicalFg ?? DefaultForeground) : (_logicalBg ?? DefaultBackground);
            int fr, fg, fb, br, bg, bb;
            if (!TryParseHex(showFg, out fr, out fg, out fb) || !TryParseHex(showBg, out br, out bg, out bb))
            {
                // 颜色解析不出来就退回老路：让库自己反显，状态别装作自己在管
                _sgrReverse = false;
                return null;
            }

            // 库只认"一条 SGR 一个颜色"：38;2;…;48;2;… 连着发会被解析丢色（见 Rewrite 的拆分逻辑），
            // 所以这里也必须拆成多条。
            var sb = new StringBuilder(others.Count * 4 + 56);
            if (others.Count > 0)
            {
                sb.Append('\u001b').Append('[');
                sb.Append(string.Join(";", others));
                sb.Append('m');
            }
            sb.Append('\u001b').Append('[');
            sb.Append("38;2;").Append(fr).Append(';').Append(fg).Append(';').Append(fb).Append('m');
            sb.Append('\u001b').Append('[');
            sb.Append("48;2;").Append(br).Append(';').Append(bg).Append(';').Append(bb).Append('m');
            return sb.ToString();
        }

        /// <summary>解析 38/48 的扩展色参数（mode 5 = 调色板号，mode 2 = 真彩 RGB）。</summary>
        private string ResolveExtendedColor(string[] parts, int at, int mode)
        {
            if (mode == 5)
            {
                int idx;
                if (int.TryParse(parts[at + 2], out idx)) return LookupPalette(idx);
                return null;
            }
            int r, g, b;
            if (!int.TryParse(parts[at + 2], out r)) return null;
            if (!int.TryParse(parts[at + 3], out g)) return null;
            if (!int.TryParse(parts[at + 4], out b)) return null;
            r = Math.Min(255, Math.Max(0, r)); g = Math.Min(255, Math.Max(0, g)); b = Math.Min(255, Math.Max(0, b));
            return "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        // ================= SGR 归一化 =================

        /// <summary>
        /// 参数串归一化。返回 null 表示无需改写；否则返回可含多条 SGR 的完整字符串。
        /// </summary>
        internal static string Rewrite(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            bool hasColon = body.IndexOf(':') >= 0;
            if (!hasColon)
            {
                if (!NeedsSplit(body)) return null;
            }

            string normalized = hasColon ? ColonToSemicolon(body) : body;

            var parts = normalized.Split(';');
            var segments = new List<string>();
            var pending = new List<string>();

            for (int p = 0; p < parts.Length; p++)
            {
                string token = parts[p];
                int value;
                if (!int.TryParse(token, out value)) { pending.Add(token); continue; }

                if (value == 38 || value == 48 || value == 58)
                {
                    // 扩展色：38/48 支持 5;n（256 色）与 2;r;g;b（RGB）；58（下划线色）引擎不支持 → 丢弃
                    int modeIdx = p + 1;
                    if (modeIdx < parts.Length)
                    {
                        int mode;
                        int.TryParse(parts[modeIdx], out mode);
                        int need = mode == 5 ? 3 : mode == 2 ? 5 : 0;
                        if (need > 0 && p + need - 1 < parts.Length)
                        {
                            if (value == 58)
                            {
                                p += need - 1;
                                continue;
                            }
                            if (pending.Count > 0) { segments.Add(string.Join(";", pending)); pending.Clear(); }
                            var color = new string[need];
                            for (int k = 0; k < need; k++) color[k] = parts[p + k];
                            segments.Add(string.Join(";", color));
                            p += need - 1;
                            continue;
                        }
                    }
                }

                pending.Add(token);
            }
            if (pending.Count > 0) segments.Add(string.Join(";", pending));

            if (segments.Count == 0)
                return string.Empty;  // 整条都被丢弃（例如只有 58;...）→ 发空 SGR 无害
            if (segments.Count == 1 && !hasColon && segments[0] == body)
                return null;          // 无需改写

            var sb = new StringBuilder();
            for (int k = 0; k < segments.Count; k++)
            {
                sb.Append('\u001b');
                sb.Append('[');
                sb.Append(segments[k]);
                sb.Append('m');
            }
            return sb.ToString();
        }

        /// <summary>是否含“首参之外”的扩展色（即需要拆分）。</summary>
        private static bool NeedsSplit(string body)
        {
            bool first = true;
            foreach (var token in body.Split(';'))
            {
                int v;
                if (first) { first = false; int.TryParse(token, out v); if (v == 38 || v == 48 || v == 58) return true; continue; }
                if (token == "38" || token == "48" || token == "58") return true;
            }
            var parts = body.Split(';');
            if (parts.Length > 0)
            {
                int v0;
                if (int.TryParse(parts[0], out v0) && (v0 == 38 || v0 == 48 || v0 == 58))
                {
                    int mode;
                    int need = 0;
                    if (parts.Length > 1 && int.TryParse(parts[1], out mode))
                        need = mode == 5 ? 3 : mode == 2 ? 5 : 0;
                    if (need > 0 && parts.Length > need) return true;
                }
            }
            return false;
        }

        /// <summary>`38:2::58:150:221` → `38;2;58;150;221`；`4:3` → `4`；空子参填 0。</summary>
        private static string ColonToSemicolon(string body)
        {
            var sb = new StringBuilder(body.Length + 8);
            var segment = new StringBuilder();
            var subs = new List<string>();

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == ':' || c == ';')
                {
                    subs.Add(segment.Length == 0 ? "0" : segment.ToString());
                    segment.Length = 0;
                    if (c == ';')
                    {
                        EmitGroup(sb, subs);
                        subs.Clear();
                    }
                }
                else
                {
                    segment.Append(c);
                }
            }
            if (segment.Length > 0 || subs.Count > 0)
            {
                subs.Add(segment.Length == 0 ? "0" : segment.ToString());
                EmitGroup(sb, subs);
            }
            return sb.ToString();
        }

        private static void EmitGroup(StringBuilder sb, List<string> subs)
        {
            if (subs.Count == 0) return;
            int func;
            int.TryParse(subs[0], out func);

            var outSubs = new List<string>();
            outSubs.Add(subs[0]);

            if (func == 38 || func == 48)
            {
                int mode = 0;
                if (subs.Count > 1) int.TryParse(subs[1], out mode);
                if (mode == 5)
                {
                    outSubs.Add("5");
                    outSubs.Add(subs.Count > 2 ? subs[2] : "0");
                }
                else if (mode == 2)
                {
                    outSubs.Add("2");
                    int start = subs.Count >= 6 ? 3 : 2;
                    for (int k = start; k < start + 3 && k < subs.Count; k++) outSubs.Add(subs[k]);
                    while (outSubs.Count < 5) outSubs.Add("0");
                }
                else
                {
                    for (int k = 1; k < subs.Count; k++) outSubs.Add(subs[k]);
                }
            }
            else if (func == 58)
            {
                for (int k = 1; k < subs.Count; k++) outSubs.Add(subs[k]);
            }

            if (sb.Length > 0) sb.Append(';');
            sb.Append(string.Join(";", outSubs));
        }
    }
}
