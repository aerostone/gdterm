using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using Gdterm.Terminal.Rendering.Vt;
using Gdterm.Terminal.Themes;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// Phase 0 cell 渲染器：VtTerminalEngine → GDI+ Panel。
    /// 对齐 LightweightRenderer 的 Pause/16ms 节流/双缓冲/刷子缓存，但语义是完整 VT cell grid。
    /// </summary>
    public class CellGdiRenderer : IRenderer, IDisposable
    {
        private readonly DoubleBufferedPanel _canvas;
        private readonly VtTerminalEngine _engine;
        private readonly object _lock = new object();
        private readonly Dictionary<Color, SolidBrush> _brushCache = new Dictionary<Color, SolidBrush>();

        private Font _font;
        private Font _boldFont;
        /// <summary>终端画布左侧内边距（参考成熟终端客户端的 8-12px 画布留白）。</summary>
        internal const float PadX = 8f;
        /// <summary>终端画布顶部内边距。</summary>
        internal const float PadY = 6f;
        private TerminalColorScheme _scheme;
        private VtPageSnapshot _page;
        private bool _isPaused;
        private bool _disposed;
        private bool _needsRedraw;
        private readonly Timer _redrawTimer;
        private const int MinRedrawIntervalMs = 16;
        private const string FontName = "Consolas";
        private const float FontSize = 12f;

        private float _charWidth = 8f;
        private float _charHeight = 16f;

        /// <summary>真彩 brush 缓存上限，防止 24-bit 颜色刷爆低配内存。</summary>
        private const int MaxBrushCache = 256;

        /// <summary>引擎请求发往主机的字节（DA/键鼠应答等）。</summary>
        public event EventHandler<byte[]> SendToHost;

        /// <summary>本地 cell 尺寸变化（供 UI 通知 SSH window-change）。</summary>
        public event EventHandler TerminalResized;

        public VtTerminalEngine Engine { get { return _engine; } }

        public int Rows { get { return _engine.Rows; } }
        public int Columns { get { return _engine.Columns; } }

        /// <summary>是否为 cell VT 路径（UI 双轨判断）。</summary>
        public bool IsVtCell { get { return true; } }

        public CellGdiRenderer(int rows = 24, int columns = 80, TerminalColorScheme scheme = null, int maxHistory = 500)
        {
            _scheme = scheme ?? ColorSchemes.Classic;
            _engine = new VtTerminalEngine(columns, rows, maxHistory);
            _engine.SendToHost += (s, data) =>
            {
                var h = SendToHost;
                if (h != null) h(this, data);
            };

            ApplyFont(FontName, FontSize);

            _canvas = new DoubleBufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = _scheme.Background,
                ForeColor = _scheme.Foreground
            };
            _canvas.Paint += OnPaint;
            _canvas.Resize += OnResize;
            _canvas.Disposed += OnCanvasDisposed;

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
                _engine.FullReset();
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
            // Phase 0：未实现拖选；返回可见全文便于复制调试
            return GetScreenText();
        }

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
            _page = _engine.SnapshotVisible();
            _engine.ClearChanges();
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
            lock (_lock)
            {
                page = _page;
            }

            g.Clear(_scheme.Background);
            if (page == null || page.Lines == null) return;

            float y = PadY;
            for (int r = 0; r < page.Lines.Count; r++)
            {
                var line = page.Lines[r];
                float x = PadX;
                if (line != null && line.Spans != null)
                {
                    foreach (var span in line.Spans)
                    {
                        if (span == null || span.Hidden || string.IsNullOrEmpty(span.Text))
                        {
                            if (span != null && !string.IsNullOrEmpty(span.Text))
                                x += span.Text.Length * _charWidth;
                            continue;
                        }

                        var bg = span.Background;
                        var fg = span.Foreground;
                        // 若背景接近默认黑且未设真彩，用 scheme 背景
                        var font = span.Bold ? _boldFont : _font;
                        var textSize = g.MeasureString(span.Text, font, int.MaxValue, StringFormat.GenericTypographic);
                        float w = Math.Max(textSize.Width, span.Text.Length * _charWidth);

                        if (bg.A > 0 && (bg.R | bg.G | bg.B) != 0)
                        {
                            g.FillRectangle(GetBrush(bg), x, y, w, _charHeight);
                        }

                        g.DrawString(span.Text, font, GetBrush(fg), x, y, StringFormat.GenericTypographic);

                        if (span.Underline)
                        {
                            using (var pen = new Pen(fg))
                                g.DrawLine(pen, x, y + _charHeight - 1, x + w, y + _charHeight - 1);
                        }

                        x += w;
                    }
                }

                y += _charHeight;
            }

            if (page.ShowCursor)
            {
                float cx = PadX + page.CursorColumn * _charWidth;
                float cy = PadY + page.CursorRow * _charHeight;
                g.FillRectangle(GetBrush(_scheme.CursorColor), cx, cy, Math.Max(2, _charWidth * 0.15f), _charHeight);
            }
        }

        private void OnResize(object sender, EventArgs e)
        {
            if (_disposed || _canvas.ClientSize.Width <= 0 || _canvas.ClientSize.Height <= 0) return;
            MeasureCell();
            // 减去两侧 padding 再计算 cols/rows — 否则右侧/底部边缘会有半截字被裁
            int cols = Math.Max(2, (int)((_canvas.ClientSize.Width - PadX * 2) / _charWidth));
            int rows = Math.Max(1, (int)((_canvas.ClientSize.Height - PadY * 2) / _charHeight));
            if (cols != Columns || rows != Rows)
                ResizeTerminal(cols, rows);
        }

        public void ApplyFont(string fontName, float fontSizePx)
        {
            if (_disposed) return;
            if (string.IsNullOrWhiteSpace(fontName)) fontName = FontName;
            if (fontSizePx < 8f) fontSizePx = 8f;
            if (fontSizePx > 36f) fontSizePx = 36f;
            lock (_lock)
            {
                try { if (_font != null) _font.Dispose(); } catch { }
                try { if (_boldFont != null) _boldFont.Dispose(); } catch { }
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
                MeasureCell();
                _needsRedraw = true;
                ScheduleRedraw();
            }
        }

        private void MeasureCell()
        {
            if (_font == null) return;
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var size = g.MeasureString("W", _font, int.MaxValue, StringFormat.GenericTypographic);
                _charWidth = Math.Max(6f, (float)Math.Ceiling(size.Width));
                _charHeight = Math.Max(12f, (float)Math.Ceiling(_font.GetHeight(g)) + 2f);
            }
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
            try { _engine.Dispose(); } catch { }
            try { _font.Dispose(); } catch { }
            try { _boldFont.Dispose(); } catch { }
            foreach (var b in _brushCache.Values)
            {
                try { b.Dispose(); } catch { }
            }
            _brushCache.Clear();
        }

        /// <summary>与 LightweightRenderer 一致的双缓冲 Panel。</summary>
        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }
        }
    }
}
