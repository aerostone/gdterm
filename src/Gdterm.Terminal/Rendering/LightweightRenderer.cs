using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Gdterm.Terminal.Themes;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// 轻量级 GDI+ 终端渲染器——零 GPU 依赖，零内存泄漏设计
    /// 
    /// 设计原则：
    ///   1. 无 GPU 依赖 — 纯 GDI+ 软件渲染，兼容无显卡/旧显卡机器
    ///   2. 无持续渲染 — 按需重绘，只在有新数据时 Invalidate，空闲时零 CPU
    ///   3. 无内存泄漏 — Font/Brush/Region 全部缓存复用，Dispose 彻底
    ///   4. 节流机制 — 高频输出时合并重绘，避免卡顿
    ///   5. Double Buffer — Panel 双缓冲，消除闪烁
    /// </summary>
    public class LightweightRenderer : IRenderer
    {
        private readonly DoubleBufferedPanel _canvas;
        private readonly List<ColoredLine> _lineBuffer = new List<ColoredLine>();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly List<ColorSpan> _currentSpans = new List<ColorSpan>();
        private readonly object _lock = new object();

        // 缓存的 GDI 对象 — 全生命周期复用，Dispose 时统一释放
        private Font _font;
        private Font _boldFont;
        private readonly Dictionary<Color, SolidBrush> _brushCache = new Dictionary<Color, SolidBrush>();

        // ANSI 正则 — 编译一次，全局复用
        private static readonly Regex AnsiRegex = new Regex(
            @"\x1b\[([0-9;]*)([A-Za-z])",
            RegexOptions.Compiled);

        // 重绘节流
        private bool _needsRedraw;
        private bool _redrawScheduled;
        private DateTime _lastRedraw = DateTime.MinValue;
        private const int MinRedrawIntervalMs = 16; // ~60fps 上限，实际按需更低

        private Color _currentColor;
        private int _scrollOffset;
        private bool _isPaused;
        private bool _disposed;

        // 配色方案
        private TerminalColorScheme _scheme;

        private const int MaxBufferLines = 300;
        private const int CharWidth = 8;
        private const int LineHeight = 16;
        private const string FontName = "Consolas";
        private const float FontSize = 10f;

        // 重绘定时器 — 用于合并高频写入
        private readonly Timer _redrawTimer;

        public int Rows { get; private set; }
        public int Columns { get; private set; }

        public LightweightRenderer(int rows = 24, int columns = 80, TerminalColorScheme scheme = null)
        {
            Rows = rows;
            Columns = columns;

            _scheme = scheme ?? ColorSchemes.Classic;
            _currentColor = _scheme.Foreground;

            // 缓存字体 — 整个生命周期复用，避免 GDI handle 泄漏
            _font = new Font(FontName, FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            _boldFont = new Font(FontName, FontSize, FontStyle.Bold, GraphicsUnit.Pixel);

            // 双缓冲 Panel — 消除闪烁，不触发持续渲染
            _canvas = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = _scheme.Background,
                ForeColor = _scheme.Foreground
            };

            _canvas.Paint += OnPaint;
            _canvas.Resize += OnResize;
            _canvas.Disposed += OnCanvasDisposed;

            // 节流定时器 — 16ms 间隔检查是否需要重绘
            // 只在 _needsRedraw=true 时触发 Invalidate，空闲时完全静默
            _redrawTimer = new Timer { Interval = MinRedrawIntervalMs };
            _redrawTimer.Tick += OnRedrawTimerTick;
        }

        public void SetColorScheme(TerminalColorScheme scheme)
        {
            if (scheme == null || _disposed) return;

            lock (_lock)
            {
                _scheme = scheme;
                _currentColor = _scheme.Foreground;
                _canvas.BackColor = _scheme.Background;
                _canvas.ForeColor = _scheme.Foreground;
                _needsRedraw = true;
                ScheduleRedraw();
            }
        }

        public TerminalColorScheme GetColorScheme()
        {
            return _scheme;
        }

        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text) || _disposed) return;

            lock (_lock)
            {
                ParseAndAppend(text);
                _needsRedraw = true;

                if (!_isPaused)
                {
                    ScheduleRedraw();
                }
            }
        }

        public void Clear()
        {
            if (_disposed) return;

            lock (_lock)
            {
                _lineBuffer.Clear();
                _currentLine.Clear();
                _currentSpans.Clear();
                _scrollOffset = 0;
                _needsRedraw = true;
                ScheduleRedraw();
            }
        }

        public Control GetControl()
        {
            return _canvas;
        }

        public string GetSelection()
        {
            return string.Empty;
        }

        public string[] GetRecentLines(int lineCount)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _lineBuffer.Count - lineCount);
                var count = Math.Min(lineCount, _lineBuffer.Count);
                var result = new string[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = _lineBuffer[start + i].Text;
                }
                return result;
            }
        }

        public void Pause()
        {
            _isPaused = true;
            // 暂停时停止节流定时器 — 零 CPU
            _redrawTimer.Stop();
        }

        public void Resume()
        {
            _isPaused = false;
            if (_needsRedraw)
            {
                ScheduleRedraw();
            }
        }

        /// <summary>
        /// 调度重绘 — 节流机制：不直接 Invalidate，而是标记后由定时器统一处理
        /// 高频写入时（如快速滚动的日志），多次 Write 只触发一次重绘
        /// </summary>
        private void ScheduleRedraw()
        {
            if (_isPaused || _disposed) return;

            // 如果距离上次重绘已经足够久，直接重绘
            var now = DateTime.UtcNow;
            if ((now - _lastRedraw).TotalMilliseconds >= MinRedrawIntervalMs && !_redrawScheduled)
            {
                _redrawScheduled = true;
                DoRedraw();
            }
            else if (!_redrawTimer.Enabled)
            {
                // 启动定时器等待下次机会重绘
                _redrawTimer.Start();
            }
        }

        private void OnRedrawTimerTick(object sender, EventArgs e)
        {
            if (_needsRedraw && !_disposed)
            {
                DoRedraw();
            }
            else
            {
                // 没有待重绘内容，停止定时器 — 零 CPU
                _redrawTimer.Stop();
            }
        }

        private void DoRedraw()
        {
            _redrawScheduled = false;
            _lastRedraw = DateTime.UtcNow;
            _needsRedraw = false;

            if (_canvas.IsHandleCreated && !_canvas.IsDisposed)
            {
                try
                {
                    _canvas.BeginInvoke(new Action(() =>
                    {
                        if (!_canvas.IsDisposed)
                            _canvas.Invalidate();
                    }));
                }
                catch { }
            }
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (!_disposed)
            {
                _needsRedraw = true;
                ScheduleRedraw();
            }
        }

        private void OnCanvasDisposed(object sender, EventArgs e)
        {
            Dispose();
        }

        // ===== Paint — 按需触发，不会持续运行 =====

        private void OnPaint(object sender, PaintEventArgs e)
        {
            if (_disposed) return;

            lock (_lock)
            {
                var g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.CompositingQuality = CompositingQuality.HighSpeed;

                // 清除背景
                var bgBrush = GetBrush(_scheme.Background);
                g.FillRectangle(bgBrush, 0, 0, _canvas.Width, _canvas.Height);

                int visibleLines = Math.Max(1, _canvas.Height / LineHeight);
                int startLine = Math.Max(0, _lineBuffer.Count - visibleLines);
                int y = 0;

                for (int i = startLine; i < _lineBuffer.Count && y < _canvas.Height; i++)
                {
                    var line = _lineBuffer[i];
                    DrawColoredLine(g, line, 0, y);
                    y += LineHeight;
                }

                if (y < _canvas.Height && _currentLine.Length > 0)
                {
                    DrawSpans(g, _currentSpans, _currentLine.ToString(), 0, y);
                }

                // 绘制光标
                DrawCursor(g, y);
            }
        }

        private void DrawCursor(Graphics g, int y)
        {
            if (_isPaused) return;

            var cursorX = _currentLine.Length * CharWidth;
            var brush = GetBrush(_scheme.CursorColor);
            g.FillRectangle(brush, cursorX, y, CharWidth, LineHeight);
        }

        private void DrawColoredLine(Graphics g, ColoredLine line, int x, int y)
        {
            if (line.Spans == null || line.Spans.Count == 0)
            {
                var brush = GetBrush(_scheme.Foreground);
                g.DrawString(line.Text, _font, brush, x, y);
                return;
            }

            foreach (var span in line.Spans)
            {
                var brush = GetBrush(span.Color);
                g.DrawString(span.Text, _font, brush, x, y);
                x += span.Text.Length * CharWidth;
            }
        }

        private void DrawSpans(Graphics g, List<ColorSpan> spans, string currentText, int x, int y)
        {
            if (spans.Count == 0)
            {
                var brush = GetBrush(_currentColor);
                g.DrawString(currentText, _font, brush, x, y);
                return;
            }

            foreach (var span in spans)
            {
                var brush = GetBrush(span.Color);
                g.DrawString(span.Text, _font, brush, x, y);
                x += span.Text.Length * CharWidth;
            }
        }

        /// <summary>
        /// 获取缓存的 Brush — 避免每次 Paint 创建/销毁
        /// 颜色数量有限（16 ANSI + 少量 scheme 颜色），缓存不会膨胀
        /// </summary>
        private SolidBrush GetBrush(Color color)
        {
            if (!_brushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidBrush(color);
                _brushCache[color] = brush;
            }
            return brush;
        }

        // ===== 解析 =====

        private void ParseAndAppend(string text)
        {
            int lastIndex = 0;

            foreach (Match match in AnsiRegex.Matches(text))
            {
                if (match.Index > lastIndex)
                {
                    var plain = text.Substring(lastIndex, match.Index - lastIndex);
                    AppendPlainText(plain);
                }

                if (match.Groups[2].Value == "m")
                {
                    var codes = match.Groups[1].Value.Split(';');
                    foreach (var code in codes)
                    {
                        if (int.TryParse(code, out int c))
                        {
                            if (c == 0) _currentColor = _scheme.Foreground;
                            else if (c >= 30 && c <= 37) _currentColor = _scheme.AnsiColors[c - 30];
                            else if (c >= 90 && c <= 97) _currentColor = _scheme.AnsiColors[c - 90 + 8];
                        }
                    }
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                AppendPlainText(text.Substring(lastIndex));
            }
        }

        private void AppendPlainText(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    _lineBuffer.Add(new ColoredLine
                    {
                        Text = _currentLine.ToString(),
                        Spans = new List<ColorSpan>(_currentSpans)
                    });
                    _currentLine.Clear();
                    _currentSpans.Clear();

                    while (_lineBuffer.Count > MaxBufferLines)
                    {
                        _lineBuffer.RemoveAt(0);
                    }
                }
                else if (ch != '\r')
                {
                    _currentLine.Append(ch);
                    _currentSpans.Add(new ColorSpan { Text = ch.ToString(), Color = _currentColor });
                }
            }
        }

        // ===== Dispose — 彻底释放所有 GDI 资源 =====

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _redrawTimer.Stop();
            _redrawTimer.Dispose();

            _font?.Dispose();
            _font = null;
            _boldFont?.Dispose();
            _boldFont = null;

            foreach (var brush in _brushCache.Values)
                brush.Dispose();
            _brushCache.Clear();

            _lineBuffer.Clear();
            _currentLine.Clear();
            _currentSpans.Clear();
        }

        // ===== 内部类型 =====

        /// <summary>
        /// 双缓冲 Panel — 消除闪烁，不引入持续渲染
        /// </summary>
        private class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.UserPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
                UpdateStyles();
            }
        }

        private class ColoredLine
        {
            public string Text { get; set; }
            public List<ColorSpan> Spans { get; set; }
        }

        private class ColorSpan
        {
            public string Text { get; set; }
            public Color Color { get; set; }
        }
    }
}
