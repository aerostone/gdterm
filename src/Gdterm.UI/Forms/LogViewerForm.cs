using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.Core.Serialization;
using Gdterm.UI.Controls;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 日志查看器（P1-3 差距补齐）：把 logs/ 下散落的终端会话、审计、崩溃日志**在应用内**读出来。
    ///
    /// 缺口背景：此前只有「帮助 → 打开日志文件夹」——用户被丢到资源管理器里，自己拿记事本开。
    /// 便携版在 U 盘/只读介质、或被远程指导时根本没法开，日志等于不存在。审计日志还是回填
    /// 审计台账的唯一依据。
    ///
    /// 取舍：
    ///  - **不加载整个文件**。文本视图只读末尾 2MB（超出在首行标注），二进制视图按 4KB 分页；
    ///    10MB 的会话日志一次性读进 TextBox 会卡住 UI 线程，这是记事本能做而我们不该做的事。
    ///  - **十六进制视图是刚需不是花活**：终端日志被截断/编码错乱时，文本视图只能看到乱码，
    ///    要判断"到底是 0x0a 被吞了还是 GBK 写坏了"必须看字节。
    ///  - 查找用纯文本 Ordinal 匹配，不接正则：日志里 `[`、`(` 满地都是，正则反而帮倒忙。
    ///  - 跟随最新（tail）只在文本模式且文件仍在增长时刷新，避免"每 1s 重读 2MB"把便携机拖死。
    /// </summary>
    public sealed class LogViewerForm : AntdUI.Window
    {
        /// <summary>文本视图尾部读取上限：2MB ≈ 2 万行，足够看崩溃现场。</summary>
        private const int TextTailBytes = 2 * 1024 * 1024;

        /// <summary>十六进制视图每页字节数（256 行 × 16B）。</summary>
        private const int HexPageBytes = 4096;

        private readonly string _logsDir;
        private readonly ListView _list;
        private ListShell _shell;
        private EmptyStateView _empty;
        private TextBox _viewer;
        private AntdUI.Input _search;
        private AntdUI.Button _btnPrev;
        private AntdUI.Button _btnNext;
        private AntdUI.Button _btnPrevPage;
        private AntdUI.Button _btnNextPage;
        private AntdUI.Button _btnOpenExternal;
        private AntdUI.Checkbox _chkHex;
        private AntdUI.Checkbox _chkTail;
        private AntdUI.Label _status;
        private AntdUI.Select _scope;
        private readonly Timer _tailTimer;

        private string _currentPath;
        private long _currentLength = -1;
        private long _hexOffset;
        private DateTime _currentMtime = DateTime.MinValue;

        public LogViewerForm(string logsDir = null)
        {
            _logsDir = string.IsNullOrEmpty(logsDir) ? Gdterm.UI.Program.LogsDir : logsDir;

            Text = L10n.T("日志查看器");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            // Font 先于 ClientSize（CI 0.1.359 字体缩放顺序教训）
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 1040, 660);
            MinimumSize = DpiScale.S(this, 720, 460);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            // ── 左：文件列表（ListShell 自带过滤/排序，日志文件多了靠它找） ──
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
            _list.Columns.Add(L10n.T("日志文件"), 300);
            _list.Columns.Add(L10n.T("大小"), 90);
            _list.Columns.Add(L10n.T("修改时间"), 130);
            _list.SelectedIndexChanged += (s, e) => LoadSelected();
            _list.DoubleClick += (s, e) => OpenExternal();

            _shell = new ListShell(_list, L10n.T("过滤文件名…"));
            _shell.SetItems(new List<ListViewItem>());
            _empty = EmptyStateView.Attach(_shell,
                L10n.T("还没有日志文件"),
                L10n.T("开启「终端自动记录」或产生审计事件后，日志会出现在这里；也可用下面的「刷新」重新扫描。"),
                L10n.T("刷新"), (s, e) => Reload());
            _empty.Name = "LogViewerEmptyState";

            // ── 右上：查看器工具条 ──
            var viewerTop = new FlowLayoutPanel
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
            int fieldH = FormFontPolicy.FieldHeight(this);

            _chkHex = new AntdUI.Checkbox
            {
                Name = "LogViewerHexToggle",
                Text = L10n.T("十六进制"),
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, DpiScale.V(this, 6), DpiScale.V(this, 12), 0)
            };
            _chkHex.CheckedChanged += (s, e) => ReloadCurrent();
            viewerTop.Controls.Add(_chkHex);

            _chkTail = new AntdUI.Checkbox
            {
                Name = "LogViewerTailToggle",
                Text = L10n.T("跟随最新"),
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, DpiScale.V(this, 6), DpiScale.V(this, 12), 0)
            };
            _chkTail.CheckedChanged += (s, e) => { if (_chkTail.Checked) ReloadCurrent(); };
            viewerTop.Controls.Add(_chkTail);

            _btnPrevPage = MkButton("LogViewerPrevPage", L10n.T("上一页"), (s, e) => PageHex(-1));
            _btnNextPage = MkButton("LogViewerNextPage", L10n.T("下一页"), (s, e) => PageHex(1));
            _btnOpenExternal = MkButton("LogViewerOpenExternal", L10n.T("用系统程序打开"), (s, e) => OpenExternal());
            viewerTop.Controls.Add(_btnPrevPage);
            viewerTop.Controls.Add(_btnNextPage);
            viewerTop.Controls.Add(_btnOpenExternal);

            _viewer = new TextBox
            {
                Name = "LogViewerViewer",
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.MonoFont(),
                // TextBox 默认最大 32KB 文本量限制在 .NET 下已放开，但显式设上限避免超长行拖死重绘
                MaxLength = TextTailBytes
            };
            _viewer.KeyDown += (s, e) =>
            {
                // Del/退格在只读视图里什么都不该做；Ctrl+F 走查找框更符合直觉
                if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            };

            var viewerPanel = new Panel { Dock = DockStyle.Fill, BackColor = GdtermColorTable.Background };
            viewerPanel.Controls.Add(_viewer);
            viewerPanel.Controls.Add(viewerTop);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = Math.Max(4, DpiScale.V(this, 6)),
                BackColor = GdtermColorTable.Background
            };
            split.Panel1.Controls.Add(_shell);
            split.Panel2.Controls.Add(viewerPanel);

            // ── 底部：查找 + 状态 + 关闭 ──
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

            _scope = new AntdUI.Select
            {
                Name = "LogViewerScope",
                Width = DpiScale.V(this, 118),
                Height = fieldH,
                Margin = new Padding(0, 0, DpiScale.V(this, 6), 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _scope.Items.AddRange(new object[] { L10n.T("全部日志"), "terminal", "audit", "crash" });
            _scope.SelectedIndex = 0;
            _scope.SelectedIndexChanged += (s, e) => Reload();
            leftFlow.Controls.Add(_scope);

            var btnRefresh = MkButton("LogViewerRefresh", L10n.T("刷新"), (s, e) => Reload());
            leftFlow.Controls.Add(btnRefresh);

            _search = new AntdUI.Input
            {
                Name = "LogViewerSearch",
                Width = DpiScale.V(this, 190),
                Height = fieldH,
                PlaceholderText = L10n.T("在日志中查找…"),
                Margin = new Padding(DpiScale.V(this, 10), 0, DpiScale.V(this, 6), 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                Find(!e.Shift);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            leftFlow.Controls.Add(_search);

            _btnPrev = MkButton("LogViewerFindPrev", L10n.T("上一个"), (s, e) => Find(false));
            _btnNext = MkButton("LogViewerFindNext", L10n.T("下一个"), (s, e) => Find(true));
            leftFlow.Controls.Add(_btnPrev);
            leftFlow.Controls.Add(_btnNext);

            _status = new AntdUI.Label
            {
                Name = "LogViewerStatus",
                Dock = DockStyle.Right,
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, DpiScale.V(this, 6), 0, 0)
            };
            FormFontPolicy.FitSingleLineLabel(_status);

            bottom.Controls.Add(leftFlow);
            bottom.Controls.Add(_status);

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
            var btnClose = MkButton("LogViewerClose", L10n.T("关闭"), (s, e) => Close());
            btnClose.Margin = new Padding(DpiScale.V(this, 8), 0, 0, 0);
            // Esc 关闭：只读查看器没有"确认/取消"对，关闭就是唯一的取消语义。
            // 不绑 CancelButton 的话 Esc 什么都不做（冒烟门 R3 也是这么判的）。
            CancelButton = btnClose;
            rightFlow.Controls.Add(btnClose);
            bottom.Controls.Add(rightFlow);

            Controls.Add(split);
            Controls.Add(bottom);
            try { UiA11y.AutoName(this); } catch (Exception) { }

            _tailTimer = new Timer { Interval = 1000 };
            _tailTimer.Tick += (s, e) => TailTick();

            Load += (s, e) =>
            {
                // 左右三七开：左侧文件列表只是定位用的，读内容是主体
                try
                {
                    split.SplitterDistance = Math.Max(DpiScale.V(this, 240),
                        (int)(split.Width * 0.32));
                }
                catch (Exception ex) { DiagLog.Swallowed("LogViewerForm.Splitter", ex); }
                Reload();
                _tailTimer.Start();
            };
            FormClosed += (s, e) => { try { _tailTimer.Stop(); _tailTimer.Dispose(); } catch { } };
        }

        /// <summary>
        /// 状态栏写文本。必须空安全：底部面板（含 _status）在构造次序上晚于文件列表，
        /// 而 _scope.SelectedIndex=0 会立刻触发一次 Reload → 状态栏还不存在。
        /// </summary>
        private void SetStatus(string text)
        {
            if (_status != null) _status.Text = text;
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

        // ───────────────────────── 枚举 ─────────────────────────

        private void Reload()
        {
            if (IsDisposed) return;
            var items = new List<ListViewItem>();
            try
            {
                if (!Directory.Exists(_logsDir)) Directory.CreateDirectory(_logsDir);
                string scope = _scope != null && _scope.SelectedIndex > 0
                    ? Convert.ToString(_scope.Items[_scope.SelectedIndex], CultureInfo.InvariantCulture)
                    : null;

                // 递归但限深：logs/ 下就是 2 层（logs/terminal/*.log），
                // 深递归一旦有人把大目录塞进来会卡住 UI 线程
                var files = new List<FileInfo>();
                CollectFiles(new DirectoryInfo(_logsDir), 0, files);

                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                foreach (var f in files)
                {
                    string rel = RelativePath(f);
                    if (!string.IsNullOrEmpty(scope) &&
                        rel.IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var it = new ListViewItem(rel);
                    it.SubItems.Add(FormatSize(f.Length));
                    it.SubItems.Add(f.LastWriteTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    it.Tag = f.FullName;
                    items.Add(it);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("LogViewerForm.Reload", ex);
                SetStatus(L10n.T("读取日志目录失败：") + ex.Message);
            }

            _shell.SetItems(items);
            _empty.SetEmpty(items.Count == 0);
            if (items.Count == 0)
            {
                _viewer.Clear();
                _currentPath = null;
                SetStatus(L10n.T("共 0 个日志文件"));
                return;
            }

            // 保持当前选中项（刷新不该把用户踹回第一条），否则选最新一条
            int keep = -1;
            if (_currentPath != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (string.Equals((string)items[i].Tag, _currentPath, StringComparison.OrdinalIgnoreCase))
                    { keep = i; break; }
                }
            }
            _list.SelectedIndices.Clear();
            _list.SelectedIndices.Add(keep >= 0 ? keep : 0);
            SetStatus(L10n.Tf("共 {0} 个日志文件", items.Count));
        }

        private void CollectFiles(DirectoryInfo dir, int depth, List<FileInfo> sink)
        {
            if (dir == null || depth > 2) return;
            try
            {
                foreach (var f in dir.GetFiles("*.*"))
                {
                    // 只认文本/日志类；.dmp 也列出来（崩溃排查第一步就是"有没有 dump"）
                    string ext = f.Extension.ToLowerInvariant();
                    if (ext == ".log" || ext == ".txt" || ext == ".json" || ext == ".ini"
                        || ext == ".csv" || ext == ".dmp" || ext == ".md")
                        sink.Add(f);
                }
                foreach (var sub in dir.GetDirectories())
                {
                    // 跳过 bin/obj 之类噪音目录（有人把 logs 指到程序目录时会发生）
                    string n = sub.Name.ToLowerInvariant();
                    if (n == "bin" || n == "obj" || n == "node_modules" || n == ".git") continue;
                    CollectFiles(sub, depth + 1, sink);
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("LogViewerForm.CollectFiles", ex); }
        }

        private string RelativePath(FileInfo f)
        {
            try
            {
                string root = Path.GetFullPath(_logsDir);
                string full = Path.GetFullPath(f.FullName);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return rel.Length > 0 ? rel : f.Name;
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("LogViewerForm.RelativePath", ex); }
            return f.Name;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        // ───────────────────────── 读取 ─────────────────────────

        private void LoadSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            _currentPath = (string)_list.SelectedItems[0].Tag;
            _currentLength = -1;
            _hexOffset = 0;
            ReloadCurrent();
        }

        private bool IsHexMode { get { return _chkHex != null && _chkHex.Checked; } }

        private void ReloadCurrent()
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            var fi = new FileInfo(_currentPath);
            if (!fi.Exists)
            {
                _viewer.Text = L10n.T("文件已被删除或移动。");
                return;
            }
            _currentLength = fi.Length;
            _currentMtime = fi.LastWriteTimeUtc;

            bool hex = IsHexMode;
            _btnPrevPage.Visible = hex;
            _btnNextPage.Visible = hex;
            _chkTail.Enabled = !hex;   // 十六进制分页视图不跟随，否则翻页被拽回尾巴

            try
            {
                if (hex)
                {
                    if (_hexOffset > Math.Max(0, fi.Length - 1)) _hexOffset = 0;
                    LoadHexPage(fi);
                }
                else
                {
                    LoadTextTail(fi);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("LogViewerForm.ReloadCurrent", ex);
                _viewer.Text = L10n.T("读取失败：") + ex.Message;
            }
        }

        private void LoadTextTail(FileInfo fi)
        {
            long len = fi.Length;
            long start = Math.Max(0, len - TextTailBytes);

            byte[] buf;
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(start, SeekOrigin.Begin);
                int want = (int)Math.Min(TextTailBytes, len - start);
                buf = new byte[want];
                int read = 0;
                while (read < want)
                {
                    int n = fs.Read(buf, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < want) Array.Resize(ref buf, read);
            }

            var sb = new StringBuilder(buf.Length + 128);
            if (start > 0)
                sb.Append(L10n.Tf("（文件共 {0}，仅显示末尾 {1}；更多内容用「用系统程序打开」）",
                    FormatSize(len), FormatSize(buf.Length))).Append("\r\n");
            // UTF-8 严格解码会因半行截断/历史 GBK 内容抛异常，这里用替换字符兜底（比抛错好）
            sb.Append(Encoding.UTF8.GetString(buf).Replace("\r\n", "\n").Replace("\n", "\r\n"));

            _viewer.Text = sb.ToString();
            if (start == 0) _viewer.SelectionStart = 0;
            SetStatus(L10n.Tf("总计 {0} · {1} 行", FormatSize(len),
                _viewer.Lines.Length.ToString(CultureInfo.InvariantCulture)));
        }

        private void LoadHexPage(FileInfo fi)
        {
            long len = fi.Length;
            byte[] buf;
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(_hexOffset, SeekOrigin.Begin);
                int want = (int)Math.Min(HexPageBytes, Math.Max(0, len - _hexOffset));
                buf = new byte[want];
                int read = 0;
                while (read < want)
                {
                    int n = fs.Read(buf, read, want - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < want) Array.Resize(ref buf, read);
            }

            var header = L10n.Tf("总计 {0} · 偏移 0x{1}–0x{2}（每页 {3} 字节）",
                FormatSize(len),
                _hexOffset.ToString("x", CultureInfo.InvariantCulture),
                Math.Min(len, _hexOffset + buf.Length).ToString("x", CultureInfo.InvariantCulture),
                HexPageBytes.ToString(CultureInfo.InvariantCulture));
            var sb = new StringBuilder();
            sb.Append(header).Append("\r\n").Append("\r\n");
            foreach (var line in HexDump.FormatLines(buf, _hexOffset, 0))
                sb.Append(line).Append("\r\n");

            _viewer.Text = sb.ToString();
            _viewer.SelectionStart = 0;
            _btnPrevPage.Enabled = _hexOffset > 0;
            _btnNextPage.Enabled = _hexOffset + HexPageBytes < len;
            SetStatus(header);
        }

        private void PageHex(int direction)
        {
            if (!IsHexMode || string.IsNullOrEmpty(_currentPath)) return;
            _hexOffset = Math.Max(0, _hexOffset + direction * HexPageBytes);
            if (_hexOffset > TextTailBytes * 1024L) _hexOffset = 0;   // 防溢出式误翻
            ReloadCurrent();
        }

        private void TailTick()
        {
            if (IsDisposed || !_chkTail.Checked || IsHexMode || string.IsNullOrEmpty(_currentPath)) return;
            try
            {
                var fi = new FileInfo(_currentPath);
                if (!fi.Exists || fi.Length == _currentLength) return;
                ReloadCurrent();
                // 停在末尾：跟随的本意就是"看到最新一行"
                _viewer.SelectionStart = _viewer.TextLength;
                _viewer.ScrollToCaret();
            }
            catch (Exception ex) { DiagLog.Swallowed("LogViewerForm.TailTick", ex); }
        }

        // ───────────────────────── 查找 / 外部打开 ─────────────────────────

        private void Find(bool forward)
        {
            string needle = _search != null ? _search.Text : null;
            if (string.IsNullOrEmpty(needle) || _viewer.TextLength == 0) return;

            string hay = _viewer.Text;
            int from = _viewer.SelectionStart + (forward ? _viewer.SelectionLength : 0);
            int idx;
            if (forward)
            {
                idx = from <= 0 ? hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase)
                                : hay.IndexOf(needle, Math.Min(from, hay.Length), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase);  // 回绕
                if (idx < 0)
                {
                    SetStatus(L10n.T("未找到：") + needle);
                    return;
                }
            }
            else
            {
                int startAt = Math.Max(0, from - 1);
                idx = startAt >= hay.Length ? -1 : hay.LastIndexOf(needle, startAt, StringComparison.OrdinalIgnoreCase);
                if (idx < 0 && startAt < hay.Length)
                    idx = hay.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    SetStatus(L10n.T("未找到：") + needle);
                    return;
                }
            }

            _viewer.Select(idx, needle.Length);
            try { _viewer.ScrollToCaret(); } catch (Exception ex) { DiagLog.Swallowed("LogViewerForm.ScrollToCaret", ex); }
            _viewer.Focus();
            SetStatus(L10n.Tf("位置 {0} / {1}", idx.ToString(CultureInfo.InvariantCulture),
                _viewer.TextLength.ToString(CultureInfo.InvariantCulture)));
        }

        private void OpenExternal()
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            try
            {
                if (!File.Exists(_currentPath)) return;
                System.Diagnostics.Process.Start("notepad.exe", "\"" + _currentPath + "\"");
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("LogViewerForm.OpenExternal", ex);
                SetStatus(L10n.T("打开失败：") + ex.Message);
            }
        }
    }
}
