using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Gdterm.Terminal.Rendering
{
    /// <summary>
    /// 基于 RichTextBox 的终端渲染器——v1 实现，支持基本 ANSI 颜色
    /// 后期可替换为专业终端模拟库（如 TerminalControl、AvalonTerm）
    /// </summary>
    internal class TerminalRenderer : IRenderer
    {
        private readonly RichTextBox _textBox;
        private readonly List<string> _lineBuffer = new List<string>();
        private readonly StringBuilder _currentLine = new StringBuilder();
        private readonly object _lock = new object();
        private const int MaxBufferLines = 500;

        // ANSI 颜色映射
        private static readonly Color[] AnsiColors = new Color[]
        {
            Color.Black,            // 0 - Black
            Color.DarkRed,          // 1 - Red
            Color.DarkGreen,        // 2 - Green
            Color.DarkGoldenrod,    // 3 - Yellow
            Color.DarkBlue,         // 4 - Blue
            Color.DarkMagenta,      // 5 - Magenta
            Color.DarkCyan,         // 6 - Cyan
            Color.LightGray,        // 7 - White
            Color.DarkGray,         // 8 - Bright Black
            Color.Red,              // 9 - Bright Red
            Color.Green,            // 10 - Bright Green
            Color.Yellow,           // 11 - Bright Yellow
            Color.Blue,             // 12 - Bright Blue
            Color.Magenta,          // 13 - Bright Magenta
            Color.Cyan,             // 14 - Bright Cyan
            Color.White,            // 15 - Bright White
        };

        public int Rows { get; private set; }
        public int Columns { get; private set; }

        public TerminalRenderer(int rows = 24, int columns = 80)
        {
            Rows = rows;
            Columns = columns;

            _textBox = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BackColor = Color.Black,
                    ForeColor = Color.LightGray,
                    Font = new Font("Consolas", 10f, FontStyle.Regular),
                    ScrollBars = RichTextBox.Vertical,
                    WordWrap = false,
                    BorderStyle = BorderStyle.None
                };
        }

        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                // 解析 ANSI 转义序列并渲染
                var segments = ParseAnsi(text);
                foreach (var segment in segments)
                {
                    AppendSegment(segment);
                }

                // Trim buffer
                while (_lineBuffer.Count > MaxBufferLines)
                {
                    _lineBuffer.RemoveAt(0);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lineBuffer.Clear();
                _currentLine.Clear();
                if (_textBox.InvokeRequired)
                {
                    _textBox.Invoke(new Action(() => _textBox.Clear()));
                }
                else
                {
                    _textBox.Clear();
                }
            }
        }

        public Control GetControl()
        {
            return _textBox;
        }

        public string GetSelection()
        {
            return _textBox.InvokeRequired
                ? (string)_textBox.Invoke(new Func<string>(() => _textBox.SelectedText))
                : _textBox.SelectedText;
        }

        public string[] GetRecentLines(int lineCount)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _lineBuffer.Count - lineCount);
                var result = new string[Math.Min(lineCount, _lineBuffer.Count)];
                _lineBuffer.CopyTo(start, result, 0, result.Length);
                return result;
            }
        }

        private void AppendSegment(AnsiSegment segment)
        {
            if (segment.IsControl)
            {
                // 处理控制字符
                switch (segment.Text)
                {
                    case "\r":
                        // 回车，不清除行
                        break;
                    case "\n":
                        // 换行
                        _lineBuffer.Add(_currentLine.ToString());
                        _currentLine.Clear();
                        AppendToTextBox("\n");
                        break;
                    case "\t":
                        _currentLine.Append("    ");
                        AppendToTextBox("    ");
                        break;
                    default:
                        // 忽略其他控制字符
                        break;
                }
            }
            else
            {
                _currentLine.Append(segment.Text);
                AppendToTextBox(segment.Text);
            }
        }

        private void AppendToTextBox(string text)
        {
            if (_textBox.InvokeRequired)
            {
                _textBox.Invoke(new Action(() =>
                {
                    _textBox.AppendText(text);
                    _textBox.ScrollToCaret();
                }));
            }
            else
            {
                _textBox.AppendText(text);
                _textBox.ScrollToCaret();
            }
        }

        /// <summary>
        /// 解析 ANSI 转义序列为文本片段
        /// </summary>
        private static List<AnsiSegment> ParseAnsi(string text)
        {
            var segments = new List<AnsiSegment>();
            // 匹配 ANSI 转义序列：ESC[...m (SGR) 或其他
            var regex = new Regex(@"\x1b\[([0-9;]*)([A-Za-z])", RegexOptions.Compiled);
            int lastIndex = 0;

            foreach (Match match in regex.Matches(text))
            {
                // 添加转义序列前的普通文本
                if (match.Index > lastIndex)
                {
                    var plain = text.Substring(lastIndex, match.Index - lastIndex);
                    segments.Add(new AnsiSegment { Text = plain, IsControl = false });
                }

                // 处理 SGR (Select Graphic Rendition) 序列
                if (match.Groups[2].Value == "m")
                {
                    var codes = match.Groups[1].Value.Split(';');
                    foreach (var code in codes)
                    {
                        if (int.TryParse(code, out int c) && c == 0)
                        {
                            // 重置颜色 - 不输出文本，只是重置状态
                        }
                    }
                }

                lastIndex = match.Index + match.Length;
            }

            // 添加剩余文本
            if (lastIndex < text.Length)
            {
                var remaining = text.Substring(lastIndex);
                // 处理控制字符
                for (int i = 0; i < remaining.Length; i++)
                {
                    char ch = remaining[i];
                    if (ch == '\n' || ch == '\r' || ch == '\t')
                    {
                        if (i > 0 && remaining[i - 1] != '\n' && remaining[i - 1] != '\r')
                        {
                            // 纯文本部分已处理
                        }
                        segments.Add(new AnsiSegment { Text = ch.ToString(), IsControl = true });
                    }
                    else
                    {
                        // 找到连续的非控制字符
                        int start = i;
                        while (i < remaining.Length && remaining[i] != '\n' && remaining[i] != '\r' && remaining[i] != '\t')
                            i++;
                        segments.Add(new AnsiSegment { Text = remaining.Substring(start, i - start), IsControl = false });
                        i--; // 循环会 i++
                    }
                }
            }

            return segments;
        }

        private class AnsiSegment
        {
            public string Text { get; set; }
            public bool IsControl { get; set; }
        }
    }
}
