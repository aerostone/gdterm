using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Recording;
using Gdterm.Terminal.Rendering;
using Gdterm.Terminal.Themes;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 会话录制中心（P1-2）：列出 data/recordings 下的 .gtr，选中即**在应用内原样回放**。
    ///
    /// 与 Xshell/asciinema 的差别：这里回放的是**同一个 VT 引擎**（CellGdiRenderer），
    /// 不是"把日志再打印一遍"。所以录了 `vim` / `htop` 就能真的看到窗口被画出来、
    /// 被 alt-screen 擦掉——这是文本日志永远做不到的事，也是录制价值的全部。
    ///
    /// 回放调度：Timer 50ms 一跳，按录制时间轴把「偏移 &lt;= 已播时间」的帧一次性喂进去
    /// （而不是每帧一个 Timer：SSH 输出是碎片的，一帧一个 Timer 在 4x 速下会打爆消息循环）。
    /// 只喂 output 帧——输入帧在回放里若是也喂，会与主机回显重复一份（本地回显的路径尤其明显）。
    /// </summary>
    public sealed class SessionRecordingsForm : AntdUI.Window
    {
        /// <summary>回放节拍：50ms ≈ 20fps，肉眼连续且单次批量喂帧不会太长。</summary>
        private const int PlaybackTickMs = 50;

        private static readonly double[] Speeds = { 0.5, 1.0, 2.0, 4.0, 8.0 };

        private readonly string _recordingsDir;
        private ListView _list;
        private ListShell _shell;
        private EmptyStateView _empty;
        private Panel _stage;
        private AntdUI.Label _status;
        private AntdUI.Label _progressLabel;
        private AntdUI.Button _btnPlay;
        private AntdUI.Button _btnRestart;
        private AntdUI.Select _speedSelect;
        private TrackBar _seek;
        private readonly Timer _playTimer;

        private CellGdiRenderer _renderer;
        private SessionRecording _current;
        private string _currentPath;
        private long _cursorMs;
        private int _nextFrame;
        private double _speed = 1.0;
        private bool _playing;
        private bool _seeking;

        public SessionRecordingsForm(string recordingsDir = null)
        {
            _recordingsDir = recordingsDir ?? Program.RecordingsDir;

            Text = L10n.T("会话录制");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 1000, 640);
            MinimumSize = DpiScale.S(this, 720, 460);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            int fieldH = FormFontPolicy.FieldHeight(this);

            // ── 左：录制列表 ──
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont()
            };
            _list.Columns.Add(L10n.T("录制文件"), 250);
            _list.Columns.Add(L10n.T("连接"), 120);
            _list.Columns.Add(L10n.T("时长"), 80);
            _list.Columns.Add(L10n.T("大小"), 80);
            _list.Columns.Add(L10n.T("开始时间"), 140);
            // 选中即加载（含播放中切到另一段：切段本来就是"停下再看新的"，不该静默无视用户点击）
            _list.SelectedIndexChanged += (s, e) => SelectCurrent();
            _list.DoubleClick += (s, e) => { SelectCurrent(); StartPlayback(fromBeginning: true); };

            _shell = new ListShell(_list, L10n.T("过滤录制文件…"));
            _shell.SetItems(new List<ListViewItem>());
            _empty = EmptyStateView.Attach(_shell,
                L10n.T("还没有会话录制"),
                L10n.T("在终端里用「终端 → 开始录制会话」记一段，断开或停止后录制会出现在这里，可原样回放。"),
                L10n.T("刷新"), (s, e) => Reload());
            _empty.Name = "RecordingsEmptyState";

            // ── 右：回放舞台 ──
            _stage = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                BorderStyle = BorderStyle.None
            };

            var stagePlaceholder = new AntdUI.Label
            {
                Dock = DockStyle.Fill,
                Text = L10n.T("左侧选一段录制开始回放"),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = GdtermColorTable.Muted
            };
            _stage.Controls.Add(stagePlaceholder);
            _stage.Tag = stagePlaceholder;

            // ── 右上：回放控制条 ──
            var topBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, 0, 0, DpiScale.V(this, 4)),
                Margin = new Padding(0)
            };

            _btnPlay = MkButton("RecordingsPlay", L10n.T("播放"), (s, e) => TogglePlay());
            _btnRestart = MkButton("RecordingsRestart", L10n.T("重新开始"), (s, e) => StartPlayback(fromBeginning: true));
            topBar.Controls.Add(_btnPlay);
            topBar.Controls.Add(_btnRestart);

            _speedSelect = new AntdUI.Select
            {
                Name = "RecordingsSpeed",
                Width = DpiScale.V(this, 90),
                Height = fieldH,
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            foreach (var sp in Speeds)
                _speedSelect.Items.Add(sp.ToString("0.#", CultureInfo.InvariantCulture) + "x");
            _speedSelect.SelectedIndex = 1;
            _speedSelect.SelectedIndexChanged += (s, e) =>
            {
                int i = _speedSelect.SelectedIndex;
                if (i >= 0 && i < Speeds.Length) _speed = Speeds[i];
            };
            topBar.Controls.Add(_speedSelect);

            _progressLabel = new AntdUI.Label
            {
                Name = "RecordingsProgressLabel",
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Text = "00:00 / 00:00",
                Margin = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 6), 0, 0)
            };
            topBar.Controls.Add(_progressLabel);

            var stagePanel = new Panel { Dock = DockStyle.Fill, BackColor = GdtermColorTable.Background };
            stagePanel.Controls.Add(_stage);
            stagePanel.Controls.Add(topBar);

            _seek = new TrackBar
            {
                Name = "RecordingsSeek",
                Dock = DockStyle.Bottom,
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None,
                Height = DpiScale.V(this, 28),
                BackColor = GdtermColorTable.Background
            };
            _seek.Scroll += (s, e) =>
            {
                if (_current == null) return;
                _seeking = true;
                _cursorMs = (long)(_current.DurationMs * (_seek.Value / 1000.0));
                _seeking = false;
                SeekTo(_cursorMs);
            };
            stagePanel.Controls.Add(_seek);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = Math.Max(4, DpiScale.V(this, 6)),
                BackColor = GdtermColorTable.Background
            };
            split.Panel1.Controls.Add(_shell);
            split.Panel2.Controls.Add(stagePanel);

            // ── 底部：动作 + 状态 + 关闭 ──
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = fieldH + DpiScale.V(this, 16),
                BackColor = GdtermColorTable.Background
            };

            var leftFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0),
                BackColor = GdtermColorTable.Background
            };
            leftFlow.Controls.Add(MkButton("RecordingsRefresh", L10n.T("刷新"), (s, e) => Reload()));
            leftFlow.Controls.Add(MkButton("RecordingsDelete", L10n.T("删除"), (s, e) => DeleteSelected()));
            leftFlow.Controls.Add(MkButton("RecordingsOpenFolder", L10n.T("打开所在文件夹"), (s, e) => OpenFolder()));

            _status = new AntdUI.Label
            {
                Name = "RecordingsStatus",
                Dock = DockStyle.Right,
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, DpiScale.V(this, 6), 0, 0)
            };

            var rightFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0),
                BackColor = GdtermColorTable.Background
            };
            var btnClose = MkButton("RecordingsClose", L10n.T("关闭"), (s, e) => Close());
            btnClose.Margin = new Padding(DpiScale.V(this, 8), 0, 0, 0);
            CancelButton = btnClose;   // ESC 关闭（与其它工具窗一致）
            rightFlow.Controls.Add(btnClose);

            bottom.Controls.Add(leftFlow);
            bottom.Controls.Add(rightFlow);
            bottom.Controls.Add(_status);

            Controls.Add(split);
            Controls.Add(bottom);
            try { UiA11y.AutoName(this); } catch (Exception) { }

            _playTimer = new Timer { Interval = PlaybackTickMs };
            _playTimer.Tick += (s, e) => PlaybackTick();

            Load += (s, e) =>
            {
                try
                {
                    split.SplitterDistance = Math.Max(DpiScale.V(this, 260), (int)(split.Width * 0.34));
                }
                catch (Exception ex) { DiagLog.Swallowed("SessionRecordingsForm.Splitter", ex); }
                Reload();
            };
            FormClosed += (s, e) =>
            {
                StopPlayback();
                try { _playTimer.Dispose(); } catch { }
                DisposeRenderer();
            };
        }

        private AntdUI.Button MkButton(string name, string text, EventHandler onClick)
        {
            var btn = new AntdUI.Button {
                Name = name,
                Text = text,
                Height = FormFontPolicy.FieldHeight(this),
                Margin = new Padding(0, 0, DpiScale.V(this, 6), 0)
            };
            FormFontPolicy.SizeActionButton(this, btn, 10, 4);
            btn.Click += onClick;
            return btn;
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.Text = text;
            // 无状态栏时也要有反馈（CI/自动化截图里读的是标题栏）
            if (!string.IsNullOrEmpty(text)) Text = L10n.T("会话录制") + " — " + text;
        }

        // ───────────────────────── 列表 ─────────────────────────

        private void Reload()
        {
            if (IsDisposed) return;
            var items = new List<ListViewItem>();
            int broken = 0;
            try
            {
                if (!Directory.Exists(_recordingsDir))
                {
                    try { Directory.CreateDirectory(_recordingsDir); } catch (Exception) { }
                }
                if (Directory.Exists(_recordingsDir))
                {
                    var files = Directory.GetFiles(_recordingsDir, "*.gtr");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        string error;
                        var rec = SessionRecording.TryLoad(file, out error);
                        var info = new FileInfo(file);
                        string name = Path.GetFileNameWithoutExtension(file);
                        string conn = "-";
                        string duration = "-";
                        if (rec != null)
                        {
                            conn = !string.IsNullOrEmpty(rec.Header.ConnectionName)
                                ? rec.Header.ConnectionName
                                : (rec.Header.Host ?? "-");
                            duration = FormatMs(rec.DurationMs);
                        }
                        else
                        {
                            broken++;
                            duration = L10n.T("读取失败");
                        }

                        var item = new ListViewItem(name);
                        item.SubItems.Add(conn);
                        item.SubItems.Add(duration);
                        item.SubItems.Add(FormatSize(info.Length));
                        item.SubItems.Add(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        item.Tag = file;
                        item.ToolTipText = rec != null
                            ? string.Format(CultureInfo.InvariantCulture, "{0} × {1} · {2} 帧 · {3}",
                                rec.Header.Columns, rec.Header.Rows, rec.Frames.Count, file)
                            : (error ?? L10n.T("读取失败")) + " · " + file;
                        items.Add(item);
                    }
                }

                _shell.SetItems(items);
                _empty.Visible = items.Count == 0;
                string msg = items.Count == 0
                    ? L10n.T("没有录制文件")
                    : string.Format(CultureInfo.InvariantCulture, L10n.T("共 {0} 段录制"), items.Count);
                if (broken > 0) msg += string.Format(CultureInfo.InvariantCulture, L10n.T("（{0} 段无法读取）"), broken);
                SetStatus(msg);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("SessionRecordingsForm.Reload", ex);
                SetStatus(L10n.T("扫描录制目录失败：") + ex.Message);
            }
        }

        private string SelectedPath()
        {
            if (_list == null || _list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as string;
        }

        /// <summary>选中即加载（不自动播）：用户点一行多半是想看看这段录的是什么。</summary>
        private void SelectCurrent()
        {
            string path = SelectedPath();
            if (string.IsNullOrEmpty(path) || path == _currentPath) return;
            LoadRecording(path);
        }

        private void LoadRecording(string path)
        {
            StopPlayback();
            string error;
            var rec = SessionRecording.TryLoad(path, out error);
            if (rec == null)
            {
                SetStatus(L10n.T("无法读取：") + (error ?? path));
                return;
            }
            _current = rec;
            _currentPath = path;
            _cursorMs = 0;
            _nextFrame = 0;
            RebuildRenderer();
            _seek.Value = 0;
            UpdateProgressLabel();
            SetStatus(string.Format(CultureInfo.InvariantCulture,
               "{0} · {1} × {2} · {3} 帧", Path.GetFileName(path),
                rec.Header.Columns, rec.Header.Rows, rec.Frames.Count));
        }

        private void RebuildRenderer()
        {
            DisposeRenderer();
            if (_current == null) return;
            try
            {
                var scheme = ColorSchemes.GetByName("Classic") ?? ColorSchemes.Classic;
                _renderer = new CellGdiRenderer(
                    Math.Max(4, _current.Header.Rows),
                    Math.Max(20, _current.Header.Columns),
                    scheme);
                var canvas = _renderer.GetControl();
                canvas.Dock = DockStyle.Fill;
                _stage.Controls.Clear();
                _stage.Controls.Add(canvas);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("SessionRecordingsForm.RebuildRenderer", ex);
                SetStatus(L10n.T("回放引擎初始化失败：") + ex.Message);
            }
        }

        private void DisposeRenderer()
        {
            var r = _renderer;
            _renderer = null;
            if (r == null) return;
            try
            {
                var canvas = r.GetControl();
                if (canvas != null && _stage != null && _stage.Controls.Contains(canvas))
                    _stage.Controls.Remove(canvas);
                r.Dispose();
            }
            catch (Exception ex) { DiagLog.Swallowed("SessionRecordingsForm.DisposeRenderer", ex); }
        }

        // ───────────────────────── 回放 ─────────────────────────

        private void TogglePlay()
        {
            if (_playing) { StopPlayback(); return; }
            if (_current == null) { SelectCurrent(); if (_current == null) return; }
            StartPlayback(fromBeginning: false);
        }

        private void StartPlayback(bool fromBeginning)
        {
            if (_current == null) return;
            if (fromBeginning || _cursorMs >= _current.DurationMs)
            {
                _cursorMs = 0;
                _nextFrame = 0;
                if (_renderer != null) { try { _renderer.Clear(); } catch (Exception) { } }
            }
            _playing = true;
            if (_btnPlay != null) _btnPlay.Text = L10n.T("暂停");
            _playTimer.Start();
        }

        private void StopPlayback()
        {
            _playing = false;
            try { _playTimer.Stop(); } catch { }
            if (_btnPlay != null) _btnPlay.Text = L10n.T("播放");
        }

        /// <summary>按已播时间把到期帧一次性喂进去；4x/8x 时一批可能几十帧。</summary>
        private void PlaybackTick()
        {
            if (!_playing || _current == null || _renderer == null) return;
            try
            {
                _cursorMs += (long)(PlaybackTickMs * _speed);
                int fed = 0;
                while (_nextFrame < _current.Frames.Count &&
                       _current.Frames[_nextFrame].OffsetMs <= _cursorMs)
                {
                    var frame = _current.Frames[_nextFrame];
                    _nextFrame++;
                    if (frame.Kind != SessionFrameKind.Output) continue;
                    _renderer.Write(frame.Data);
                    fed++;
                }

                // 不拖拽时进度条跟着走；拖拽中让用户的手赢
                if (!_seeking && _seek.Maximum > 0)
                {
                    long dur = _current.DurationMs;
                    int pos = dur <= 0 ? 0 : (int)(_seek.Maximum * Math.Min(1.0, (double)_cursorMs / dur));
                    if (pos != _seek.Value) _seek.Value = Math.Min(_seek.Maximum, Math.Max(_seek.Minimum, pos));
                }
                UpdateProgressLabel();

                if (_nextFrame >= _current.Frames.Count && _cursorMs >= _current.DurationMs)
                {
                    StopPlayback();
                    SetStatus(L10n.T("回放结束") + " · " + FormatMs(_current.DurationMs));
                }
                else if (_cursorMs > _current.DurationMs + 2000 && _nextFrame < _current.Frames.Count)
                {
                    // 时间轴跑完了但帧还有剩（尾部 duration 比最后一帧偏移小，例如录制被强杀没写 end）：
                    // 剩下的全喂完再收尾——宁可快一点播完，也不能把用户的内容截掉。
                    FlushRemainingFrames();
                    StopPlayback();
                }
            }
            catch (Exception ex)
            {
                StopPlayback();
                DiagLog.Swallowed("SessionRecordingsForm.PlaybackTick", ex);
                SetStatus(L10n.T("回放中断：") + ex.Message);
            }
        }

        /// <summary>把剩余帧一次性喂完（收尾/跳播用）。</summary>
        private void FlushRemainingFrames()
        {
            if (_current == null || _renderer == null) return;
            while (_nextFrame < _current.Frames.Count)
            {
                var frame = _current.Frames[_nextFrame];
                _nextFrame++;
                if (frame.Kind == SessionFrameKind.Output) _renderer.Write(frame.Data);
            }
        }

        /// <summary>拖到某处：从头重放（VT 状态是有状态的，不能"只喂后半段"），但不启动定时器。</summary>
        private void SeekTo(long targetMs)
        {
            if (_current == null || _renderer == null) return;
            try
            {
                _renderer.Clear();
                _nextFrame = 0;
                _cursorMs = 0;
                long stop = Math.Max(0, targetMs);
                while (_nextFrame < _current.Frames.Count && _current.Frames[_nextFrame].OffsetMs <= stop)
                {
                    var frame = _current.Frames[_nextFrame];
                    _nextFrame++;
                    if (frame.Kind == SessionFrameKind.Output) _renderer.Write(frame.Data);
                }
                _cursorMs = stop;
                UpdateProgressLabel();
            }
            catch (Exception ex) { DiagLog.Swallowed("SessionRecordingsForm.SeekTo", ex); }
        }

        private void UpdateProgressLabel()
        {
            if (_progressLabel == null) return;
            long dur = _current != null ? _current.DurationMs : 0;
            _progressLabel.Text = FormatMs(Math.Min(_cursorMs, dur)) + " / " + FormatMs(dur);
        }

        private static string FormatMs(long ms)
        {
            if (ms < 0) ms = 0;
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalHours >= 1)
                return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                    (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", ts.Minutes, ts.Seconds);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // ───────────────────────── 动作 ─────────────────────────

        private void DeleteSelected()
        {
            string path = SelectedPath();
            if (string.IsNullOrEmpty(path)) return;
            var answer = MessageBox.Show(this,
                L10n.T("删除这段录制？此操作不可撤销。") + "\r\n" + Path.GetFileName(path),
                L10n.T("删除录制"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            try
            {
                if (path == _currentPath) { StopPlayback(); DisposeRenderer(); _current = null; _currentPath = null; }
                File.Delete(path);
                Reload();
            }
            catch (Exception ex)
            {
                SetStatus(L10n.T("删除失败：") + ex.Message);
            }
        }

        private void OpenFolder()
        {
            try
            {
                if (!Directory.Exists(_recordingsDir)) Directory.CreateDirectory(_recordingsDir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + _recordingsDir + "\"");
            }
            catch (Exception ex) { SetStatus(L10n.T("打开文件夹失败：") + ex.Message); }
        }
    }
}
