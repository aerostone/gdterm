using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using Gdterm.Terminal.Rendering.Vt;
using Gdterm.Terminal.Themes;
using Gdterm.Core;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// Phase 0 cell 渲染器：VtTerminalEngine → GDI+ Panel。
    /// 对齐 LightweightRenderer 的 Pause/16ms 节流/双缓冲/刷子缓存，但语义是完整 VT cell grid。
    /// </summary>
    public class CellGdiRenderer : IRenderer, IDisposable
    {
        private readonly DoubleBufferedPanel _canvas;

        /// <summary>图形叠加层（sixel / kitty graphics）。按“绝对行号”锚定，滚动时跟随内容。</summary>
        private sealed class ImageOverlay
        {
            public TerminalImage Image;
            public Bitmap Bitmap;
            public bool DecodeFailed;
            public int ImageId;
            public int PlacementId;
        }

        private const int MaxOverlays = 24;
        private readonly System.Collections.Generic.List<ImageOverlay> _overlays =
            new System.Collections.Generic.List<ImageOverlay>();
        private int _pageTopRow;
        private readonly VtTerminalEngine _engine;
        private readonly object _lock = new object();
        private readonly Dictionary<Color, SolidBrush> _brushCache = new Dictionary<Color, SolidBrush>();

        private Font _font;
        private Font _boldFont;
        private Font _cjkFont;
        private Font _cjkBoldFont;
        /// <summary>终端画布左侧内边距（参考成熟终端客户端的 8-12px 画布留白）。</summary>
        internal const float PadX = 8f;
        /// <summary>终端画布顶部内边距。</summary>
        internal const float PadY = 6f;
        private TerminalColorScheme _scheme;
        private VtPageSnapshot _page;
        // 关键词高亮（HighlightRulePanel 规则；绘制时按列覆盖前景，选区反色优先）
        private HighlightEngine _highlight;
        private bool _isPaused;
        private bool _disposed;
        private bool _needsRedraw;
        private readonly Timer _redrawTimer;
        // 脏行差分：只重画变化的行，长日志刷屏时 CPU 明显降下来
        private VtPageSnapshot _paintedPage;
        private int _paintedTopRow = int.MinValue;
        private bool[] _dirtyRows;
        private bool _fullPaint = true;
        /// <summary>上一帧实际重画的行数（诊断/测试用：1 表示只补了脏行，多行表示整屏）。</summary>
        public int LastPaintedRowCount { get; private set; }
        /// <summary>上一帧快照+差分耗时（ms，诊断用；Paint 耗时 = 帧总耗时 - 此项）。</summary>
        internal long LastSnapshotMs { get; private set; }
        private const int MinRedrawIntervalMs = 16;
        private const string FontName = "Consolas";
        private const float FontSize = 12f;

        private float _charWidth = 8f;
        private float _charHeight = 16f;

        // 字体可观测性：供 UI 在日志中记录实际生效的字体/单元格度量，定位“字体过大/行重叠/被 UI 遮挡”问题
        private string _appliedFontName = FontName;
        private float _appliedFontSizePt = FontSize;
        private float _appliedFontSizePx = 0f;
        private float _appliedDpi = 96f;
        private string _appliedCjkFontName = null;
        private bool _cjkLoadFailed = false;

        /// <summary>真彩 brush 缓存上限，防止 24-bit 颜色刷爆低配内存。</summary>
        private const int MaxBrushCache = 256;

        /// <summary>引擎请求发往主机的字节（DA/键鼠应答等）。</summary>
        public event EventHandler<byte[]> SendToHost;

        /// <summary>应用通过 OSC 52 请求写系统剪贴板；参数是 base64 文本。</summary>
        public event EventHandler<string> ClipboardSetRequested;

        /// <summary>OSC 7：远端 shell 上报的当前目录。</summary>
        public event EventHandler<string> DirectoryChanged;

        /// <summary>OSC 9 / 777 / 99：应用请求的桌面通知。</summary>
        public event EventHandler<TerminalNotification> NotificationReceived;

        /// <summary>本地 cell 尺寸变化（供 UI 通知 SSH window-change）。</summary>
        public event EventHandler TerminalResized;

        public VtTerminalEngine Engine { get { return _engine; } }

        /// <summary>查找跳行：滚动到匹配行，返回屏内行号（-1=未命中）。</summary>
        public int ScrollToMatch(string text, bool searchDown)
        {
            try
            {
                int hit;
                lock (_lock) hit = _engine.FindAndScrollTo(text, searchDown);
                if (hit >= 0)
                {
                    lock (_lock) { _page = _engine.SnapshotVisible(); }
                    try { if (_canvas != null && !_canvas.IsDisposed) _canvas.Invalidate(); } catch { }
                }
                return hit;
            }
            catch { return -1; }
        }

        /// <summary>设置关键词高亮规则（null/空=关闭）。下次重绘即生效。</summary>
        public void SetHighlightRules(System.Collections.Generic.List<Gdterm.Core.Models.HighlightRule> rules)
        {
            lock (_lock)
            {
                if (rules == null || rules.Count == 0) _highlight = null;
                else
                {
                    if (_highlight == null) _highlight = new HighlightEngine();
                    _highlight.LoadRules(rules);
                }
                // 高亮变了必须整屏重画：脏行差分比的是原始快照，看不出"同一行换了颜色"
                ForceFullRepaint();
                if (!_isPaused) { _needsRedraw = true; }
            }
            if (!_isPaused) ScheduleRedraw();
        }

        public int Rows { get { return _engine.Rows; } }
        public int Columns { get { return _engine.Columns; } }

        /// <summary>实际生效的字体名（可观测性，供 UI 日志）。</summary>
        public string AppliedFontName { get { return _appliedFontName; } }
        /// <summary>实际生效的字号（pt，用户语义）。</summary>
        public float AppliedFontSizePt { get { return _appliedFontSizePt; } }
        /// <summary>实际生效的字号（px，已按屏幕 DPI 换算）。</summary>
        public float AppliedFontSizePx { get { return _appliedFontSizePx; } }
        /// <summary>字体测量时的屏幕 DPI（横向）。</summary>
        public float AppliedDpi { get { return _appliedDpi; } }
        /// <summary>实际生效的 CJK 补充字体名（空=未配置；配置了但加载失败时返回 "<名字>(加载失败)"，可观测性）。</summary>
        public string AppliedCjkFontName
        {
            get
            {
                if (string.IsNullOrEmpty(_appliedCjkFontName)) return null;
                return _cjkLoadFailed ? _appliedCjkFontName + L10n.T("(加载失败)") : _appliedCjkFontName;
            }
        }
        /// <summary>单个 ASCII 单元格像素宽。</summary>
        public float CharWidth { get { return _charWidth; } }
        /// <summary>单个单元行像素高。</summary>
        public float CharHeight { get { return _charHeight; } }

        /// <summary>是否为 cell VT 路径（UI 双轨判断）。</summary>
        public bool IsVtCell { get { return true; } }

        /// <summary>应用是否启用了 VT 鼠标上报（vim/less/mc）。UI 据此决定左键是拖选还是透传。</summary>
        public bool IsMouseTrackingEnabled
        {
            get { lock (_lock) return _engine.IsMouseTrackingEnabled; }
        }

        public bool IsBracketedPasteEnabled
        {
            get { lock (_lock) return _engine.IsBracketedPasteEnabled; }
        }

        /// <summary>应用是否请求焦点事件上报（?1004）。</summary>
        public bool FocusReporting
        {
            get { lock (_lock) return _engine.FocusReporting; }
        }

        /// <summary>窗口焦点变化转给引擎（仅在应用开了 ?1004 时真的上报）。</summary>
        public void ReportFocus(bool focused)
        {
            lock (_lock) _engine.ReportFocus(focused);
        }

        // ===== 文本选择（左键拖选 / Shift+左键 扩选） =====
        private bool _hasSelection;
        private int _selStartCol, _selStartRow, _selEndCol, _selEndRow;

        public CellGdiRenderer(int rows = 24, int columns = 80, TerminalColorScheme scheme = null, int maxHistory = 500)
        {
            _scheme = scheme ?? ColorSchemes.Classic;
            _engine = new VtTerminalEngine(columns, rows, maxHistory);
            _engine.SendToHost += (s, data) =>
            {
                var h = SendToHost;
                if (h != null) h(this, data);
            };
            _engine.ClipboardSetRequested += (s, b64) =>
            {
                var h = ClipboardSetRequested;
                if (h != null) h(this, b64);
            };
            // OSC 4 调色板查询：0-15 取当前主题，其余走 xterm 256 公式
            _engine.PaletteLookup = index =>
            {
                var ansi = _scheme != null ? _scheme.AnsiColors : null;
                if (ansi != null && index >= 0 && index < ansi.Length)
                {
                    var c = ansi[index];
                    return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
                }
                return null;
            };
            _engine.DirectoryChanged += (s, path) =>
            {
                var h = DirectoryChanged;
                if (h != null && !string.IsNullOrEmpty(path)) { try { h(this, path); } catch { } }
            };
            _engine.NotificationReceived += (s, n) =>
            {
                var h = NotificationReceived;
                if (h != null && n != null) { try { h(this, n); } catch { } }
            };
            _engine.ImageReceived += OnImageReceived;
            _engine.ImageDeleted += OnImageDeleted;

            PushQueryMetadata();

            _canvas = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = _scheme.Background,
                ForeColor = _scheme.Foreground
            };
            _canvas.Paint += OnPaint;
            _canvas.Resize += OnResize;
            _canvas.Disposed += OnCanvasDisposed;

            // 必须在 _canvas 创建之后：ApplyFont 尾部会 RecomputeGrid 访问 _canvas.ClientSize
            ApplyFont(FontName, FontSize);

            _redrawTimer = new Timer { Interval = MinRedrawIntervalMs };
            _redrawTimer.Tick += OnRedrawTimerTick;

            // 初始空白页
            RefreshSnapshot(force: true);
        }

        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text) || _disposed) return;
            lock (_lock)
            {
                _engine.Feed(text);
                if (!_isPaused)
                {
                    _needsRedraw = true;
                    ScheduleRedraw();
                }
            }
        }

        /// <summary>喂入原始字节（SSH ShellStream 首选）。</summary>
        public void Write(byte[] data)
        {
            if (data == null || data.Length == 0 || _disposed) return;
            lock (_lock)
            {
                _engine.Feed(data);
                if (!_isPaused)
                {
                    _needsRedraw = true;
                    ScheduleRedraw();
                }
            }
        }

        public void Clear()
        {
            if (_disposed) return;
            lock (_lock)
            {
                ClearOverlaysLocked();
                _engine.FullReset();
                ForceFullRepaint();
                RefreshSnapshot(force: true);
                if (!_isPaused)
                {
                    _needsRedraw = true;
                    ScheduleRedraw();
                }
            }
        }

        public Control GetControl()
        {
            return _canvas;
        }

        public string GetSelection()
        {
            if (!_hasSelection) return string.Empty;
            VtPageSnapshot page;
            int sc, sr, ec, er;
            lock (_lock)
            {
                page = _page;
                sc = _selStartCol; sr = _selStartRow;
                ec = _selEndCol; er = _selEndRow;
            }
            return TerminalSelection.ComputeText(page, sc, sr, ec, er);
        }

        /// <summary>全选可视区（右键菜单「全选」）。返回是否有内容可选。</summary>
        public bool SelectAll()
        {
            int sc, sr, ec, er;
            bool ok;
            lock (_lock)
            {
                ok = TerminalSelection.TryComputeSelectAll(_page, out sc, out sr, out ec, out er);
                if (!ok) return false;
                _hasSelection = true;
                _selStartCol = sc; _selStartRow = sr;
                _selEndCol = ec; _selEndRow = er;
                ForceFullRepaint();
                _needsRedraw = true;
            }
            ScheduleRedraw();
            return true;
        }

        /// <summary>左键拖选起点（像素）。返回是否进入选择模式。</summary>
        public bool BeginSelection(int pixelX, int pixelY)
        {
            int col, row;
            if (!TryHitTest(pixelX, pixelY, out col, out row)) return false;
            lock (_lock)
            {
                _hasSelection = true;
                _selStartCol = _selEndCol = col;
                _selStartRow = _selEndRow = row;
                ForceFullRepaint();
                _needsRedraw = true;
            }
            ScheduleRedraw();
            return true;
        }

        /// <summary>拖选过程（像素）。extend=true 表示 Shift+点击扩选。</summary>
        public bool ExtendSelection(int pixelX, int pixelY)
        {
            if (!_hasSelection) return false;
            int col, row;
            if (!TryHitTest(pixelX, pixelY, out col, out row)) return false;
            lock (_lock)
            {
                _selEndCol = col;
                _selEndRow = row;
                ForceFullRepaint();
                _needsRedraw = true;
            }
            ScheduleRedraw();
            return true;
        }

        /// <summary>清选区。</summary>
        public void ClearSelection()
        {
            bool needRedraw;
            lock (_lock)
            {
                needRedraw = _hasSelection;
                _hasSelection = false;
                if (needRedraw) ForceFullRepaint();
                _needsRedraw = needRedraw;
            }
            if (needRedraw) ScheduleRedraw();
        }

        public bool HasSelection { get { lock (_lock) return _hasSelection; } }

        public string[] GetRecentLines(int lineCount)
        {
            var text = GetScreenText();
            if (string.IsNullOrEmpty(text)) return new string[0];
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lineCount <= 0 || lineCount >= lines.Length) return lines;
            var start = lines.Length - lineCount;
            var result = new string[lineCount];
            Array.Copy(lines, start, result, 0, lineCount);
            return result;
        }

        public string GetScreenText()
        {
            lock (_lock) return _engine.GetScreenText();
        }

        public void Pause()
        {
            _isPaused = true;
            try { _redrawTimer.Stop(); } catch { }
        }

        public void Resume()
        {
            if (_disposed) return;
            _isPaused = false;
            lock (_lock)
            {
                // 暂停期间引擎仍在 Feed；恢复时一次性 snapshot，不持续空转
                RefreshSnapshot(force: true);
                _needsRedraw = true;
            }
            ScheduleRedraw();
        }

        public bool TryKeyPressed(string key, bool control, bool shift)
        {
            if (_disposed) return false;
            lock (_lock) return _engine.KeyPressed(key, control, shift);
        }

        public void ResizeTerminal(int columns, int rows)
        {
            if (_disposed) return;
            bool changed = false;
            lock (_lock)
            {
                if (columns != Columns || rows != Rows)
                {
                    ClearOverlaysLocked();   // 行列变了像素锚点失效，图上不再跟着内容走，直接清掉
                    _engine.Resize(columns, rows);
                    changed = true;
                }
                RefreshSnapshot(force: true);
                _needsRedraw = true;
                ScheduleRedraw();
            }
            if (changed)
            {
                var h = TerminalResized;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void MousePress(int column, int row, int button, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _engine.MousePress(column, row, button, control, shift);
        }

        public void MouseRelease(int column, int row, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _engine.MouseRelease(column, row, control, shift);
        }

        public void MouseMove(int column, int row, int button, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _engine.MouseMove(column, row, button, control, shift);
        }

        public void MouseWheel(int column, int row, int button, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _engine.MouseWheel(column, row, button, control, shift);
        }

        /// <summary>滚轮回看历史（负=向上）。</summary>
        public void ScrollViewport(int lines)
        {
            if (_disposed) return;
            lock (_lock) _engine.ScrollViewport(lines);
            ScheduleRedraw();
        }

        /// <summary>回到实时底部。</summary>
        public bool IsAtBottom
        {
            get { lock (_lock) return _engine.IsAtBottom; }
        }

        public void ScrollToBottom()
        {
            if (_disposed) return;
            lock (_lock) _engine.ScrollToBottom();
            ScheduleRedraw();
        }

        /// <summary>像素坐标 → cell 列/行。</summary>
        public bool TryHitTest(int pixelX, int pixelY, out int column, out int row)
        {
            column = 0;
            row = 0;
            if (_charWidth <= 0 || _charHeight <= 0) return false;
            column = Math.Max(0, Math.Min(Columns - 1, (int)((pixelX - PadX) / _charWidth)));
            row = Math.Max(0, Math.Min(Rows - 1, (int)((pixelY - PadY) / _charHeight)));
            return true;
        }

        private void RefreshSnapshot(bool force)
        {
            if (!force && !_engine.Changed) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var page = _engine.SnapshotVisible();
            int top;
            try { top = _engine.ViewportTopRow; } catch { top = 0; }
            _page = page;
            _pageTopRow = top;
            ComputeDirty(page, top);
            _engine.ClearChanges();
            LastSnapshotMs = (long)sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// 与「已画到屏幕上的那一帧」比较，标出变化行。
        /// 宁可退化成整屏（滚动/换字号/有图形叠加/行数变化），也不能画错。
        /// </summary>
        private void ComputeDirty(VtPageSnapshot page, int top)
        {
            if (_paintedPage == null || _paintedPage.Lines == null || page == null || page.Lines == null
                || _paintedPage.Lines.Count != page.Lines.Count
                || _paintedTopRow != top
                || _overlays.Count > 0)
            {
                ForceFullRepaint();
                return;
            }
            int n = page.Lines.Count;
            // 复用同一个数组并清零：每帧 new bool[n] 会在长日志刷屏时制造无谓 GC 压力
            if (_dirtyRows == null || _dirtyRows.Length != n) _dirtyRows = new bool[n];
            else Array.Clear(_dirtyRows, 0, n);
            for (int r = 0; r < n; r++)
            {
                if (!LineEquals(_paintedPage.Lines[r], page.Lines[r])) _dirtyRows[r] = true;
            }
            // 光标所在行（新旧各一行）必须重画，否则会留下残影
            MarkCursorRow(page, n);
            MarkCursorRow(_paintedPage, n);
        }

        private void MarkCursorRow(VtPageSnapshot page, int rows)
        {
            if (page == null || !page.ShowCursor) return;
            if (page.CursorRow < 0 || page.CursorRow >= rows) return;
            if (_dirtyRows != null) _dirtyRows[page.CursorRow] = true;
        }

        private static bool LineEquals(VtLineSnapshot a, VtLineSnapshot b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.DoubleWidth != b.DoubleWidth) return false;
            var sa = a.Spans;
            var sb = b.Spans;
            int na = sa == null ? 0 : sa.Count;
            int nb = sb == null ? 0 : sb.Count;
            if (na != nb) return false;
            for (int k = 0; k < na; k++)
            {
                var x = sa[k];
                var y = sb[k];
                if (ReferenceEquals(x, y)) continue;
                if (x == null || y == null) return false;
                if (!string.Equals(x.Text, y.Text, StringComparison.Ordinal)) return false;
                if (x.Foreground != y.Foreground) return false;
                if (x.Background != y.Background) return false;
                if (x.Bold != y.Bold || x.Underline != y.Underline || x.Hidden != y.Hidden) return false;
            }
            return true;
        }

        /// <summary>下一帧整屏重画（选区/字体/图形/滚动变化都走这里）。</summary>
        private void ForceFullRepaint()
        {
            _paintedPage = null;
            _paintedTopRow = int.MinValue;
            _dirtyRows = null;
            _fullPaint = true;
        }

        /// <summary>取屏内某一行文本（Ctrl+Click 识别链接/路径用；已去宽字符占位列）。</summary>
        public string GetRowText(int row)
        {
            VtPageSnapshot page;
            lock (_lock) page = _page;
            if (page == null || page.Lines == null || row < 0 || row >= page.Lines.Count) return string.Empty;
            var line = page.Lines[row];
            int len = TerminalSelection.LineTextLength(line);
            return len <= 0 ? string.Empty : WideText.Strip(TerminalSelection.ExtractSubstring(line, 0, len));
        }

        private void ScheduleRedraw()
        {
            if (_isPaused || _disposed) return;
            try
            {
                if (!_redrawTimer.Enabled)
                    _redrawTimer.Start();
            }
            catch { }
        }

        private void OnRedrawTimerTick(object sender, EventArgs e)
        {
            if (_disposed || _isPaused)
            {
                try { _redrawTimer.Stop(); } catch { }
                return;
            }

            bool paint = false;
            lock (_lock)
            {
                if (_needsRedraw || _engine.Changed)
                {
                    RefreshSnapshot(force: true);
                    _needsRedraw = false;
                    paint = true;
                }
                else
                {
                    try { _redrawTimer.Stop(); } catch { }
                }
            }

            // 生产路径保持异步 Invalidate（0.1.395 教训：tick 内同步 Update() 在
            // DoEvents 泵环境有重入风险，与基准的同步帧路径刻意分开）。
            if (paint && !_canvas.IsDisposed)
            {
                try { _canvas.Invalidate(); } catch { }
            }
        }

        /// <summary>
        /// 同步快照帧：快照 + 脏行差分 + 异步 Invalidate（绘制交给消息泵/定时器合并，
        /// 不在本方法内同步 Update——0.1.395/396 教训：基准的 parentless 窗口在
        /// Update()→SendMessage(WM_PAINT) 上硬崩，绘制统一走消息队列最安全）。
        /// 生产路径仍走定时器合并（一帧内多次 Write 只画一次）。
        /// </summary>
        internal void RenderFrameNow()
        {
            if (_disposed || _isPaused) return;
            bool paint;
            lock (_lock)
            {
                if (_needsRedraw || _engine.Changed)
                {
                    RefreshSnapshot(force: true);
                    _needsRedraw = false;
                    paint = true;
                }
                else paint = false;
            }
            if (paint && !_canvas.IsDisposed)
            {
                try { _canvas.Invalidate(); } catch { }
            }
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            if (_disposed) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            VtPageSnapshot page;
            bool hasSel;
            int sc, sr, ec, er;
            lock (_lock)
            {
                page = _page;
                hasSel = _hasSelection;
                sc = _selStartCol; sr = _selStartRow;
                ec = _selEndCol; er = _selEndRow;
            }

            bool full;
            bool[] dirty;
            lock (_lock)
            {
                full = _fullPaint;
                dirty = _dirtyRows;
            }
            if (full) g.Clear(_scheme.Background);
            if (page == null || page.Lines == null) return;

            // 选区规范化（上→下、左→右）
            if (hasSel && (er < sr || (er == sr && ec < sc)))
            {
                int t = sc; sc = ec; ec = t;
                t = sr; sr = er; er = t;
            }

            float y = PadY;
            int paintedRows = 0;
            for (int r = 0; r < page.Lines.Count; r++)
            {
                // 脏行差分：没变化的行整段跳过（背景上一帧已经画对）
                if (!full && (dirty == null || r >= dirty.Length || !dirty[r]))
                {
                    y += _charHeight;
                    continue;
                }
                if (!full) ClearRowBand(g, y);
                paintedRows++;
                var line = page.Lines[r];
                float x = PadX;
                // 该行选区起止列（不含则 -1）
                int selStart = -1, selEnd = -1;
                if (hasSel && r >= sr && r <= er)
                {
                    selStart = (r == sr) ? sc : 0;
                    selEnd = (r == er) ? ec : int.MaxValue;
                }
                int colCursor = 0;
                // 行高亮：拼行文本取首匹配列区间（选区段不染色）
                int hlStart = -1, hlLen = 0;
                System.Drawing.Color hlColor = System.Drawing.Color.Empty;
                bool hlBold = false;
                try
                {
                    var hl = _highlight;
                    if (hl != null && line != null && line.Spans != null)
                    {
                        var sb = new StringBuilder();
                        foreach (var sp in line.Spans)
                        {
                            if (sp == null || sp.Hidden) continue;
                            sb.Append(sp.Text ?? "");
                        }
                        // 去掉宽字符占位列再匹配，命中下标换算回模型列（占位列算 1 列）
                        string lineText = WideText.Strip(sb.ToString());
                        if (lineText.Length > 0)
                        {
                            var matches = hl.MatchLine(lineText);
                            if (matches != null)
                            {
                                foreach (var m in matches)
                                {
                                    if (m == null || m.Length <= 0) continue;
                                    if (m.StartIndex < 0 || m.StartIndex + m.Length > lineText.Length) continue;
                                    if (m.Foreground.IsEmpty) continue;
                                    hlStart = WideText.Columns(lineText.Substring(0, m.StartIndex));
                                    hlLen = WideText.Columns(lineText.Substring(m.StartIndex, m.Length));
                                    hlColor = m.Foreground; hlBold = m.Bold;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { hlStart = -1; }
                if (line != null && line.Spans != null)
                {
                    foreach (var span in line.Spans)
                    {
                        if (span == null || span.Hidden || string.IsNullOrEmpty(span.Text))
                        {
                            if (span != null && !string.IsNullOrEmpty(span.Text))
                            {
                                x += span.Text.Length * _charWidth;
                                colCursor += span.Text.Length;
                            }
                            continue;
                        }

                        var font = span.Bold ? _boldFont : _font;
                        // CJK 补充字体（非 ASCII 段用 cjk 字体绘制，Xshell 风格双字体）
                        var cjkFont = span.Bold ? _cjkBoldFont : _cjkFont;

                        // 选区按列裁剪成最多三段（前/选中/后）：
                        // 旧实现整 span 命中即整段反色，单击（起点==终点）落在 span 中间
                        // 也会高亮整段 —— 看起来“一点就选中整行”
                        int spanLen = span.Text.Length;
                        int selS = -1, selE = -1;
                        if (selStart >= 0)
                        {
                            // 注意不能声明局部 s/e：外层 OnPaint(PaintEventArgs e) 在 C# 7.3 下禁止遮蔽
                            int from = Math.Max(selStart, colCursor);
                            int to = Math.Min(selEnd, colCursor + spanLen);
                            if (to > from) { selS = from - colCursor; selE = to - colCursor; }
                        }

                        if (selS < 0)
                        {
                            Font hlFont = font;
                            System.Drawing.Color hlFg = span.Foreground;
                            if (hlStart >= 0 && colCursor + spanLen > hlStart && colCursor < hlStart + hlLen)
                            {
                                hlFg = hlColor;
                                if (hlBold) hlFont = span.Bold ? font : _boldFont;
                            }
                            DrawSpanCell(g, span.Text, hlFont, cjkFont,
                                span.Background, hlFg, span.Underline, false, ref x, y);
                        }
                        else
                        {
                            if (selS > 0)
                            {
                                Font hlFont3 = font;
                                System.Drawing.Color hlFg3 = span.Foreground;
                                if (hlStart >= 0 && colCursor + selS > hlStart && colCursor < hlStart + hlLen)
                                {
                                    hlFg3 = hlColor;
                                    if (hlBold) hlFont3 = span.Bold ? font : _boldFont;
                                }
                                DrawSpanCell(g, span.Text.Substring(0, selS), hlFont3, cjkFont,
                                    span.Background, hlFg3, span.Underline, false, ref x, y);
                            }
                            DrawSpanCell(g, span.Text.Substring(selS, selE - selS), font, cjkFont,
                                _scheme.SelectionBackground, _scheme.SelectionForeground, span.Underline, true, ref x, y);
                            if (selE < spanLen)
                            {
                                // 后段列偏移 colCursor+selE：与匹配区间相交则染
                                Font hlFont2 = font;
                                System.Drawing.Color hlFg2 = span.Foreground;
                                if (hlStart >= 0 && colCursor + spanLen > hlStart && colCursor + selE < hlStart + hlLen)
                                {
                                    hlFg2 = hlColor;
                                    if (hlBold) hlFont2 = span.Bold ? font : _boldFont;
                                }
                                DrawSpanCell(g, span.Text.Substring(selE), hlFont2, cjkFont,
                                    span.Background, hlFg2, span.Underline, false, ref x, y);
                            }
                        }

                        colCursor += spanLen;
                    }
                }

                y += _charHeight;
            }

            if (page.ShowCursor)
            {
                float cx = PadX + page.CursorColumn * _charWidth;
                float cy = PadY + page.CursorRow * _charHeight;
                // 半透明整格光标：可见且不删除字符（密度打磨）
                // 形状跟随 DECSCUSR（vim 插入模式的竖线/下划线光标）
                Color cc = _scheme.CursorColor;
                int shape = _engine.CursorShape;
                bool thinShape = shape >= 3;          // 3/4 下划线、5/6 竖线
                // 整格光标保持半透明（不遮挡字符，密度打磨过的观感）；细线只有几个像素，必须够实才看得见
                using (var cursorBrush = new SolidBrush(Color.FromArgb(thinShape ? 235 : 110, cc.R, cc.G, cc.B)))
                {
                    float thin = Math.Max(2f, _charHeight / 8f);
                    if (shape == 3 || shape == 4)
                        g.FillRectangle(cursorBrush, cx, cy + _charHeight - thin, _charWidth, thin);
                    else if (shape == 5 || shape == 6)
                        g.FillRectangle(cursorBrush, cx, cy, Math.Max(2f, _charWidth / 6f), _charHeight);
                    else
                        g.FillRectangle(cursorBrush, cx, cy, _charWidth, _charHeight);
                }
            }

            DrawOverlays(g, _pageTopRow);

            lock (_lock)
            {
                _paintedPage = page;
                _paintedTopRow = _pageTopRow;
                // 数组留着复用（ComputeDirty 会先清零），只在整屏重画时丢弃
                _fullPaint = false;
                LastPaintedRowCount = paintedRows;
            }
        }

        /// <summary>只清单行背景（局部重画前先擦掉这一行，含左右留白）。</summary>
        private void ClearRowBand(Graphics g, float y)
        {
            try
            {
                using (var bg = new SolidBrush(_scheme.Background))
                    g.FillRectangle(bg, 0f, y - 1f, _canvas.Width, _charHeight + 2f);
            }
            catch { }
        }

        // ───────────────────────── 图形（sixel / kitty graphics）─────────────────────────

        private void OnImageReceived(object sender, TerminalImage img)
        {
            if (_disposed || img == null) return;
            try
            {
                lock (_lock)
                {
                    // 同一 (imageId, placementId) 再放置一次就替换，避免无限堆积
                    for (int k = _overlays.Count - 1; k >= 0; k--)
                    {
                        var o = _overlays[k];
                        if (o.ImageId == img.ImageId && o.PlacementId == img.PlacementId && img.ImageId != 0)
                        {
                            try { if (o.Bitmap != null) o.Bitmap.Dispose(); } catch { }
                            _overlays.RemoveAt(k);
                        }
                    }
                    while (_overlays.Count >= MaxOverlays)
                    {
                        var old = _overlays[0];
                        try { if (old.Bitmap != null) old.Bitmap.Dispose(); } catch { }
                        _overlays.RemoveAt(0);
                    }
                    _overlays.Add(new ImageOverlay
                    {
                        Image = img,
                        ImageId = img.ImageId,
                        PlacementId = img.PlacementId
                    });
                }
                if (!_isPaused)
                {
                    _needsRedraw = true;
                    ScheduleRedraw();
                }
            }
            catch (System.Exception ex) { Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("CellGdiRenderer.ImageReceived", ex); }
        }

        private void OnImageDeleted(object sender, TerminalImageDeleteEventArgs e)
        {
            if (_disposed || e == null) return;
            try
            {
                lock (_lock)
                {
                    for (int k = _overlays.Count - 1; k >= 0; k--)
                    {
                        var o = _overlays[k];
                        bool hit = e.ImageId == 0 || o.ImageId == e.ImageId;
                        if (!hit) continue;
                        if (e.PlacementOnly && e.ImageId == 0) continue;  // 只删“当前放置”时要求带 id
                        try { if (o.Bitmap != null) o.Bitmap.Dispose(); } catch { }
                        _overlays.RemoveAt(k);
                    }
                }
                if (!_isPaused) { _needsRedraw = true; ScheduleRedraw(); }
            }
            catch (System.Exception ex) { Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("CellGdiRenderer.ImageDeleted", ex); }
        }

        private void ClearOverlaysLocked()
        {
            for (int k = 0; k < _overlays.Count; k++)
            {
                try { if (_overlays[k].Bitmap != null) _overlays[k].Bitmap.Dispose(); } catch { }
            }
            _overlays.Clear();
        }

        /// <summary>画出所有可见叠加图；纹理按需解码（解码必须在绘制线程，且失败只影响这张图）。</summary>
        private void DrawOverlays(Graphics g, int topRow)
        {
            if (_overlays.Count == 0) return;
            ImageOverlay[] snapshot;
            lock (_lock) snapshot = _overlays.ToArray();
            if (snapshot.Length == 0) return;

            int prevInterp = (int)g.InterpolationMode;
            try { g.InterpolationMode = InterpolationMode.NearestNeighbor; } catch { }
            try
            {
                foreach (var o in snapshot)
                {
                    var img = o.Image;
                    if (img == null) continue;
                    if (o.Bitmap == null && !o.DecodeFailed) o.Bitmap = DecodeImage(img, ref o.DecodeFailed);
                    if (o.Bitmap == null) continue;

                    int row = img.AbsoluteRow - topRow;
                    if (row + 4 < 0 || row > Rows + 4) continue;      // 图整体在视口外，跳过

                    float x = PadX + img.CursorColumn * _charWidth + img.OffsetX;
                    float y = PadY + row * _charHeight + img.OffsetY;
                    float w = img.Columns > 0 ? img.Columns * _charWidth : img.Width;
                    float h = img.Rows > 0 ? img.Rows * _charHeight : img.Height;
                    if (w <= 0 || h <= 0) continue;
                    g.DrawImage(o.Bitmap, x, y, w, h);
                }
            }
            catch (System.Exception ex) { Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("CellGdiRenderer.DrawOverlays", ex); }
            finally { try { g.InterpolationMode = (InterpolationMode)prevInterp; } catch { } }
        }

        /// <summary>PNG（kitty f=100）交给 GDI；原始 RGBA/RGB 手工转 BGRA 位图。</summary>
        private Bitmap DecodeImage(TerminalImage img, ref bool failed)
        {
            try
            {
                if (img.Format == 100)
                {
                    using (var ms = new System.IO.MemoryStream(img.Rgba, 0, img.Rgba.Length))
                        return new Bitmap(ms);
                }
                if (img.Rgba == null || img.Width <= 0 || img.Height <= 0) { failed = true; return null; }

                var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    var buf = new byte[stride * img.Height];
                    for (int yy = 0; yy < img.Height; yy++)
                    {
                        int src = yy * img.Width * 4;
                        int dst = yy * stride;
                        for (int xx = 0; xx < img.Width; xx++, src += 4, dst += 4)
                        {
                            buf[dst + 0] = img.Rgba[src + 2];   // B
                            buf[dst + 1] = img.Rgba[src + 1];   // G
                            buf[dst + 2] = img.Rgba[src + 0];   // R
                            buf[dst + 3] = img.Rgba[src + 3];   // A
                        }
                    }
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
            catch (System.Exception ex)
            {
                Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("CellGdiRenderer.DecodeImage", ex);
                failed = true;
                return null;
            }
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (_disposed || _canvas.ClientSize.Width <= 0 || _canvas.ClientSize.Height <= 0) return;
            MeasureCell();
            RecomputeGrid();
        }

        /// <summary>
        /// 按当前画布与单元格度量重算列行数；变化时走 ResizeTerminal（引擎重排 + 通知会话同步 PTY）。
        /// 字体变化后必须调用——否则旧网格 × 新字宽溢出画布右侧/底部，或内容缩在角落。
        /// </summary>
        private void RecomputeGrid()
        {
            // _canvas 在构造函数尾部才创建；ApplyFont 可能在其之前被调用（ctor 内），此时跳过，
            // 等首次 OnResize 再算网格 —— 否则 NRE 杀死整个渲染器构造链（SSH/本地终端全部打不开）
            if (_disposed || _canvas == null || _canvas.ClientSize.Width <= 0 || _canvas.ClientSize.Height <= 0) return;
            // 减去两侧 padding 再计算 cols/rows — 否则右侧/底部边缘会有半截字被裁
            int cols = Math.Max(2, (int)((_canvas.ClientSize.Width - PadX * 2) / _charWidth));
            int rows = Math.Max(1, (int)((_canvas.ClientSize.Height - PadY * 2) / _charHeight));
            if (cols != Columns || rows != Rows)
                ResizeTerminal(cols, rows);
        }

        public void ApplyFont(string fontName, float fontSize)
        {
            ApplyFont(fontName, fontSize, null);
        }

        /// <param name="cjkFontName">Xshell 风格的非 ASCII 补充字体；空则不分割。</param>
        /// <param name="fontSize">pt 单位（与 UI 控件一致），内部按实际屏幕 DPI 转为 px 创建字体。</param>
        public void ApplyFont(string fontName, float fontSize, string cjkFontName)
        {
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(fontName)) fontName = FontName;
            if (fontSize < 8f) fontSize = 8f;
            if (fontSize > 36f) fontSize = 36f;
            ForceFullRepaint();   // 字体变化会改变每格像素尺寸：必须整屏重画
            // pt → px 按实际屏幕 DPI 转换，非 96 硬编码（4K/高 DPI 下字体大小才一致）
            float dpiX;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
                dpiX = g.DpiX;
            var fontSizePx = fontSize * dpiX / 72f;
            lock (_lock)
            {
                try { if (_font != null) _font.Dispose(); } catch { }
                try { if (_boldFont != null) _boldFont.Dispose(); } catch { }
                try { if (_cjkFont != null) _cjkFont.Dispose(); } catch { }
                try { if (_cjkBoldFont != null) _cjkBoldFont.Dispose(); } catch { }
                try
                {
                    _font = new Font(fontName, fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
                }
                catch
                {
                    _font = new Font(FontName, fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
                }
                try { _boldFont = new Font(_font.FontFamily, fontSizePx, FontStyle.Bold, GraphicsUnit.Pixel); }
                catch { _boldFont = new Font(_font, FontStyle.Bold); }

                // CJK 补充字体（可空）—— 加载失败不再静默：记入日志并标记到 AppliedCjkFontName
                _cjkLoadFailed = false;
                if (!string.IsNullOrWhiteSpace(cjkFontName))
                {
                    try { _cjkFont = new Font(cjkFontName, fontSizePx, FontStyle.Regular, GraphicsUnit.Pixel); }
                    catch (Exception cjkEx)
                    {
                        _cjkFont = null;
                        _cjkLoadFailed = true;
                        Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("CellGdiRenderer.CjkFont", cjkEx);
                        Gdterm.Terminal.Diagnostics.TerminalLog.Info("CellGdiRenderer.CjkFont",
                            "CJK 字体加载失败，非 ASCII 回退主字体 name=" + cjkFontName + " px=" + fontSizePx.ToString("0.#"));
                    }
                    if (_cjkFont != null)
                    {
                        try { _cjkBoldFont = new Font(_cjkFont.FontFamily, fontSizePx, FontStyle.Bold, GraphicsUnit.Pixel); }
                        catch { try { _cjkBoldFont = new Font(_cjkFont, FontStyle.Bold); } catch { _cjkBoldFont = null; } }
                    }
                    else
                    {
                        _cjkBoldFont = null;
                    }
                }
                else
                {
                    _cjkFont = null;
                    _cjkBoldFont = null;
                }

                // 记录实际生效度量（供 UI 在日志中输出，定位字体过大/重叠）
                _appliedFontName = fontName;
                _appliedFontSizePt = fontSize;
                _appliedFontSizePx = fontSizePx;
                _appliedDpi = dpiX;
                _appliedCjkFontName = string.IsNullOrWhiteSpace(cjkFontName) ? null : cjkFontName;

                MeasureCell();
                _needsRedraw = true;
                ScheduleRedraw();
            }
            // 字体变了 → 单元格尺寸变了 → 网格必须按当前画布重算并同步 PTY，
            // 否则调大字号内容溢出组件、调小则缩在左上角（用户报告的“改字号就超出/错位”）。
            RecomputeGrid();
        }

        /// <summary>
        /// 测量 span 实宽（ASCII 主字体 + CJK 字体分段测量之和，与 DrawSpanText 的绘制宽度一致）。
        /// 绘制前先测量：背景矩形需要宽度才能填充，而背景必须画在文字下面。
        /// </summary>
        /// <summary>
        /// 绘制一个（可能被选区裁出的）文本段：先背景后文字。
        /// 选区背景色即使是纯黑（0,0,0）也要填充（forceFill），否则选区在黑底主题下不可见。
        /// </summary>
        private void DrawSpanCell(Graphics g, string text, Font font, Font cjkFont,
                                  Color bg, Color fg, bool underline, bool forceFill, ref float x, float y)
        {
            if (string.IsNullOrEmpty(text)) return;
            // 双字体宽度：实际宽度以 MeasureSpanWidth 为准（取测量值与 cell 宽度较大者）
            float w = MeasureSpanWidth(g, text, font, cjkFont);
            w = Math.Max(w, text.Length * _charWidth);
            if (forceFill || (bg.A > 0 && (bg.R | bg.G | bg.B) != 0))
                g.FillRectangle(GetBrush(bg), x, y, w, _charHeight);
            DrawSpanText(g, text, font, cjkFont, GetBrush(fg), x, y, underline, fg);
            x += w;
        }

        private float MeasureSpanWidth(Graphics g, string text, Font mainFont, Font cjkFont)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var fmt = StringFormat.GenericTypographic;
            // 没有 cjk 字体 -> 一次性测完
            if (cjkFont == null)
            {
                return g.MeasureString(text, mainFont, int.MaxValue, fmt).Width;
            }

            // 按 ASCII/非 ASCII 分段测量，CJK 段末尾对齐 cell 网格（与 DrawSpanText 一致）
            float cx = 0;
            var asciiBuf = new System.Text.StringBuilder();
            var cjkBuf = new System.Text.StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                bool isLast = (i == text.Length);
                char ch = isLast ? '\0' : text[i];
                bool isAscii = !isLast && ch < 0x80;

                if (isAscii)
                    asciiBuf.Append(ch);
                else if (!isLast && ch != WideText.Filler)
                    cjkBuf.Append(ch);   // 占位列不画（宽字符字形本身占 2 格）

                bool segBreak = isLast || (i + 1 < text.Length && ((text[i + 1] < 0x80) != isAscii));
                if (segBreak || isLast)
                {
                    if (asciiBuf.Length > 0)
                    {
                        var s = asciiBuf.ToString();
                        cx += g.MeasureString(s, mainFont, int.MaxValue, fmt).Width;
                        asciiBuf.Clear();
                    }
                    if (cjkBuf.Length > 0)
                    {
                        var s = cjkBuf.ToString();
                        cx += g.MeasureString(s, cjkFont, int.MaxValue, fmt).Width;
                        cjkBuf.Clear();
                        // CJK 字形占 2 cell —— 对齐 cell 网格，与 DrawSpanText 保持一致
                        cx = (float)Math.Ceiling(cx / _charWidth) * _charWidth;
                    }
                }
            }
            return cx;
        }

        /// <summary>
        /// 绘制 span 文本（ASCII / 非 ASCII 分段双字体，Xshell 风格）。
        /// 没有 cjk 字体时退化为全部用主字体画。背景已由调用方填充。
        /// </summary>
        /// <returns>实际渲染的宽度（ASCII 段主字体 + CJK 段 cjk 字体之和），用于下划线。</returns>
        private float DrawSpanText(Graphics g, string text, Font mainFont, Font cjkFont,
                                 Brush brush, float x, float y, bool underline, Color underlineColor)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var fmt = StringFormat.GenericTypographic;
            float measuredW;
            // 没有 cjk 字体 -> 一次性画完
            if (cjkFont == null)
            {
                measuredW = g.MeasureString(text, mainFont, int.MaxValue, fmt).Width;
                g.DrawString(text, mainFont, brush, x, y, fmt);
                if (underline)
                {
                    using (var pen = new Pen(underlineColor))
                        g.DrawLine(pen, x, y + _charHeight - 1, x + Math.Max(measuredW, text.Length * _charWidth), y + _charHeight - 1);
                }
                return Math.Max(measuredW, text.Length * _charWidth);
            }

            // 按字符遍历分段：ASCII 用主字体，其他用 cjk 字体
            float cx = x;
            var asciiBuf = new System.Text.StringBuilder();
            var cjkBuf = new System.Text.StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                bool isLast = (i == text.Length);
                char ch = isLast ? '\0' : text[i];
                bool isAscii = !isLast && ch < 0x80;

                if (isAscii)
                    asciiBuf.Append(ch);
                else if (!isLast && ch != WideText.Filler)
                    cjkBuf.Append(ch);   // 占位列不画（宽字符字形本身占 2 格）

                // 段间刷新
                bool segBreak = isLast || (i + 1 < text.Length && ((text[i + 1] < 0x80) != isAscii));
                if (segBreak || isLast)
                {
                    if (asciiBuf.Length > 0)
                    {
                        var s = asciiBuf.ToString();
                        g.DrawString(s, mainFont, brush, cx, y, fmt);
                        cx += g.MeasureString(s, mainFont, int.MaxValue, fmt).Width;
                        asciiBuf.Clear();
                    }
                    if (cjkBuf.Length > 0)
                    {
                        var s = cjkBuf.ToString();
                        g.DrawString(s, cjkFont, brush, cx, y, fmt);
                        cx += g.MeasureString(s, cjkFont, int.MaxValue, fmt).Width;
                        cjkBuf.Clear();
                        // CJK 字形占 2 cell —— 对齐 cell 网格，避免后续 ASCII 紧贴汉字
                        cx = x + (float)Math.Ceiling((cx - x) / _charWidth) * _charWidth;
                    }
                }
            }

            if (underline)
            {
                using (var pen = new Pen(underlineColor))
                    g.DrawLine(pen, x, y + _charHeight - 1, x + Math.Max(cx - x, text.Length * _charWidth), y + _charHeight - 1);
            }
            return Math.Max(cx - x, text.Length * _charWidth);
        }

        /// <summary>
        /// 把主题色与单元像素尺寸推给引擎的查询应答器（OSC 10/11/12/4、CSI 14t/16t）。
        /// 换主题、换字体、改字号后都要调一次。
        /// </summary>
        /// <summary>把 TerminalProfile 里的 TERM 名同步到引擎（XTVERSION / XTGETTCAP TN 用）。</summary>
        public void ApplyTerminalType(string term)
        {
            if (_disposed || string.IsNullOrWhiteSpace(term)) return;
            try { _engine.TerminalName = term.Trim(); } catch (System.Exception ex) { Gdterm.Terminal.Diagnostics.TerminalLog.Swallowed("CellGdiRenderer.ApplyTerminalType", ex); }
        }

        public void PushQueryMetadata()
        {
            try
            {
                string fg = ToHex(_scheme != null ? _scheme.Foreground : Color.Silver);
                string bg = ToHex(_scheme != null ? _scheme.Background : Color.Black);
                string cur = ToHex(_scheme != null ? _scheme.CursorColor : Color.White);
                _engine.SetQueryMetadata(fg, bg, cur,
                    (int)Math.Round(_charWidth), (int)Math.Round(_charHeight));
            }
            catch { }   // 元信息只是应答用，失败不影响渲染
        }

        private static string ToHex(Color c)
        {
            return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }

        private void MeasureCell()
        {
            if (_font == null) return;
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var size = g.MeasureString("W", _font, int.MaxValue, StringFormat.GenericTypographic);
                // 关键：cell 宽取字体精确步进（不取整）。
                // 若 Ceiling 取整：文字按自然步进批量绘制，光标/选区按 cell 网格定位，
                // 每字符错开零点几像素，行尾累积到 1-2 字符宽 —— 光标看起来和文字隔了空格。
                _charWidth = Math.Max(6f, size.Width);
                _charHeight = Math.Max(12f, (float)Math.Ceiling(_font.GetHeight(g)) + 2f);
            }
            PushQueryMetadata();
        }

        private SolidBrush GetBrush(Color c)
        {
            SolidBrush b;
            if (_brushCache.TryGetValue(c, out b))
                return b;

            // 真彩场景颜色种类可能很大：超限时整表重建，避免无限增长
            if (_brushCache.Count >= MaxBrushCache)
            {
                foreach (var old in _brushCache.Values)
                {
                    try { old.Dispose(); } catch { }
                }
                _brushCache.Clear();
            }

            b = new SolidBrush(c);
            _brushCache[c] = b;
            return b;
        }

        private void OnCanvasDisposed(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _redrawTimer.Stop(); _redrawTimer.Dispose(); } catch { }
            try { lock (_lock) ClearOverlaysLocked(); } catch { }
            try { _engine.Dispose(); } catch { }
            try { _font.Dispose(); } catch { }
            try { _boldFont.Dispose(); } catch { }
            foreach (var b in _brushCache.Values)
            {
                try { b.Dispose(); } catch { }
            }
            _brushCache.Clear();
        }

        /// <summary>
        /// 与 LightweightRenderer 一致的双缓冲 Panel。
        /// 默认 Panel 的 Selectable=false，拿不到焦点 —— 必需显式打开，
        /// 否则 OnKeyPress/OnKeyDown 不会路由进来，键盘输入静默丢失。
        /// </summary>
        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.Selectable, true);
                // 终端区域内鼠标应为文本输入光标（I-beam），而非默认箭头
                Cursor = Cursors.IBeam;
                TabStop = true;
                UpdateStyles();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (!Focused) Focus();
                base.OnMouseDown(e);
            }
        }
    }
}
