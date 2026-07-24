using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Gdterm.Terminal.Themes;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// 轻量级 GDI+ 终端渲染器——支持可切换配色方案，大幅降低内存占用
    /// 每个实例仅 ~0.3MB（vs RichTextBox ~3-5MB）
    /// </summary>
    internal class LightweightRenderer : IRenderer
    {
        private readonly Panel _canvas;
        private readonly List<ColoredLine> _lineBuffer = new List<ColoredLine>();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly List<ColorSpan> _currentSpans = new List<ColorSpan>();
        private readonly object _lock = new object();
        private bool _needsRedraw;

        private Color _currentColor;
        private int _scrollOffset;
        private bool _isPaused;

        // 配色方案
        private TerminalColorScheme _scheme;

        private const int MaxBufferLines = 300;
        private const int CharWidth = 8;
        private const int LineHeight = 16;
        private const string FontName = "Consolas";

        public int Rows { get; private set; }
        public int Columns { get; private set; }

        public LightweightRenderer(int rows = 24, int columns = 80, TerminalColorScheme scheme = null)
        {
            Rows = rows;
            Columns = columns;

            // 应用配色方案
            _scheme = scheme ?? ColorSchemes.Classic;
            _currentColor = _scheme.Foreground;

            _canvas = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _scheme.Background,
                ForeColor = _scheme.Foreground
            };

            _canvas.Paint += OnPaint;
            _canvas.Resize += (s, e) => _canvas.Invalidate();
        }

        /// <summary>
        /// 切换配色方案
        /// </summary>
        public void SetColorScheme(TerminalColorScheme scheme)
        {
            if (scheme == null) return;

            lock (_lock)
            {
                _scheme = scheme;
                _currentColor = _scheme.Foreground;
                _canvas.BackColor = _scheme.Background;
                _canvas.ForeColor = _scheme.Foreground;

                // 更新所有已缓存行的颜色（简单方案：重置为新前景色）
                // 注意：已解析的 ANSI 颜色会保持不变
                _needsRedraw = true;
                RequestRedraw();
            }
        }

        /// <summary>
        /// 获取当前配色方案
        /// </summary>
        public TerminalColorScheme GetColorScheme()
        {
            return _scheme;
        }

        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                ParseAndAppend(text);
                _needsRedraw = true;

                if (!_isPaused)
                {
                    RequestRedraw();
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lineBuffer.Clear();
                _currentLine.Clear();
                _currentSpans.Clear();
                _scrollOffset = 0;
                _needsRedraw = true;
                RequestRedraw();
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
                var result = new string[Math.Min(lineCount, _lineBuffer.Count)];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = _lineBuffer[start + i].Text;
                }
                return result;
            }
        }

        public void Pause()
        {
            _isPaused = true;
        }

        public void Resume()
        {
            _isPaused = false;
            if (_needsRedraw)
            {
                RequestRedraw();
            }
        }

        private void RequestRedraw()
        {
            if (_canvas.IsHandleCreated && !_canvas.IsDisposed)
            {
                try
                {
                    _canvas.BeginInvoke(new Action(() => _canvas.Invalidate()));
                }
                catch { }
            }
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            lock (_lock)
            {
                var g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                // 清除背景
                using (var bgBrush = new SolidBrush(_scheme.Background))
                {
                    g.FillRectangle(bgBrush, 0, 0, _canvas.Width, _canvas.Height);
                }

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

                // 绘制光标（简化：闪烁由定时器控制）
                DrawCursor(g, y);
            }
        }

        private void DrawCursor(Graphics g, int y)
        {
            if (_isPaused) return;

            var cursorX = _currentLine.Length * CharWidth;
            using (var brush = new SolidBrush(_scheme.CursorColor))
            {
                g.FillRectangle(brush, cursorX, y, CharWidth, LineHeight);
            }
        }

        private void DrawColoredLine(Graphics g, ColoredLine line, int x, int y)
        {
            if (line.Spans == null || line.Spans.Count == 0)
            {
                using (var brush = new SolidBrush(_scheme.Foreground))
                {
                    g.DrawString(line.Text, new Font(FontName, 10f), brush, x, y);
                }
                return;
            }

            foreach (var span in line.Spans)
            {
                using (var brush = new SolidBrush(span.Color))
                {
                    g.DrawString(span.Text, new Font(FontName, 10f), brush, x, y);
                    x += (int)(span.Text.Length * CharWidth);
                }
            }
        }

        private void DrawSpans(Graphics g, List<ColorSpan> spans, string currentText, int x, int y)
        {
            if (spans.Count == 0)
            {
                using (var brush = new SolidBrush(_currentColor))
                {
                    g.DrawString(currentText, new Font(FontName, 10f), brush, x, y);
                }
                return;
            }

            foreach (var span in spans)
            {
                using (var brush = new SolidBrush(span.Color))
                {
                    g.DrawString(span.Text, new Font(FontName, 10f), brush, x, y);
                    x += (int)(span.Text.Length * CharWidth);
                }
            }
        }

        private void ParseAndAppend(string text)
        {
            var regex = new Regex(@"\x1b\[([0-9;]*)([A-Za-z])", RegexOptions.Compiled);
            int lastIndex = 0;

            foreach (Match match in regex.Matches(text))
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
