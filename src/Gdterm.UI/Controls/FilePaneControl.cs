using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

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
        private ListShell _shell;
        private EmptyStateView _empty;
        private ContextMenuStrip _toolbarOverflowMenu;
        private AntdUI.Label _status;
        private string _currentPath;
        private string _requestedPath;
        private bool _busy;
        private bool _closed;
        private bool _hasPendingNavigation;
        private string _pendingNavigation;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        internal IFilePaneProvider AcceptedDragSource { get; set; }
        public event Action<FileEntry[]> ResumeToPeerRequested;

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

            var top = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 4), DpiScale.V(this, 6), DpiScale.V(this, 4))
            };


            var btnHome = MakeBtn("⌂", L10n.T("主目录"), (s, e) => Navigate(_provider.HomePath));
            var btnUp = MakeBtn("↑", L10n.T("上级目录 (Backspace)"), (s, e) => NavigateUp());
            var btnRefresh = MakeBtn("⟳", L10n.T("刷新 (F5)"), (s, e) => Refresh());
            var btnMkdir = MakeBtn("＋", L10n.T("新建目录 (F7)"), (s, e) => Mkdir());
            var btnRename = MakeBtn("改", L10n.T("重命名 (F2)"), (s, e) => RenameSelected());
            var btnDelete = MakeBtn("✕", L10n.T("删除 (Del)"), (s, e) => DeleteSelected());

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var toolbarButtons = new[] { btnHome, btnUp, btnRefresh, btnMkdir, btnRename, btnDelete };
            Action[] toolbarActions = { () => Navigate(_provider.HomePath), NavigateUp, Refresh, Mkdir, RenameSelected, DeleteSelected };
            _toolbarOverflowMenu = new ContextMenuStrip();
            var overflowItems = new ToolStripMenuItem[toolbarButtons.Length];
            for (int i = 0; i < toolbarButtons.Length; i++)
            {
                var action = toolbarActions[i];
                var item = new ToolStripMenuItem(toolbarButtons[i].AccessibleName);
                item.Click += (sender, args) => action();
                overflowItems[i] = item;
                _toolbarOverflowMenu.Items.Add(item);
            }
            var overflow = MakeBtn("…", L10n.T("更多"), (sender, args) =>
                _toolbarOverflowMenu.Show((Control)sender, new Point(0, ((Control)sender).Height)));
            overflow.Name = "FilePaneMoreActions";
            buttons.Controls.AddRange(toolbarButtons);
            buttons.Controls.Add(overflow);

            _pathBox = new AntdUI.Input {
                Dock = DockStyle.Top,
                Height = Services.FormFontPolicy.FieldHeight(this),
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont()
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

            top.Controls.Add(buttons);
            top.Controls.Add(_pathBox);
            bool fittingToolbar = false;
            Action fitToolbar = () =>
            {
                if (fittingToolbar) return;
                fittingToolbar = true;
                try
                {
                    int available = buttons.ClientSize.Width;
                    bool more = toolbarButtons.Sum(button => button.Width + button.Margin.Horizontal) > available;
                    int used = more ? overflow.Width + overflow.Margin.Horizontal : 0;
                    for (int i = 0; i < toolbarButtons.Length; i++)
                    {
                        var button = toolbarButtons[i];
                        bool show = !more || used + button.Width + button.Margin.Horizontal <= available;
                        button.Visible = show;
                        overflowItems[i].Available = !show;
                        if (show) used += button.Width + button.Margin.Horizontal;
                    }
                    overflow.Visible = more;
                    buttons.Height = Math.Max(overflow.Height + overflow.Margin.Vertical,
                        toolbarButtons.Max(button => button.Height + button.Margin.Vertical));
                    int height = _pathBox.Height + buttons.Height + top.Padding.Vertical;
                    if (top.Height != height) top.Height = height;
                }
                finally { fittingToolbar = false; }
            };
            buttons.SizeChanged += (s, e) => fitToolbar();
            foreach (var button in toolbarButtons)
            {
                button.SizeChanged += (s, e) => fitToolbar();
                button.MarginChanged += (s, e) => fitToolbar();
            }
            overflow.SizeChanged += (s, e) => fitToolbar();
            overflow.MarginChanged += (s, e) => fitToolbar();
            _pathBox.SizeChanged += (s, e) => fitToolbar();
            fitToolbar();

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HeaderStyle = ColumnHeaderStyle.Clickable,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                AllowDrop = true
            };
            _list.OwnerDraw = true;
            _list.DrawColumnHeader += (sender, args) =>
            {
                using (var brush = new SolidBrush(GdtermColorTable.Surface)) args.Graphics.FillRectangle(brush, args.Bounds);
                TextRenderer.DrawText(args.Graphics, args.Header.Text, _list.Font, args.Bounds,
                    GdtermColorTable.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            _list.DrawItem += (sender, args) => args.DrawDefault = true;
            _list.DrawSubItem += (sender, args) => args.DrawDefault = true;
            _list.Columns.Add(L10n.T("名称"), DpiScale.V(this, 240));
            _list.Columns.Add(L10n.T("大小"), DpiScale.V(this, 80));
            _list.Columns.Add(L10n.T("修改时间"), DpiScale.V(this, 130));
            _list.Columns.Add(L10n.T("权限"), DpiScale.V(this, 90));
            _list.DoubleClick += (s, e) => OpenSelected();
            _list.KeyDown += OnListKeyDown;

            // 拖拽源：列表项拖出（自定义格式携带 FileEntry，供跨面板传输）
            _list.ItemDrag += (s, e) =>
            {
                if (_list.SelectedItems.Count == 0) return;
                var entries = SelectedEntries();
                if (entries.Length == 0) return;
                var data = new DataObject();
                data.SetData("GdtermFileEntries", false, new FilePaneDrag(_provider, entries));
                try { _list.DoDragDrop(data, DragDropEffects.Copy); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("FilePaneControl", exSwallowed); } catch { } }
            };

            WireDropTarget(_list);

            _status = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                AutoEllipsis = true,
                // 固定 22 在大字号下裁字 → 行盒跟随（其余 Dock 标题同）
                Height = Math.Max(DpiScale.V(this, 22), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this)),
                ForeColor = GdtermColorTable.Muted,
                Text = " ",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 6), 0, 0, 0)
            };

            _list.ContextMenuStrip = BuildContextMenu();

            // 列表壳：过滤 + 排序 + 导出（目录文件多时按名称/大小/修改时间找某个文件；
            // 勾选批量走右键/拖拽，不受过滤影响，故 showFilter:true 安全）
            _shell = new ListShell(_list, L10n.T("过滤文件名…"), minimumListHeight: 96);
            _shell.AddAction(L10n.T("导出"), (s, e) => _shell.ExportCsv(FindForm(), "file-list")).Name = "FileListExportButton";

            Controls.Add(_shell);
            Controls.Add(_status);
            Controls.Add(top);

            // 审计 C1：空目录/读取失败此前都只是一片空列表（状态栏那行字容易被忽略）
            _empty = EmptyStateView.Attach(_shell, L10n.T("这个目录是空的"),
                L10n.T("可从对侧拖入文件；点「刷新」重新读取"), L10n.T("刷新"), (s2, e2) => Refresh());
            WireDropTarget(_empty);
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        internal bool CanAcceptDrop(FilePaneDrag drag)
        {
            return !_closed && Enabled && !_busy && !string.IsNullOrEmpty(_currentPath)
                && drag != null && drag.Entries != null && drag.Entries.Length > 0
                && AcceptedDragSource != null && ReferenceEquals(drag.Source, AcceptedDragSource)
                && !ReferenceEquals(drag.Source, _provider);
        }

        private void WireDropTarget(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += (sender, args) =>
                args.Effect = CanAcceptDrop(args.Data.GetData("GdtermFileEntries") as FilePaneDrag) ? DragDropEffects.Copy : DragDropEffects.None;
            control.DragOver += (sender, args) =>
                args.Effect = CanAcceptDrop(args.Data.GetData("GdtermFileEntries") as FilePaneDrag) ? DragDropEffects.Copy : DragDropEffects.None;
            control.DragDrop += (sender, args) =>
            {
                var drag = args.Data.GetData("GdtermFileEntries") as FilePaneDrag;
                if (CanAcceptDrop(drag)) EntriesDropped?.Invoke(drag.Entries);
            };
            foreach (Control child in control.Controls) WireDropTarget(child);
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var ctx = new ContextMenuStrip();
            ctx.BackColor = GdtermColorTable.Surface;
            ctx.ForeColor = GdtermColorTable.Foreground;

            var miTransfer = new ToolStripMenuItem(L10n.T("传输到对侧")) { Enabled = false };
            miTransfer.Click += (s, e) => RequestTransferToPeer();
            var miResume = new ToolStripMenuItem(L10n.T("校验后续传到对侧")) { Enabled = false };
            miResume.Click += (sender, args) => ResumeToPeerRequested?.Invoke(SelectedEntries());
            var miRename = new ToolStripMenuItem(L10n.T("重命名")) { Enabled = false };
            miRename.Click += (s, e) => RenameSelected();
            var miMkdir = new ToolStripMenuItem(L10n.T("新建目录"));
            miMkdir.Click += (s, e) => Mkdir();
            var miDelete = new ToolStripMenuItem(L10n.T("删除")) { Enabled = false };
            miDelete.Click += (s, e) => DeleteSelected();
            var miPreview = new ToolStripMenuItem(L10n.T("预览 (F3)")) { Enabled = false };
            miPreview.Click += (s, e) => PreviewSelected();
            var miProps = new ToolStripMenuItem(L10n.T("属性...")) { Enabled = false };
            miProps.Click += (s, e) => ShowPropsSelected();
            var miRefresh = new ToolStripMenuItem(L10n.T("刷新"));
            miRefresh.Click += (s, e) => Refresh();

            _list.ItemSelectionChanged += (s, e) =>
            {
                var has = _list.SelectedItems.Count > 0;
                miTransfer.Enabled = has;
                miResume.Enabled = has;
                miRename.Enabled = has;
                miDelete.Enabled = has;
                miPreview.Enabled = has;
                miProps.Enabled = has;
                UpdateStatus();
            };

            ctx.Items.Add(miTransfer);
            ctx.Items.Add(miResume);
            ctx.Items.Add(miRename);
            ctx.Items.Add(miPreview);
            ctx.Items.Add(miProps);
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
            else if (e.KeyCode == Keys.F3) { PreviewSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back) { NavigateUp(); e.Handled = true; }
        }

        private readonly ToolTip _tips = new ToolTip();

        private AntdUI.Button MakeBtn(string text, string tip, EventHandler onClick)
        {
            var b = new AntdUI.Button {
                Text = text,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Margin = DpiScale.Pad(this, 1, 0, 1, 0),
                TabStop = true
            };
            Services.FormFontPolicy.SizeActionButton(this, b, 5, 2);
            b.Click += onClick;
            b.AccessibleName = tip;
            _tips.SetToolTip(b, tip);
            return b;
        }

        private void UpdateStatus()
        {
            var sel = _list.SelectedItems.Count;
            if (sel == 0)
                _status.Text = _currentPath == null ? " " : (_currentPath.Length == 0 ? L10n.T("此电脑") : _currentPath);
            else
                _status.Text = sel + L10n.T(" 项已选");
        }

        private void OpenSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var entry = _list.SelectedItems[0].Tag as FileEntry;
            if (entry == null) return;
            if (entry.IsDirectory) Navigate(entry.FullPath);
            else PreviewSelected();
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
        public async void Navigate(string path)
        {
            if (_closed) return;
            if (!IsHandleCreated || _busy) { _pendingNavigation = path; _hasPendingNavigation = true; return; }
            _busy = true;
            _requestedPath = path;
            _list.Enabled = false;
            _status.Text = L10n.T("加载 ") + (string.IsNullOrEmpty(path) ? L10n.T("此电脑") : path) + L10n.T(" …");
            var ct = _lifetime.Token;
            try
            {
                var entries = await Task.Run(() => _provider.List(path, ct), ct);
                if (_closed) return;
                _currentPath = path ?? "";
                _pathBox.Text = string.IsNullOrEmpty(path) ? L10n.T("此电脑") : path;
                RenderList(entries);
                _status.Text = _pathBox.Text + L10n.T("  (") + entries.Count + L10n.T(" 项)");
                _empty.SetEmpty(entries.Count == 0, L10n.T("这个目录是空的"), L10n.T("可从对侧拖入文件；点「刷新」重新读取"));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_closed) return;
                _currentPath = null;
                RenderList(new List<FileEntry>());
                _status.Text = L10n.T("读取失败: ") + ex.Message;
                _empty.SetEmpty(true, L10n.T("读取失败"), ex.Message);
                DiagLog.Error("FilePaneControl.Navigate", ex);
            }
            finally
            {
                _busy = false;
                if (!_closed)
                {
                    _list.Enabled = true;
                    DrainNavigation();
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke(new Action(() => { if (!_closed) DrainNavigation(); }));
        }

        private void DrainNavigation()
        {
            if (!_hasPendingNavigation || _closed) return;
            var path = _pendingNavigation;
            _hasPendingNavigation = false;
            Navigate(path);
        }

        public void Refresh()
        {
            if (_currentPath != null) Navigate(_currentPath);
            else Navigate(_requestedPath ?? _provider.HomePath);
        }

        public void NavigateUp()
        {
            if (_currentPath == null) return;
            var parent = _provider.ParentOf(_currentPath);
            if (parent != null) Navigate(parent);
        }

        private void RenderList(List<FileEntry> entries)
        {
            var items = new List<ListViewItem>();
            foreach (var f in entries)
            {
                var item = new ListViewItem(f.Name);
                item.SubItems.Add(f.IsSymbolicLink ? "<LINK>" : f.IsDirectory ? "<DIR>" : FormatSize(f.SizeBytes));
                item.SubItems.Add(f.LastModified == DateTime.MinValue ? "" : f.LastModified.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(f.Permissions ?? "");
                item.ForeColor = f.IsDirectory ? GdtermColorTable.Info : GdtermColorTable.Foreground;
                item.Tag = f;
                items.Add(item);
            }
            _shell.SetItems(items);
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
            if (_busy || string.IsNullOrEmpty(_currentPath)) return;
            var name = InputBox.Show(FindForm(), L10n.T("新建目录"), L10n.T("目录名:"));
            if (string.IsNullOrWhiteSpace(name)) return;
            RunFsOp(L10n.T("新建目录"), ct =>
            {
                _provider.Mkdir(_provider.Combine(_currentPath, name.Trim()), ct);
            }, refresh: true);
        }

        private void DeleteSelected()
        {
            var entries = SelectedEntries();
            if (entries.Length == 0) return;
            if (MessageBox.Show(FindForm(), L10n.T("确认删除 ") + DescribeEntries(entries) + L10n.T(" ?"), L10n.T("文件操作"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunFsOp(L10n.T("删除"), ct =>
            {
                foreach (var e in entries) { ct.ThrowIfCancellationRequested(); _provider.Delete(e.FullPath, e.IsDirectory, ct); }
            }, refresh: true);
        }

        private void RenameSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var entry = _list.SelectedItems[0].Tag as FileEntry;
            if (entry == null) return;
            var newName = InputBox.Show(FindForm(), L10n.T("重命名 ") + entry.Name, L10n.T("新名称:"), entry.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;
            RunFsOp(L10n.T("重命名"), ct =>
            {
                _provider.Rename(entry.FullPath, _provider.Combine(_currentPath, newName.Trim()), entry.IsDirectory, ct);
            }, refresh: true);
        }

        /// <summary>预览选中文件：远端文本→下载预览，远端图片→外部打开；本地文本→直接读，本地其他→外部打开。</summary>
        private async void PreviewSelected()
        {
            if (_closed || _list.SelectedItems.Count == 0) return;
            var entry = _list.SelectedItems[0].Tag as FileEntry;
            if (entry == null || entry.IsDirectory) return;
            var sftpProvider = _provider as SftpFilePaneProvider;
            if (sftpProvider != null)
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        _status.Text = L10n.T("预览加载 ") + entry.Name + L10n.T(" …");
                        if (Gdterm.Sftp.SftpEnhancements.IsImageFile(entry.Name))
                        {
                            const int limit = 8 * 1024 * 1024;
                            if (entry.SizeBytes > limit) throw new IOException(L10n.T("图片预览最大支持 8 MiB，请下载后打开。"));
                            var bytes = await sftpProvider.Sftp.ReadFilePrefixAsync(entry.FullPath, limit + 1, cts.Token);
                            if (bytes.Length > limit) throw new IOException(L10n.T("图片预览最大支持 8 MiB，请下载后打开。"));
                            if (!_closed) PreviewBox.ShowImage(FindForm(), entry.Name, bytes);
                        }
                        else if (Gdterm.Sftp.SftpEnhancements.IsTextFile(entry.Name))
                        {
                            var text = await Gdterm.Sftp.SftpEnhancements.PreviewTextFileAsync(sftpProvider.Sftp, entry.FullPath, 100, cts.Token);
                            if (!_closed) PreviewBox.Show(FindForm(), entry.Name, text);
                        }
                        else if (!_closed) MessageBox.Show(FindForm(), L10n.T("该类型暂不支持预览（仅文本/图片）。"), L10n.T("预览"));
                    }
                    catch (Exception ex)
                    {
                        if (!_closed) MessageBox.Show(FindForm(), ex is OperationCanceledException ? L10n.T("下载超时（30s）") : ex.Message,
                            L10n.T("预览"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    finally { if (!_closed) _status.Text = entry.FullPath; }
                }
                return;
            }
            // 本地
            try
            {
                var ext = Path.GetExtension(entry.Name).ToLower();
                if (Gdterm.Sftp.SftpEnhancements.IsImageFile(entry.Name)
                    || (!Gdterm.Sftp.SftpEnhancements.IsTextFile(entry.Name) && ext != ""))
                {
                    try { System.Diagnostics.Process.Start(entry.FullPath); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("FilePaneControl", exSwallowed); } catch { } }
                    return;
                }
                var lines = new List<string>();
                using (var reader = new StreamReader(entry.FullPath, System.Text.Encoding.UTF8, true))
                {
                    string line;
                    int count = 0;
                    while ((line = reader.ReadLine()) != null && count < 100) { lines.Add(line); count++; }
                    if (!reader.EndOfStream) lines.Add(L10n.T("... （仅显示前 100 行）"));
                }
                PreviewBox.Show(FindForm(), entry.Name, string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), L10n.T("预览失败:\n") + ex.Message,
                    L10n.T("预览"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>属性：远端→权限对话框走 SFTP chmod；本地→简单信息。</summary>
        private void ShowPropsSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var entry = _list.SelectedItems[0].Tag as FileEntry;
            if (entry == null) return;
            var sftpProvider = _provider as SftpFilePaneProvider;
            if (sftpProvider != null)
            {
                var sftp = sftpProvider.Sftp;
                if (sftp == null || !sftp.IsConnected) return;
                int octal = Gdterm.Sftp.SftpEnhancements.ParsePermissionToOctal(entry.Permissions ?? "");
                using (var dlg = new Gdterm.UI.Forms.SftpPermissionForm(entry.Name, entry.Permissions, octal))
                {
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                    int mode = dlg.OctalMode;
                    RunFsOp(L10n.T("修改权限"), ct =>
                    {
                        sftp.ChmodAsync(entry.FullPath, mode, ct).GetAwaiter().GetResult();
                    }, refresh: true);
                }
                return;
            }
            MessageBox.Show(FindForm(),
                L10n.T("名称: ") + entry.Name + L10n.T("\n大小: ") + FormatSize(entry.SizeBytes)
                + L10n.T("\n修改: ") + (entry.LastModified == DateTime.MinValue ? "-" : entry.LastModified.ToString("yyyy-MM-dd HH:mm")),
                L10n.T("属性"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async void RunFsOp(string opName, Action<CancellationToken> op, bool refresh)
        {
            if (_closed || _busy || _currentPath == null) return;
            _busy = true;
            _status.Text = opName + " …";
            var ct = _lifetime.Token;
            try
            {
                await Task.Run(() => op(ct), ct);
                if (!_closed) _status.Text = opName + L10n.T("完成");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_closed) MessageBox.Show(FindForm(), opName + L10n.T("失败:\n") + ex.Message,
                    L10n.T("文件操作"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                if (!_closed)
                {
                    if (_hasPendingNavigation) DrainNavigation();
                    else if (refresh) Refresh();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                _lifetime.Cancel();
                _toolbarOverflowMenu?.Dispose();
                _lifetime.Dispose();
                _tips.Dispose();
                _list.ContextMenuStrip?.Dispose();
            }
            base.Dispose(disposing);
        }

        private static string DescribeEntries(FileEntry[] entries)
        {
            if (entries.Length == 1)
            {
                var e = entries[0];
                return e.IsDirectory ? L10n.T("目录 ") + e.Name : L10n.T("文件 ") + e.Name;
            }
            return entries.Length + L10n.T(" 个条目");
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString();
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " K";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " M";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " G";
        }
    }

    internal static class PreviewBox
    {
        public static void ShowImage(IWin32Window owner, string title, byte[] bytes)
        {
            using (var memory = new MemoryStream(bytes))
            using (var image = Image.FromStream(memory, true, true))
            using (var form = new Form { Text = L10n.T("预览：") + title, StartPosition = FormStartPosition.CenterParent,
                BackColor = GdtermColorTable.Background, ShowInTaskbar = false })
            using (var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = image })
            {
                form.ClientSize = DpiScale.S(form, 640, 480);
                form.KeyPreview = true;
                form.KeyDown += (sender, args) => { if (args.KeyCode == Keys.Escape) form.Close(); };
                form.Controls.Add(picture);
                form.ShowDialog(owner);
            }
        }

        public static void Show(IWin32Window owner, string title, string text)
        {
            using (var f = new Form
            {
                Text = L10n.T("预览：") + (title ?? ""),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = true,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground
            })
            {
                f.ClientSize = new Size(DpiScale.V(f, 640), DpiScale.V(f, 440));
                f.MinimumSize = new Size(DpiScale.V(f, 420), DpiScale.V(f, 300));
                var box = new AntdUI.Input {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    Text = text ?? "",
                    Font = Services.FormFontPolicy.MonoFont(),
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground,
                };
                var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(DpiScale.V(f, 12), DpiScale.V(f, 6), DpiScale.V(f, 12), DpiScale.V(f, 6)) };
                var close = new AntdUI.Button {
                    Text = L10n.T("关闭"),
                    DialogResult = DialogResult.OK,
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground
                };
                Services.FormFontPolicy.SizeActionButton(f, close, 12, 4);
                flow.Controls.Add(close);
                Gdterm.UI.Services.FormFontPolicy.StyleDialogActions(f, flow);
                f.Controls.Add(box);
                f.Controls.Add(flow);
                f.AcceptButton = close;
                f.CancelButton = close;
                f.ShowDialog(owner);
            }
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
                var lbl = new AntdUI.Label { Text = label, Dock = DockStyle.Top, Height = 28, Padding = new Padding(DpiScale.V(f, 12), DpiScale.V(f, 8), DpiScale.V(f, 12), 0), ForeColor = GdtermColorTable.Muted };
                var box = new AntdUI.Input {
                    Dock = DockStyle.Top,
                    MinimumSize = new Size(0, FormFontPolicy.FieldHeight(f)),
                    Font = Services.FormFontPolicy.MonoFont(),
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground,
                };
                if (!string.IsNullOrEmpty(initial)) box.Text = initial;
                var flow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(DpiScale.V(f, 12), DpiScale.V(f, 6), DpiScale.V(f, 12), DpiScale.V(f, 6)) };
                var cancel = new AntdUI.Button {
                    Text = L10n.T("取消"),
                    DialogResult = DialogResult.Cancel,
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground
                };
                Services.FormFontPolicy.SizeActionButton(f, cancel, 12, 4);
                var ok = new AntdUI.Button {
                    Text = L10n.T("确定"),
                    DialogResult = DialogResult.OK,
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground
                };
                Services.FormFontPolicy.SizeActionButton(f, ok, 12, 4);
                flow.Controls.Add(cancel); // RightToLeft 流序：先加靠右
                flow.Controls.Add(ok);
                Gdterm.UI.Services.FormFontPolicy.StyleDialogActions(f, flow);
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
