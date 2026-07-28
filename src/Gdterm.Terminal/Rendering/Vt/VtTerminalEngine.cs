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
        private readonly object _lock = new object();
        private bool _disposed;

        /// <summary>终端需要回写主机的序列（DA/CPR/键鼠等）。</summary>
        public event EventHandler<byte[]> SendToHost;

        /// <summary>窗口标题变更（OSC）。</summary>
        public event EventHandler<string> TitleChanged;

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

        /// <summary>应用是否启用了鼠标上报（vim/less/mc）。UI 据此决定左键是拖选还是透传。</summary>
        public bool IsMouseTrackingEnabled
        {
            get
            {
                lock (_lock)
                {
                    try { return _controller.MouseTrackingEnabled; }
                    catch { return false; }
                }
            }
        }

        public VtTerminalEngine(int columns = 80, int rows = 24, int maxHistory = 500)
        {
            if (columns < 2) columns = 2;
            if (rows < 1) rows = 1;
            Columns = columns;
            Rows = rows;

            _controller = new VirtualTerminalController();
            _controller.MaximumHistoryLines = Math.Min(Math.Max(maxHistory, rows), 2000);
            _controller.ResizeView(columns, rows);
            _controller.SendData += OnControllerSendData;

            _consumer = new DataConsumer(_controller);

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
            lock (_lock)
            {
                _consumer.Push(data);
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
                _controller.ResizeView(columns, rows);
            }
        }

        /// <summary>可见区域纯文本（调试/测试用）。</summary>
        public string GetScreenText()
        {
            lock (_lock) return _controller.GetScreenText() ?? string.Empty;
        }

        /// <summary>
        /// 抓取可见页为行+着色 span，供 GDI 渲染。
        /// 颜色来自 VtNetCore WebColor (#RRGGBB)，含 256/true-color 路径。
        /// </summary>
        public VtPageSnapshot SnapshotVisible()
        {
            lock (_lock)
            {
                var top = _controller.ViewPort.TopRow;
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

        public void MousePress(int column, int row, int button, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _controller.MousePress(column, row, button, control, shift);
        }

        public void MouseRelease(int column, int row, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _controller.MouseRelease(column, row, control, shift);
        }

        public void MouseMove(int column, int row, int button, bool control, bool shift)
        {
            if (_disposed) return;
            lock (_lock) _controller.MouseMove(column, row, button, control, shift);
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
