using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// 轻量级 GDI+ 终端渲染器——用自绘 Panel 替代 RichTextBox，大幅降低内存占用
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

        private Color _currentColor = Color.LightGray;
        private int _scrollOffset;
        private bool _isPaused; // 非活动标签暂停渲染

        private const int MaxBufferLines = 300; // 降低缓冲区
        private const int CharWidth = 8;
        private const int LineHeight = 16;
        private const string FontName = "Consolas";

        public int Rows { get; private set; }
        public int Columns { get; private set; }

        // ANSI 颜色映射（16 色）
        private static readonly Color[] AnsiColors = new Color[]
        {
            Color.Black, Color.DarkRed, Color.DarkGreen, Color.DarkGoldenrod,
            Color.DarkBlue, Color.DarkMagenta, Color.DarkCyan, Color.LightGray,
            Color.DarkGray, Color.Red, Color.Green, Color.Yellow,
            Color.Blue, Color.Magenta, Color.Cyan, Color.White
        };

        public LightweightRenderer(int rows = 24, int columns = 80)
        {
            Rows = rows;
            Columns = columns;

            _canvas = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ForeColor = Color.LightGray
            };

            _canvas.Paint += OnPaint;
            _canvas.Resize += (s, e) => _canvas.Invalidate();
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
            // v1 不支持选中
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

        /// <summary>
        /// 暂停渲染（非活动标签调用，节省 CPU）
        /// </summary>
        public void Pause()
        {
            _isPaused = true;
        }

        /// <summary>
        /// 恢复渲染并刷新（活动标签调用）
        /// </summary>
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
                catch { /* 控件已销毁 */ }
            }
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            lock (_lock)
            {
                var g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                int visibleLines = Math.Max(1, _canvas.Height / LineHeight);
                int startLine = Math.Max(0, _lineBuffer.Count - visibleLines);
                int y = 0;

                for (int i = startLine; i < _lineBuffer.Count && y < _canvas.Height; i++)
                {
                    var line = _lineBuffer[i];
                    DrawColoredLine(g, line, 0, y);
                    y += LineHeight;
                }

                // 绘制当前行
                if (y < _canvas.Height && _currentLine.Length > 0)
                {
                    DrawSpans(g, _currentSpans, _currentLine.ToString(), 0, y);
                }
            }
        }

        private void DrawColoredLine(Graphics g, ColoredLine line, int x, int y)
        {
            if (line.Spans == null || line.Spans.Count == 0)
            {
                using (var brush = new SolidBrush(Color.LightGray))
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
            // 简化的 ANSI 解析
            var regex = new Regex(@"\x1b\[([0-9;]*)([A-Za-z])", RegexOptions.Compiled);
            int lastIndex = 0;

            foreach (Match match in regex.Matches(text))
                {
                // 转义序列前的普通文本
                if (match.Index > lastIndex)
                {
                    var plain = text.Substring(lastIndex, match.Index - lastIndex);
                    AppendPlainText(plain);
                }

                // 处理 SGR
                if (match.Groups[2].Value == "m")
                {
                    var codes = match.Groups[1].Value.Split(';');
                    foreach (var code in codes)
                    {
                        if (int.TryParse(code, out int c))
                        {
                            if (c == 0) _currentColor = Color.LightGray;
                            else if (c >= 30 && c <= 37) _currentColor = AnsiColors[c - 30];
                            else if (c >= 90 && c <= 97) _currentColor = AnsiColors[c - 90 + 8];
                        }
                    }
                }

                lastIndex = match.Index + match.Length;
            }

            // 剩余文本
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
