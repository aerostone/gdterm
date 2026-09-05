using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 双栏面板的单侧文件栏——路径框、列表、右键菜单、拖拽源/目标、快捷键。
    /// 数据操作全部通过 IFilePaneProvider，在 Task.Run 中执行（provider 是同步 IO）。
    /// </summary>
    internal sealed class FilePaneControl : UserControl
    {
        private readonly IFilePaneProvider _provider;
        private AntdUI.Input _pathBox;
        private ListView _list;
        private AntdUI.Label _status;
        private string _currentPath;
        private bool _busy;

        /// <summary>另一面板的条目被拖到本面板放下（本面板是传输目标）。</summary>
        public event Action<FileEntry[]> EntriesDropped;

        /// <summary>本面板选中项请求传输到对侧（本面板是传输源）。</summary>
        public event Action<FileEntry[]> TransferToPeerRequested;

        public FilePaneControl(IFilePaneProvider provider)
        {
            _provider = provider;
            BuildUI();
        }

        public string CurrentPath { get { return _currentPath; } }

        public IFilePaneProvider Provider { get { return _provider; } }

        public bool IsBusy { get { return _busy; } }

        // ── UI ──────────────────────────────────────────────
        private void BuildUI()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            var top = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = GdtermColorTable.Surface, Padding = new Padding(6, 4, 6, 4) };

            var btnHome = MakeBtn("⌂", "主目录", (s, e) => Navigate(_provider.HomePath));
            var btnUp = MakeBtn("↑", "上级目录 (Backspace)", (s, e) => NavigateUp());
            var btnRefresh = MakeBtn("⟳", "刷新 (F5)", (s, e) => Refresh());
            var btnMkdir = MakeBtn("＋", "新建目录 (F7)", (s, e) => Mkdir());
            var btnRename = MakeBtn("改", "重命名 (F2)", (s, e) => RenameSelected());
            var btnDelete = MakeBtn("✕", "删除 (Del)", (s, e) => DeleteSelected());

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = DpiScale.V(this, 210),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            buttons.Controls.AddRange(new Control[] { btnHome, btnUp, btnRefresh, btnMkdir, btnRename, btnDelete });

            _pathBox = new AntdUI.Input {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", 9f)
            };
            _pathBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    Navigate(_pathBox.Text);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            top.Controls.Add(_pathBox);
            top.Controls.Add(buttons);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HeaderStyle = ColumnHeaderStyle.Clickable,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5f),
                AllowDrop = true
            };
            _list.Columns.Add("名称", DpiScale.V(this, 240));
            _list.Columns.Add("大小", DpiScale.V(this, 80));
            _list.Columns.Add("修改时间", DpiScale.V(this, 130));
            _list.Columns.Add("权限", DpiScale.V(this, 90));
            _list.DoubleClick += (s, e) => OpenSelected();
            _list.KeyDown += OnListKeyDown;

            // 拖拽源：列表项拖出（自定义格式携带 FileEntry，供跨面板传输）
            _list.ItemDrag += (s, e) =>
            {
                if (_list.SelectedItems.Count == 0) return;
                var entries = SelectedEntries();
                if (entries.Length == 0) return;
                var data = new DataObject();
                data.SetData("GdtermFileEntries", false, entries);
                try { _list.DoDragDrop(data, DragDropEffects.Copy); } catch { }
            };

            // 拖拽目标：接收另一面板的条目（上传/下载）
            _list.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent("GdtermFileEntries"))
                    e.Effect = DragDropEffects.Copy;
            };
            _list.DragDrop += (s, e) =>
            {
                var entries = e.Data.GetData("GdtermFileEntries") as FileEntry[];
                if (entries == null || entries.Length == 0) return;
                EntriesDropped?.Invoke(entries);
            };

            _status = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = GdtermColorTable.Muted,
                Text = " ",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0)
            };

            _list.ContextMenuStrip = BuildContextMenu();

            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(top);
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var ctx = new ContextMenuStrip();
            ctx.BackColor = GdtermColorTable.Surface;
            ctx.ForeColor = GdtermColorTable.Foreground;

            var miTransfer = new ToolStripMenuItem("传输到对侧") { Enabled = false };
            miTransfer.Click += (s, e) => RequestTransferToPeer();
            var miRename = new ToolStripMenuItem("重命名") { Enabled = false };
            miRename.Click += (s, e) => RenameSelected();
            var miMkdir = new ToolStripMenuItem("新建目录");
            miMkdir.Click += (s, e) => Mkdir();
            var miDelete = new ToolStripMenuItem("删除") { Enabled = false };
            miDelete.Click += (s, e) => DeleteSelected();
            var miRefresh = new ToolStripMenuItem("刷新");
            miRefresh.Click += (s, e) => Refresh();

            _list.ItemSelectionChanged += (s, e) =>
            {
                var has = _list.SelectedItems.Count > 0;
                miTransfer.Enabled = has;
                miRename.Enabled = has;
                miDelete.Enabled = has;
                UpdateStatus();
            };

            ctx.Items.Add(miTransfer);
            ctx.Items.Add(miRename);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(miMkdir);
            ctx.Items.Add(miDelete);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add(miRefresh);
            return ctx;
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { Refresh(); e.Handled = true; }
            else if (e.KeyCode == Keys.F7) { Mkdir(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back) { NavigateUp(); e.Handled = true; }
        }

        private readonly ToolTip _tips = new ToolTip();

        private Button MakeBtn(string text, string tip, EventHandler onClick)
        {
            var b = new AntdUI.Button {
                Text = text,
                Size = new Size(DpiScale.V(this, 30), DpiScale.V(this, 24)),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(1, 0, 1, 0),
                TabStop = false
            };
            b.Click += onClick;
            _tips.SetToolTip(b, tip);
            return b;
        }

        private void UpdateStatus()
        {
            var sel = _list.SelectedItems.Count;
            if (sel == 0)
                _status.Text = _currentPath == null ? " " : (_currentPath.Length == 0 ? "此电脑" : _currentPath);
            else
                _status.Text = sel + " 项已选";
        }

        private void OpenSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var entry = _list.SelectedItems[0].Tag as FileEntry;
            if (entry == null) return;
            if (entry.IsDirectory) Navigate(entry.FullPath);
        }

        public FileEntry[] SelectedEntries()
        {
            var list = new List<FileEntry>();
            foreach (ListViewItem item in _list.SelectedItems)
            {
                var e = item.Tag as FileEntry;
                if (e != null) list.Add(e);
            }
            return list.ToArray();
        }

        // ── 导航 ──────────────────────────────────────────
        public void Navigate(string path)
        {
            if (_busy) return;
            _busy = true;
            _status.Text = "加载 " + (string.IsNullOrEmpty(path) ? "此电脑" : path) + " …";
            var captured = path;
            Task.Run(() => _provider.List(captured)).ContinueWith(t =>
            {
                _busy = false;
                if (t.IsFaulted)
                {
                    _status.Text = "读取失败: " + t.Exception.GetBaseException().Message;
                    return;
                }
                var entries = t.Result;
                _currentPath = captured ?? "";   // null→""（本地盘符伪根），便于 Refresh 原地重列
                _pathBox.Text = string.IsNullOrEmpty(captured) ? "此电脑" : captured;
                RenderList(entries);
                _status.Text = (string.IsNullOrEmpty(captured) ? "此电脑" : captured) + "  (" + entries.Count + " 项)";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void Refresh()
        {
            if (_currentPath != null) Navigate(_currentPath);
            else Navigate(_provider.HomePath);
        }

        public void NavigateUp()
        {
            if (_currentPath == null) return;
            var parent = _provider.ParentOf(_currentPath);
            if (parent != null) Navigate(parent);
        }

        private void RenderList(List<FileEntry> entries)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var f in entries)
            {
                var item = new ListViewItem(f.Name);
                item.SubItems.Add(f.IsDirectory ? "<DIR>" : FormatSize(f.SizeBytes));
                item.SubItems.Add(f.LastModified == DateTime.MinValue ? "" : f.LastModified.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(f.Permissions ?? "");
                item.ForeColor = f.IsDirectory ? GdtermColorTable.Info : GdtermColorTable.Foreground;
                item.Tag = f;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
        }

        // └─ 拖拽/菜单触发的跨面板传输请求 → 宿主处理
        private void RequestTransferToPeer()
        {
            var entries = SelectedEntries();
            if (entries.Length > 0) TransferToPeerRequested?.Invoke(entries);
        }

        // ── 文件管理操作（同侧）──────────────────────────────
        private void Mkdir()
        {
            if (_currentPath == null) return;
            var name = InputBox.Show(FindForm(), "新建目录", "目录名:");
            if (string.IsNullOrWhiteSpace(name)) return;
            RunFsOp("新建目录", () =>
            {
                _provider.Mkdir(_provider.Combine(_currentPath, name.Trim()));
            }, refresh: true);
        }

        private void DeleteSelected()
        {
            var entries = SelectedEntries();
            if (entries.Length == 0) return;
            if (MessageBox.Show(FindForm(), "确认删除 " + DescribeEntries(entries) + " ?", "文件操作",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunFsOp("删除", () =>
            {
                foreach (var e in entries) _provider.Delete(e.FullPath, e.IsDirectory);
            }, refresh: true);
        }

        private void RenameSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var entry = _list.SelectedItems[0].Tag as FileEntry;
            if (entry == null) return;
            var newName = InputBox.Show(FindForm(), "重命名 " + entry.Name, "新名称:", entry.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;
            RunFsOp("重命名", () =>
            {
                _provider.Rename(entry.FullPath, _provider.Combine(_currentPath, newName.Trim()), entry.IsDirectory);
            }, refresh: true);
        }

        private void RunFsOp(string opName, Action op, bool refresh)
        {
            if (_busy || _currentPath == null) return;
            _busy = true;
            _status.Text = opName + " …";
            Task.Run(op).ContinueWith(t =>
            {
                _busy = false;
                if (t.IsFaulted)
                {
                    _status.Text = opName + "失败: " + t.Exception.GetBaseException().Message;
                    MessageBox.Show(FindForm(), opName + "失败:\n" + t.Exception.GetBaseException().Message,
                        "文件操作", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    _status.Text = opName + "完成";
                    if (refresh) Refresh();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static string DescribeEntries(FileEntry[] entries)
        {
            if (entries.Length == 1)
            {
                var e = entries[0];
                return e.IsDirectory ? "目录 " + e.Name : "文件 " + e.Name;
            }
            return entries.Length + " 个条目";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString();
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " K";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " M";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " G";
        }
    }

    /// <summary>暗色主题的输入对话框（新建目录/重命名共用）。</summary>
    internal static class InputBox
    {
        public static string Show(IWin32Window owner, string title, string label, string initial = null)
        {
            using (var f = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground
            })
            {
                f.ClientSize = new Size(DpiScale.V(f, 380), DpiScale.V(f, 128));
                var lbl = new AntdUI.Label { Text = label, Dock = DockStyle.Top, Height = 28, Padding = new Padding(12, 8, 12, 0), ForeColor = GdtermColorTable.Muted };
                var box = new AntdUI.Input {
                    Dock = DockStyle.Top,
                    Font = new Font("Consolas", 9.5f),
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground,
                };
                if (!string.IsNullOrEmpty(initial)) box.Text = initial;
                var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 6, 12, 6) };
                var cancel = new AntdUI.Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground };
                var ok = new AntdUI.Button { Text = "确定", DialogResult = DialogResult.OK, AutoSize = true, BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground };
                flow.Controls.Add(cancel); // RightToLeft 流序：先加靠右
                flow.Controls.Add(ok);
                f.Controls.Add(box);
                f.Controls.Add(lbl);
                f.Controls.Add(flow);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
