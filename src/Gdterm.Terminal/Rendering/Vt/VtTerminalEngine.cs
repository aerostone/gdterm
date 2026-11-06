using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Layout;
using VtNetCore.VirtualTerminal.Model;
using VtNetCore.XTermParser;

namespace Gdterm.Terminal.Rendering.Vt
{
    /// <summary>
    /// Phase 0 引擎封装：隔离 VtNetCore 类型，对外只暴露 gdterm 自有 cell/span 模型。
    /// 用法：Feed 字节/文本 → Snapshot 可见页 → 键盘/鼠标回传 SendToHost。
    /// </summary>
    public sealed class VtTerminalEngine : IDisposable
    {
        private readonly VirtualTerminalController _controller;
        private readonly DataConsumer _consumer;
        private readonly VtStreamPreprocessor _pre = new VtStreamPreprocessor();
        private readonly object _lock = new object();
        private readonly List<int> _dsrAt = new List<int>();
        private readonly List<int> _dsrKind = new List<int>();
        private bool _disposed;
        private int _feedErrCount;
        // 用户手动滚上去后钉住的 TopRow（-1 = 跟随底部）。否则库会把“距底行数”保持不变，
        // 有新输出时视图被一路拖回底部，回看历史就废了。
        private int _pinnedTop = -1;

        /// <summary>终端需要回写主机的序列（DA/CPR/键鼠等）。</summary>
        public event EventHandler<byte[]> SendToHost;

        /// <summary>窗口标题变更（OSC）。</summary>
        public event EventHandler<string> TitleChanged;

        /// <summary>应用请求写系统剪贴板（OSC 52），参数是 base64 文本。</summary>
        public event EventHandler<string> ClipboardSetRequested;

        /// <summary>解出一张图（sixel / kitty graphics），渲染器负责画。</summary>
        public event EventHandler<TerminalImage> ImageReceived;

        /// <summary>删除图（kitty a=d）；ImageId 0 表示全部，PlacementOnly 表示只撤显示。</summary>
        public event EventHandler<TerminalImageDeleteEventArgs> ImageDeleted;

        public int Columns { get; private set; }
        public int Rows { get; private set; }

        /// <summary>scrollback 硬顶（含可见行）。默认 500，对齐低内存门禁。</summary>
        public int MaximumHistoryLines
        {
            get { return _controller.MaximumHistoryLines; }
            set
            {
                if (value < Rows) value = Rows;
                if (value > 2000) value = 2000;
                _controller.MaximumHistoryLines = value;
            }
        }

        public bool Changed
        {
            get { lock (_lock) return _controller.Changed; }
        }

        public int CursorColumn
        {
            get
            {
                lock (_lock)
                {
                    var p = _controller.ViewPort.CursorPosition;
                    return p.Column;
                }
            }
        }

        /// <summary>当前视口第一行的绝对页号（图形定位用）。</summary>
        public int ViewportTopRow
        {
            get
            {
                lock (_lock)
                {
                    try { return _controller.ViewPort.TopRow; }
                    catch { return 0; }
                }
            }
        }

        public int CursorRow
        {
            get
            {
                lock (_lock)
                {
                    var p = _controller.ViewPort.CursorPosition;
                    return p.Row;
                }
            }
        }

        public bool ShowCursor
        {
            get { lock (_lock) return _controller.CursorState.ShowCursor; }
        }

        /// <summary>应用是否要求 bracketed paste（?2004）。粘贴时用 ESC[200~ / ESC[201~ 包裹。</summary>
        public bool IsBracketedPasteEnabled
        {
            get { lock (_lock) return _pre.BracketedPaste; }
        }

        /// <summary>应用是否启用了鼠标上报（vim/less/mc）。UI 据此决定左键是拖选还是透传。</summary>
        /// <remarks>
        /// 以自记的私有模式位为准：库的 MouseTrackingEnabled 收到 ?1000l 也不回零（实测），
        /// 会让 TUI 退出后左键拖选永久失效。
        /// </remarks>
        public bool IsMouseTrackingEnabled
        {
            get { lock (_lock) return _pre.MouseTracking; }
        }

        /// <summary>xterm modifyOtherKeys 级别（0=关）。键盘层据此改发 CSI 27;mod;code~。</summary>
        public int ModifyOtherKeys
        {
            get { lock (_lock) return _pre.ModifyOtherKeys; }
        }

        /// <summary>DECCKM 应用光标键模式。</summary>
        public bool ApplicationCursorKeys
        {
            get { lock (_lock) return _pre.ApplicationCursorKeys; }
        }

        /// <summary>
        /// 告诉应答器当前主题色与单元像素尺寸，用于 OSC 10/11/12/4 与 CSI 14t/16t/18t 应答。
        /// 主题换了/字体变了都要重新调用（幂等）。
        /// </summary>
        public void SetQueryMetadata(string foreground, string background, string cursor, int cellWidth, int cellHeight)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(foreground)) _pre.DefaultForeground = foreground;
                if (!string.IsNullOrEmpty(background)) _pre.DefaultBackground = background;
                if (!string.IsNullOrEmpty(cursor)) _pre.CursorColorValue = cursor;
                if (cellWidth > 0) _pre.CellPixelWidth = cellWidth;
                if (cellHeight > 0) _pre.CellPixelHeight = cellHeight;
            }
        }

        /// <summary>调色板查询回调（OSC 4;N;? 用），返回 #RRGGBB。</summary>
        public Func<int, string> PaletteLookup
        {
            get { lock (_lock) return _pre.PaletteLookup; }
            set { lock (_lock) _pre.PaletteLookup = value; }
        }

        /// <summary>终端类型名（XTVERSION / XTGETTCAP TN 应答里出现），与 SSH pty 请求的 TERM 保持一致。</summary>
        public string TerminalName
        {
            get { lock (_lock) return _pre.TerminalName; }
            set { lock (_lock) { if (!string.IsNullOrWhiteSpace(value)) _pre.TerminalName = value.Trim(); } }
        }

        /// <summary>客户端版本号（XTVERSION 应答里出现）。</summary>
        public string ClientVersion
        {
            get { lock (_lock) return _pre.ClientVersion; }
            set { lock (_lock) _pre.ClientVersion = value; }
        }

        public VtTerminalEngine(int columns = 80, int rows = 24, int maxHistory = 500)
        {
            // 中文标题：库里用 ASCII 解 OSC，会乱码 → 预处理器自己解 UTF-8 后从这里抛事件
            _pre.TitleSink = t => { var h = TitleChanged; if (h != null) { try { h(this, t); } catch { } } };
            if (columns < 2) columns = 2;
            if (rows < 1) rows = 1;
            Columns = columns;
            Rows = rows;

            _controller = new VirtualTerminalController();
            _controller.MaximumHistoryLines = Math.Min(Math.Max(maxHistory, rows), 2000);
            _controller.ResizeView(columns, rows);
            _controller.SendData += OnControllerSendData;

            _consumer = new DataConsumer(_controller);
            _pre.Columns = columns;
            _pre.Rows = rows;
            _pre.ClipboardWrite = b64 =>
            {
                var h = ClipboardSetRequested;
                if (h != null) { try { h(this, b64); } catch { } }
            };

            _pre.ImageSink = img =>
            {
                if (img == null) return;
                try
                {
                    // 光标位置取自“本批字节还没喂进库”的时刻：够用，且不会因为图在文本之后而错行太多。
                    var p = _controller.ViewPort.CursorPosition;
                    img.CursorColumn = p.Column;
                    img.CursorRow = p.Row;
                    try { img.AbsoluteRow = _controller.ViewPort.TopRow + p.Row; } catch { img.AbsoluteRow = p.Row; }
                }
                catch { }
                var h = ImageReceived;
                if (h != null) { try { h(this, img); } catch { } }
            };

            _pre.ImageDeleteSink = (id, placementOnly) =>
            {
                var h = ImageDeleted;
                if (h != null) { try { h(this, new TerminalImageDeleteEventArgs { ImageId = id, PlacementOnly = placementOnly }); } catch { } }
            };

            _controller.WindowTitleChanged += (s, e) =>
            {
                try
                {
                    var text = e != null ? e.Text : null;
                    if (!string.IsNullOrEmpty(text))
                        TitleChanged?.Invoke(this, text);
                }
                catch { /* ignore title callbacks */ }
            };

        }

        /// <summary>喂入原始 PTY/SSH 字节（UTF-8）。</summary>
        public void Feed(byte[] data)
        {
            if (data == null || data.Length == 0 || _disposed) return;
            try
            {
                lock (_lock)
                {
                    // SGR 归一化：库不认识 “0;38;5;N / 38;5;N;1 / 38:2::r:g:b” 这类合法写法，
                    // 在这里拆成它认的多条 SGR，否则前景会变黑（lib\VtNetCore.dll 无法重编）
                    int n;
                    var normalized = _pre.Process(data, 0, data.Length, out n);
                    // DSR（CSI 6n/5n）的答案必须是“喂到该字节为止”的光标位置：
                    // 按查询出现的位置把输出切段喂库，在分界点算光标再回写。
                    // 纯查询输入（n==0）也要答，所以不能挂在 n > 0 的判断里。
                    _pre.CopyCursorQueries(_dsrAt, _dsrKind);
                    if (n > 0 && _dsrAt.Count == 0)
                    {
                        var slice = new byte[n];
                        Array.Copy(normalized, slice, n);
                        _consumer.Push(slice);
                    }
                    else if (n > 0 || _dsrAt.Count > 0)
                    {
                        int from = 0;
                        var sb = new StringBuilder();
                        for (int q = 0; q < _dsrAt.Count; q++)
                        {
                            int to = Math.Min(_dsrAt[q], n);
                            if (to > from)
                            {
                                var part = new byte[to - from];
                                Array.Copy(normalized, from, part, 0, part.Length);
                                _consumer.Push(part);
                                from = to;
                            }
                            if (_dsrKind[q] == 6)
                            {
                                int row = 0, col = 0;
                                try { var p = _controller.ViewPort.CursorPosition; row = p.Row; col = p.Column; } catch { }
                                sb.Append("\u001b[").Append(row + 1).Append(';').Append(col + 1).Append('R');
                            }
                            else sb.Append("\u001b[0n");
                        }
                        if (n > from)
                        {
                            var tailSlice = new byte[n - from];
                            Array.Copy(normalized, from, tailSlice, 0, tailSlice.Length);
                            _consumer.Push(tailSlice);
                        }
                        var dscr = sb.ToString();
                        if (dscr.Length > 0)
                        {
                            var dh = SendToHost;
                            if (dh != null) dh(this, Encoding.UTF8.GetBytes(dscr));
                        }
                    }
                    var replies = _pre.TakeReplies();
                    if (replies.Length > 0)
                    {
                        var rb = Encoding.UTF8.GetBytes(replies);
                        var handler = SendToHost;
                        if (handler != null) handler(this, rb);
                    }
                    if (_pinnedTop >= 0)
                    {
                        // 回看中：把视口钉回原处，别让新输出把用户拽回底部
                        int top = ClampTopRow(_pinnedTop);
                        if (_controller.ViewPort.TopRow != top) SetTopRow(top);
                        if (top >= MaxTopRow) _pinnedTop = -1;
                    }
                }
            }
            catch (Exception ex)
            {
                // VtNetCore 解析异常不再静默——表现为“输出不动”时这里是唯一线索（限流防刷屏）
                if (_feedErrCount++ % 50 == 0)
                    Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("VtEngine.Feed", ex);
            }
        }

        /// <summary>喂入文本（按 UTF-8 编码）。</summary>
        public void Feed(string text)
        {
            if (string.IsNullOrEmpty(text) || _disposed) return;
            // net46 VtNetCore 1.0.30 公共表面以 Push(byte[]) 为准；Write(string) 在部分构建不可见
            Feed(Encoding.UTF8.GetBytes(text));
        }

        public void ClearChanges()
        {
            lock (_lock) _controller.ClearChanges();
        }

        public void FullReset()
        {
            lock (_lock)
            {
                _pre.Reset();
                _pinnedTop = -1;
                _controller.FullReset();
                _controller.ResizeView(Columns, Rows);
            }
        }

        public void Resize(int columns, int rows)
        {
            if (columns < 2) columns = 2;
            if (rows < 1) rows = 1;
            lock (_lock)
            {
                Columns = columns;
                Rows = rows;
                _pre.Columns = columns;
                _pre.Rows = rows;
                _controller.ResizeView(columns, rows);
            }
        }

        /// <summary>查找并滚动到匹配行（Cell 视口）：从当前页向上/向下逐页扫 scrollback，
        /// 命中则把 TopRow 置到该页并返回屏内行号；未命中返回 -1。
        /// TopRow 写经反射（VirtualTerminalViewPort.set_TopRow，strings 已验证存在）。</summary>
        public int FindAndScrollTo(string text, bool searchDown)
        {
            if (string.IsNullOrEmpty(text) || _disposed) return -1;
            lock (_lock)
            {
                try
                {
                    int rows = Rows;
                    int cols = Columns;
                    int curTop = _controller.ViewPort.TopRow;
                    int maxTop = curTop;
                    // 上限：MaximumHistoryLines 行对应的页数
                    int maxPages = Math.Max(1, MaximumHistoryLines / Math.Max(1, rows) + 1);
                    if (searchDown)
                    {
                        // 向下：当前页先找，未中则逐页往下（不超过当前 top）
                        for (int top = curTop; top <= maxTop; top += rows)
                        {
                            int hit = FindInPage(top, rows, cols, text);
                            if (hit >= 0)
                            {
                                if (top != curTop) SetTopRow(top);
                                return hit;
                            }
                            if (top >= maxTop) break;
                        }
                    }
                    else
                    {
                        // 向上：当前页先找，未中则逐页往上
                        int pages = 0;
                        for (int top = curTop; top >= 0 && pages < maxPages; top -= rows, pages++)
                        {
                            int hit = FindInPage(top, rows, cols, text);
                            if (hit >= 0)
                            {
                                if (top != curTop) SetTopRow(top);
                                return hit;
                            }
                            if (top <= 0) break;
                        }
                    }
                    return -1;
                }
                catch { return -1; }
            }
        }

        private int FindInPage(int top, int rows, int cols, string text)
        {
            var pageRows = _controller.GetPageSpans(top, rows, cols, null);
            if (pageRows == null) return -1;
            int r = 0;
            foreach (var layoutRow in pageRows)
            {
                if (layoutRow != null && layoutRow.Spans != null)
                {
                    var sb = new StringBuilder();
                    foreach (LayoutSpan span in layoutRow.Spans)
                    {
                        if (span == null || span.Hidden) continue;
                        sb.Append(span.Text ?? "");
                    }
                    // 去宽字符占位列再匹配（否则“中文”在库里是 中<fill>文<fill>，搜不到）
                    if (WideText.Strip(sb.ToString()).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                        return r;
                }
                r++;
            }
            return -1;
        }

        /// <summary>视口可用的最大 TopRow（= 末行号 - 可见行数 + 1）。</summary>
        private int MaxTopRow
        {
            get
            {
                try
                {
                    int maxTop = _controller.BottomRow - Rows + 1;
                    return maxTop < 0 ? 0 : maxTop;
                }
                catch { return 0; }
            }
        }

        private int ClampTopRow(int top)
        {
            int maxTop = MaxTopRow;
            if (top > maxTop) top = maxTop;
            if (top < 0) top = 0;
            return top;
        }

        /// <summary>滚动回看（负=向上/看历史，正=向下）。返回新的距底行数，0 表示已在底部。</summary>
        public int ScrollViewport(int lines)
        {
            if (_disposed) return 0;
            lock (_lock)
            {
                try
                {
                    int cur = _controller.ViewPort.TopRow;
                    int target = ClampTopRow(cur + lines);
                    if (target != cur) SetTopRow(target);
                    int maxTop = MaxTopRow;
                    _pinnedTop = (target >= maxTop) ? -1 : target;
                    return maxTop - target;
                }
                catch { return 0; }
            }
        }

        /// <summary>回到实时底部（有新输出时自动跟随）。</summary>
        public void ScrollToBottom()
        {
            if (_disposed) return;
            lock (_lock)
            {
                _pinnedTop = -1;
                try { SetTopRow(MaxTopRow); } catch { }
            }
        }

        /// <summary>视口是否停在底部（用于“新输出到了”提示）。</summary>
        public bool IsAtBottom
        {
            get
            {
                lock (_lock)
                {
                    try { return _controller.ViewPort.TopRow >= MaxTopRow; }
                    catch { return true; }
                }
            }
        }

        private void SetTopRow(int top)
        {
            try
            {
                var vp = _controller.ViewPort;
                if (vp == null) return;
                var prop = vp.GetType().GetProperty("TopRow");
                if (prop != null && prop.CanWrite) prop.SetValue(vp, Math.Max(0, top), null);
            }
            catch { }
        }

        /// <summary>可见区域纯文本（调试/测试用）。</summary>
        public string GetScreenText()
        {
            string raw;
            lock (_lock) raw = _controller.GetScreenText();
            // 去掉宽字符占位列（见 WideText）
            return WideText.Strip(raw ?? string.Empty);
        }

        /// <summary>光标形状（DECSCUSR）：0/1 闪烁块 2 稳态块 3/4 下划线 5/6 竖线。</summary>
        public int CursorShape { get { return _pre.CursorShape; } }

        /// <summary>应用是否请求焦点事件上报（?1004）。</summary>
        public bool FocusReporting { get { return _pre.FocusReporting; } }

        /// <summary>
        /// 焦点变化上报（xterm focus in/out）：应用开了 ?1004 才发 ESC[I / ESC[O。
        /// vim/neovim/tmux 靠它做“离开终端时自动保存”等行为。
        /// </summary>
        public void ReportFocus(bool focused)
        {
            if (_disposed || !_pre.FocusReporting) return;
            var handler = SendToHost;
            if (handler == null) return;
            try { handler(this, Encoding.ASCII.GetBytes(focused ? "\u001b[I" : "\u001b[O")); }
            catch (Exception ex) { Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("VtEngine.ReportFocus", ex); }
        }

        /// <summary>
        /// 抓取可见页为行+着色 span，供 GDI 渲染。
        /// 颜色来自 VtNetCore WebColor (#RRGGBB)，含 256/true-color 路径。
        /// </summary>
        public VtPageSnapshot SnapshotVisible()
        {
            lock (_lock)
            {
                // TopRow 越界会让 GetLine 抛 ArgumentOutOfRange（实测），先把视口拉回来。
                var top = ClampTopRow(_controller.ViewPort.TopRow);
                var rows = _controller.GetPageSpans(top, Rows, Columns, null);
                var page = new VtPageSnapshot
                {
                    Columns = Columns,
                    Rows = Rows,
                    CursorColumn = _controller.ViewPort.CursorPosition.Column,
                    CursorRow = _controller.ViewPort.CursorPosition.Row,
                    ShowCursor = _controller.CursorState.ShowCursor,
                    Lines = new List<VtLineSnapshot>(Rows)
                };

                if (rows == null)
                    return page;

                for (int r = 0; r < rows.Count && r < Rows; r++)
                {
                    var layoutRow = rows[r];
                    var line = new VtLineSnapshot
                    {
                        DoubleWidth = layoutRow != null && layoutRow.DoubleWidth,
                        Spans = new List<VtSpanSnapshot>()
                    };

                    if (layoutRow != null && layoutRow.Spans != null)
                    {
                        foreach (LayoutSpan span in layoutRow.Spans)
                        {
                            if (span == null) continue;
                            line.Spans.Add(new VtSpanSnapshot
                            {
                                Text = span.Text ?? string.Empty,
                                Foreground = ParseWebColor(span.ForgroundColor, Color.FromArgb(204, 204, 204)),
                                Background = ParseWebColor(span.BackgroundColor, Color.Black),
                                Bold = span.Bold,
                                Underline = span.Underline,
                                Hidden = span.Hidden
                            });
                        }
                    }

                    page.Lines.Add(line);
                }

                return page;
            }
        }

        /// <summary>
        /// 键入：优先走控制器 KeyPressed（应用光标键模式），否则 UTF-8 明文。
        /// key 名称与 VtNetCore Keyboard.md 一致，如 "Up","Down","Enter","F1","a"。
        /// </summary>
        public bool KeyPressed(string key, bool control, bool shift)
        {
            if (string.IsNullOrEmpty(key) || _disposed) return false;
            lock (_lock)
            {
                return _controller.KeyPressed(key, control, shift);
            }
        }

        /// <summary>
        /// 鼠标上报改为自己编码，不再交给库。
        ///
        /// 原因（实测 lib 1.0.30）：press 走 ?1006 的 SGR 形态，但 MouseMove 仍发 X10 的
        /// ESC[M + 3 字节；X10 坐标是 32+x 单字节，列号 ≥ 95 就溢出成非法值——宽窗口里
        /// 拖选/拖拽分屏在 tmux 里会点到错位置。自己编码还能统一 release/motion 语义。
        /// </summary>
        public void MousePress(int column, int row, int button, bool control, bool shift)
        {
            SendMouse(MouseAction.Press, column, row, button, control, shift);
        }

        public void MouseRelease(int column, int row, bool control, bool shift)
        {
            SendMouse(MouseAction.Release, column, row, 0, control, shift);
        }

        public void MouseMove(int column, int row, int button, bool control, bool shift)
        {
            SendMouse(MouseAction.Move, column, row, button, control, shift);
        }

        /// <summary>滚轮：button 0=上滚 1=下滚（xterm 码 64/65）。</summary>
        public void MouseWheel(int column, int row, int button, bool control, bool shift)
        {
            SendMouse(MouseAction.Wheel, column, row, button, control, shift);
        }

        private enum MouseAction { Press, Release, Move, Wheel }

        private void SendMouse(MouseAction action, int column, int row, int button, bool control, bool shift)
        {
            if (_disposed) return;
            string seq;
            lock (_lock)
            {
                if (!_pre.MouseTracking) return;
                int b;
                switch (action)
                {
                    case MouseAction.Wheel: b = 64 + (button & 1); break;
                    case MouseAction.Move: b = 32 + (button & 3); break;
                    default: b = button & 3; break;
                }
                if (shift) b += 4;
                if (control) b += 16;
                int x = column + 1;
                int y = row + 1;
                if (x < 1) x = 1;
                if (y < 1) y = 1;

                if (_pre.SgrMouseEncoding)
                {
                    seq = "\u001b[<" + b + ";" + x + ";" + y + (action == MouseAction.Release ? "m" : "M");
                }
                else
                {
                    // X10：ESC[M + (32+b) + (32+x) + (32+y)，单字节坐标上限 223
                    if (x > 223) x = 223;
                    if (y > 223) y = 223;
                    var sb = new StringBuilder(6);
                    sb.Append('\u001b').Append('[').Append('M');
                    sb.Append((char)(32 + (b & 0xff)));
                    sb.Append((char)(32 + x));
                    sb.Append((char)(32 + y));
                    seq = sb.ToString();
                }
            }
            var handler = SendToHost;
            if (handler != null) handler(this, Encoding.UTF8.GetBytes(seq));
        }

        private void OnControllerSendData(object sender, SendDataEventArgs e)
        {
            if (e == null || e.Data == null || e.Data.Length == 0) return;
            var handler = SendToHost;
            if (handler != null)
                handler(this, e.Data);
        }

        /// <summary>WebColor 形如 #RRGGBB 或 #AARRGGBB（VtNetCore 用 ARGB 的低 24 位）。</summary>
        public static Color ParseWebColor(string web, Color fallback)
        {
            if (string.IsNullOrEmpty(web)) return fallback;
            var s = web.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);
            try
            {
                if (s.Length == 6)
                {
                    int rgb = Convert.ToInt32(s, 16);
                    return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                }
                if (s.Length == 8)
                {
                    // AARRGGBB — 忽略 alpha
                    int argb = Convert.ToInt32(s, 16);
                    return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
                }
            }
            catch
            {
                return fallback;
            }
            return fallback;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _controller.SendData -= OnControllerSendData; } catch { }
        }
    }

    public sealed class VtPageSnapshot
    {
        public int Columns { get; set; }
        public int Rows { get; set; }
        public int CursorColumn { get; set; }
        public int CursorRow { get; set; }
        public bool ShowCursor { get; set; }
        public List<VtLineSnapshot> Lines { get; set; }
    }

    public sealed class VtLineSnapshot
    {
        public bool DoubleWidth { get; set; }
        public List<VtSpanSnapshot> Spans { get; set; }
    }

    public sealed class VtSpanSnapshot
    {
        public string Text { get; set; }
        public Color Foreground { get; set; }
        public Color Background { get; set; }
        public bool Bold { get; set; }
        public bool Underline { get; set; }
        public bool Hidden { get; set; }
    }
}
