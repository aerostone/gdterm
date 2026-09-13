using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Sftp;
using Gdterm.Sftp.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 轻量 SFTP 浏览器——列表/上传/下载/新建目录/删除
    /// </summary>
    public class SftpBrowserPanel : UserControl, IDisposable
    {
        private readonly ConnectionConfig _config;
        private readonly CredentialPayload _credential;
        private readonly ISftpServiceFactory _factory;
        private readonly ITunnelManager _tunnelManager;
        private ISftpService _sftp;
        private ListView _list;
        private AntdUI.Input _pathBox;
        private AntdUI.Label _status;
        private string _currentPath = "/";
        private bool _disposed;

        public SftpBrowserPanel(
            ConnectionConfig config,
            CredentialPayload credential,
            ISftpServiceFactory factory,
            ITunnelManager tunnelManager)
        {
            _config = config;
            _credential = credential;
            _factory = factory;
            _tunnelManager = tunnelManager;
            BuildUI();
            ShownConnect();
        }

        private void BuildUI()
        {
            BackColor = GdtermColorTable.Background;
            Dock = DockStyle.Fill;

            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 4))
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _pathBox = new AntdUI.Input {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Text = "/",
                Margin = new Padding(DpiScale.V(this, 2))
            };
            _pathBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    _currentPath = _pathBox.Text;
                    RefreshList();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            var btnRefresh = MakeBtn("刷新", (s, e) => RefreshList());
            var btnUp = MakeBtn("上级", (s, e) =>
            {
                if (_currentPath == "/" || string.IsNullOrEmpty(_currentPath)) return;
                var p = _currentPath.TrimEnd('/');
                var idx = p.LastIndexOf('/');
                _currentPath = idx <= 0 ? "/" : p.Substring(0, idx);
                _pathBox.Text = _currentPath;
                RefreshList();
            });
            var btnUpload = MakeBtn("上传", (s, e) => Upload());
            var btnDownload = MakeBtn("下载", (s, e) => Download());
            var btnMkdir = MakeBtn("新建目录", (s, e) => Mkdir());
            var btnRename = MakeBtn("重命名", (s, e) => RenameSelected());
            var btnSyncUp = MakeBtn("同步上传", (s, e) => SyncUpload());
            var btnSyncDown = MakeBtn("同步下载", (s, e) => SyncDownload());
            var btnDelete = MakeBtn("删除", (s, e) => DeleteSelected());

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(DpiScale.V(this, 2))
            };
            buttons.Controls.AddRange(new Control[] { btnUp, btnRefresh, btnUpload, btnDownload, btnMkdir, btnRename, btnSyncUp, btnSyncDown, btnDelete });

            top.Controls.Add(_pathBox, 0, 0);
            top.Controls.Add(buttons, 1, 0);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9.5f)
            };
            _list.Columns.Add("名称", 280);
            _list.Columns.Add("大小", 90);
            _list.Columns.Add("权限", 90);
            _list.Columns.Add("修改时间", 150);
            _list.DoubleClick += (s, e) =>
            {
                if (_list.SelectedItems.Count == 0) return;
                var info = _list.SelectedItems[0].Tag as SftpFileInfo;
                if (info == null) return;
                if (info.IsDirectory)
                {
                    _currentPath = Combine(_currentPath, info.Name);
                    _pathBox.Text = _currentPath;
                    RefreshList();
                }
                else
                {
                    Download(); // 双击文件 = 下载（WindTerm 惯例）
                }
            };
            _list.ContextMenuStrip = BuildContextMenu();

            _status = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                Height = Math.Max(DpiScale.V(this, 22), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this)),
                ForeColor = GdtermColorTable.Muted,
                Text = "正在连接..."
            };

            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(top);
        }

        private AntdUI.Button MakeBtn(string text, EventHandler onClick)
        {
            var b = new AntdUI.Button {
                Text = text,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3)),
                BackColor = GdtermColorTable.Hover,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(2)
            };
            b.Click += onClick;
            return b;
        }

        private async void ShownConnect()
        {
            try
            {
                _sftp = _factory.Create();
                if (_config.Tunnel != null && _tunnelManager != null)
                {
                    var ep = await _tunnelManager.EstablishAsync(_config, _credential, CancellationToken.None);
                    await _sftp.ConnectViaTunnelAsync(_config, _credential, ep, CancellationToken.None);
                }
                else
                {
                    await _sftp.ConnectAsync(_config, _credential, CancellationToken.None);
                }
                _status.Text = "已连接 " + _config.Host;
                RefreshList();
            }
            catch (Exception ex)
            {
                _status.Text = "连接失败: " + ex.Message;
            }
        }

        private async void RefreshList()
        {
            if (_sftp == null || !_sftp.IsConnected) return;
            try
            {
                _status.Text = "加载 " + _currentPath + " ...";
                var items = await _sftp.ListDirectoryAsync(_currentPath, CancellationToken.None);
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var f in items)
                {
                    if (f.Name == "." || f.Name == "..") continue;
                    var item = new ListViewItem(f.Name);
                    item.SubItems.Add(f.IsDirectory ? "<DIR>" : f.SizeBytes.ToString());
                    item.SubItems.Add(f.Permissions ?? "");
                    item.SubItems.Add(f.LastModified.ToString("yyyy-MM-dd HH:mm"));
                    item.ForeColor = f.IsDirectory ? GdtermColorTable.Info : GdtermColorTable.Foreground;
                    item.Tag = f;
                    _list.Items.Add(item);
                }
                _list.EndUpdate();
                _status.Text = _currentPath + "  (" + _list.Items.Count + " 项)";
            }
            catch (Exception ex)
            {
                _status.Text = "列表失败: " + ex.Message;
            }
        }

        /// <summary>同步上传：选本地文件夹 → 与当前远端目录比对 → 确认 → 逐文件续传。</summary>
        private async void SyncUpload()
        {
            if (_sftp == null || !_sftp.IsConnected) return;
            using (var dlg = new FolderBrowserDialog { Description = "选择要同步上传的本地文件夹" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string localDir = dlg.SelectedPath;
                string remoteBase = string.IsNullOrEmpty(_currentPath) ? "/" : _currentPath;
                _status.Text = "比对中…";
                try
                {
                    var cts = new CancellationTokenSource();
                    var tree = await Gdterm.Sftp.SftpSyncRunner.ListRemoteTreeAsync(_sftp, remoteBase, cts.Token);
                    var plan = Gdterm.Sftp.SftpSyncPlanner.PlanUpload(localDir, remoteBase, tree);
                    int need = 0;
                    foreach (var item in plan) if (item.Action == Gdterm.Sftp.SyncAction.Transfer) need++;
                    if (plan.Count == 0) { _status.Text = "本地无文件"; return; }
                    if (need == 0)
                    {
                        _status.Text = "已是最新";
                        ToastNotifier.Success("同步上传：两端一致，无需传输");
                        TransferCenterPanel.Record("同步上传跳过（已是最新）  " + localDir + " → " + remoteBase);
                        return;
                    }
                    if (MessageBox.Show("需传输 " + need + " 个文件（共 " + plan.Count + " 个），开始同步？", "同步上传",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    using (var progressDlg = new TransferProgressDialog("同步上传 " + need + " 个文件"))
                    {
                        progressDlg.Show(this);
                        var progress = new TransferProgressAdapter(progressDlg, "同步上传");
                        var result = await Gdterm.Sftp.SftpSyncRunner.RunUploadAsync(_sftp, plan, progress, cts.Token);
                        progressDlg.Complete(result.Failed == 0, "传 " + result.Transferred + " 跳过 " + result.Skipped + " 失败 " + result.Failed);
                        _status.Text = "同步完成";
                        ToastNotifier.Success("同步上传完成：传 " + result.Transferred + " 跳过 " + result.Skipped + " 失败 " + result.Failed);
                        TransferCenterPanel.Record("同步上传完成  " + localDir + " → " + remoteBase + "  传" + result.Transferred + " 跳过" + result.Skipped + " 失败" + result.Failed);
                        RefreshList();
                    }
                }
                catch (Exception ex)
                {
                    _status.Text = "同步失败";
                    ToastNotifier.Error("同步上传失败: " + ex.Message);
                    TransferCenterPanel.Record("同步上传失败  " + localDir + " → " + remoteBase + "  " + ex.Message);
                }
            }
        }

        /// <summary>同步下载：当前远端目录 → 选本地文件夹 → 比对 → 确认 → 逐文件续传。</summary>
        private async void SyncDownload()
        {
            if (_sftp == null || !_sftp.IsConnected) return;
            using (var dlg = new FolderBrowserDialog { Description = "选择同步下载到的本地文件夹" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string localDir = dlg.SelectedPath;
                string remoteBase = string.IsNullOrEmpty(_currentPath) ? "/" : _currentPath;
                _status.Text = "比对中…";
                try
                {
                    var cts = new CancellationTokenSource();
                    var tree = await Gdterm.Sftp.SftpSyncRunner.ListRemoteTreeAsync(_sftp, remoteBase, cts.Token);
                    var plan = Gdterm.Sftp.SftpSyncPlanner.PlanDownload(tree, localDir, remoteBase);
                    int need = 0;
                    foreach (var item in plan) if (item.Action == Gdterm.Sftp.SyncAction.Transfer) need++;
                    if (plan.Count == 0) { _status.Text = "远端无文件"; return; }
                    if (need == 0)
                    {
                        _status.Text = "已是最新";
                        ToastNotifier.Success("同步下载：两端一致，无需传输");
                        TransferCenterPanel.Record("同步下载跳过（已是最新）  " + remoteBase + " → " + localDir);
                        return;
                    }
                    if (MessageBox.Show("需传输 " + need + " 个文件（共 " + plan.Count + " 个），开始同步？", "同步下载",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    using (var progressDlg = new TransferProgressDialog("同步下载 " + need + " 个文件"))
                    {
                        progressDlg.Show(this);
                        var progress = new TransferProgressAdapter(progressDlg, "同步下载");
                        var result = await Gdterm.Sftp.SftpSyncRunner.RunDownloadAsync(_sftp, plan, progress, cts.Token);
                        progressDlg.Complete(result.Failed == 0, "传 " + result.Transferred + " 跳过 " + result.Skipped + " 失败 " + result.Failed);
                        _status.Text = "同步完成";
                        ToastNotifier.Success("同步下载完成：传 " + result.Transferred + " 跳过 " + result.Skipped + " 失败 " + result.Failed);
                        TransferCenterPanel.Record("同步下载完成  " + remoteBase + " → " + localDir + "  传" + result.Transferred + " 跳过" + result.Skipped + " 失败" + result.Failed);
                    }
                }
                catch (Exception ex)
                {
                    _status.Text = "同步失败";
                    ToastNotifier.Error("同步下载失败: " + ex.Message);
                    TransferCenterPanel.Record("同步下载失败  " + remoteBase + " → " + localDir + "  " + ex.Message);
                }
            }
        }

        private async void Upload()
        {
            if (_sftp == null || !_sftp.IsConnected) return;
            using (var dlg = new OpenFileDialog { Title = "上传文件", Multiselect = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var name = Path.GetFileName(dlg.FileName);
                var remote = Combine(_currentPath, name);
                using (var progressDlg = new TransferProgressDialog("上传 " + name))
                {
                    progressDlg.Show(this);
                    try
                    {
                        _status.Text = "上传中 " + name;
                        var progress = new TransferProgressAdapter(progressDlg, name);
                        var cts = new CancellationTokenSource();
                        // 轮询取消
                        var task = _sftp.UploadResumeAsync(dlg.FileName, remote, progress, cts.Token);
                        while (!task.IsCompleted)
                        {
                            await Task.Delay(100);
                            if (progressDlg.IsCancelled)
                            {
                                try { cts.Cancel(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SftpBrowserPanel", exSwallowed); } catch { } }
                                break;
                            }
                            Application.DoEvents();
                        }
                        bool uploaded = await task;
                        if (!uploaded && !progressDlg.IsCancelled)
                        {
                            progressDlg.Complete(true, "已是最新，无需上传");
                            _status.Text = "已是最新";
                            ToastNotifier.Success("已是最新: " + name);
                            TransferCenterPanel.Record("上传跳过（已是最新）  " + name + " → " + remote);
                            RefreshList();
                        }
                        else if (progressDlg.IsCancelled)
                        {
                            progressDlg.Complete(false, "已取消");
                            _status.Text = "上传已取消";
                            ToastNotifier.Warning("上传已取消: " + name);
                            TransferCenterPanel.Record("上传取消  " + name + " → " + remote);
                        }
                        else
                        {
                            progressDlg.Complete(true, "上传完成");
                            ToastNotifier.Success("上传完成: " + name);
                            TransferCenterPanel.Record("上传完成  " + name + " → " + remote);
                            RefreshList();
                        }
                    }
                    catch (Exception ex)
                    {
                        progressDlg.Complete(false, ex.Message);
                        ToastNotifier.Error("上传失败: " + ex.Message);
                        _status.Text = "上传失败";
                        TransferCenterPanel.Record("上传失败  " + name + " → " + remote + "  " + ex.Message);
                    }
                }
            }
        }

        private async void Download()
        {
            if (_sftp == null || !_sftp.IsConnected || _list.SelectedItems.Count == 0) return;
            var info = _list.SelectedItems[0].Tag as SftpFileInfo;
            if (info == null || info.IsDirectory) return;

            using (var dlg = new SaveFileDialog { FileName = info.Name, Title = "下载到" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                using (var progressDlg = new TransferProgressDialog("下载 " + info.Name))
                {
                    progressDlg.Show(this);
                    var remote = Combine(_currentPath, info.Name);
                    try
                    {
                        _status.Text = "下载中 " + info.Name;
                        var progress = new TransferProgressAdapter(progressDlg, info.Name);
                        var cts = new CancellationTokenSource();
                        var task = _sftp.DownloadResumeAsync(remote, dlg.FileName, progress, cts.Token);
                        while (!task.IsCompleted)
                        {
                            await Task.Delay(100);
                            if (progressDlg.IsCancelled)
                            {
                                try { cts.Cancel(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SftpBrowserPanel", exSwallowed); } catch { } }
                                break;
                            }
                            Application.DoEvents();
                        }
                        bool downloaded = await task;
                        if (!downloaded && !progressDlg.IsCancelled)
                        {
                            progressDlg.Complete(true, "已是最新，无需下载");
                            _status.Text = "已是最新";
                            ToastNotifier.Success("已是最新: " + info.Name);
                            TransferCenterPanel.Record("下载跳过（已是最新）  " + remote + " → " + dlg.FileName);
                        }
                        else if (progressDlg.IsCancelled)
                        {
                            progressDlg.Complete(false, "已取消");
                            _status.Text = "下载已取消";
                            ToastNotifier.Warning("下载已取消: " + info.Name);
                            TransferCenterPanel.Record("下载取消  " + remote + " → " + dlg.FileName);
                        }
                        else
                        {
                            progressDlg.Complete(true, "下载完成");
                            _status.Text = "下载完成";
                            ToastNotifier.Success("下载完成: " + info.Name);
                            TransferCenterPanel.Record("下载完成  " + remote + " → " + dlg.FileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        progressDlg.Complete(false, ex.Message);
                        ToastNotifier.Error("下载失败: " + ex.Message);
                        _status.Text = "下载失败";
                        TransferCenterPanel.Record("下载失败  " + remote + " → " + dlg.FileName + "  " + ex.Message);
                    }
                }
            }
        }

        private async void Mkdir()
        {
            if (_sftp == null || !_sftp.IsConnected) return;
            var name = Prompt("新建目录", "目录名:");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                await _sftp.CreateDirectoryAsync(Combine(_currentPath, name.Trim()), CancellationToken.None);
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("创建失败: " + ex.Message, "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void DeleteSelected()
        {
            if (_sftp == null || !_sftp.IsConnected || _list.SelectedItems.Count == 0) return;
            var info = _list.SelectedItems[0].Tag as SftpFileInfo;
            if (info == null) return;
            if (MessageBox.Show("确认删除 " + info.Name + " ?", "SFTP",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                await _sftp.DeleteAsync(Combine(_currentPath, info.Name), info.IsDirectory, CancellationToken.None);
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除失败: " + ex.Message, "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground
            };
            var miDownload = new ToolStripMenuItem("下载");
            miDownload.Click += (s, e) => Download();
            var miPreview = new ToolStripMenuItem("预览");
            miPreview.Click += (s, e) => PreviewSelected();
            var miPerm = new ToolStripMenuItem("权限...");
            miPerm.Click += (s, e) => EditPermissionSelected();
            var miRename = new ToolStripMenuItem("重命名...");
            miRename.Click += (s, e) => RenameSelected();
            var miDelete = new ToolStripMenuItem("删除");
            miDelete.Click += (s, e) => DeleteSelected();
            menu.Items.Add(miDownload);
            menu.Items.Add(miPreview);
            menu.Items.Add(miPerm);
            menu.Items.Add(miRename);
            menu.Items.Add(miDelete);
            menu.Items.Add(new ToolStripSeparator());
            var miRefresh = new ToolStripMenuItem("刷新");
            miRefresh.Click += (s, e) => RefreshList();
            menu.Items.Add(miRefresh);
            return menu;
        }

        private void RenameSelected()
        {
            if (_sftp == null || !_sftp.IsConnected || _list.SelectedItems.Count == 0) return;
            var info = _list.SelectedItems[0].Tag as SftpFileInfo;
            if (info == null) return;
            using (var dlg = new Gdterm.UI.Forms.TextInputForm("重命名", "新名称：", info.Name))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                var newName = (dlg.InputText ?? "").Trim();
                if (string.IsNullOrEmpty(newName) || newName == info.Name) return;
                if (newName.IndexOf('/') >= 0 || newName.IndexOf('\\') >= 0)
                {
                    MessageBox.Show("名称不能包含路径分隔符。", "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    _sftp.RenameAsync(Combine(_currentPath, info.Name), Combine(_currentPath, newName), CancellationToken.None).GetAwaiter().GetResult();
                    RefreshList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("重命名失败: " + ex.Message, "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>预览选中文件：文本前 100 行弹窗，图片下载到 temp 外部打开（与 FilePaneControl 同行为）。</summary>
        private void PreviewSelected()
        {
            if (_sftp == null || !_sftp.IsConnected || _list.SelectedItems.Count == 0) return;
            var info = _list.SelectedItems[0].Tag as SftpFileInfo;
            if (info == null || info.IsDirectory) return;
            if (Gdterm.Sftp.SftpEnhancements.IsImageFile(info.Name))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "gdterm_img_" + Guid.NewGuid().ToString("N") + Path.GetExtension(info.Name));
                try
                {
                    _sftp.DownloadAsync(Combine(_currentPath, info.Name), tmp, null, CancellationToken.None).GetAwaiter().GetResult();
                    try { System.Diagnostics.Process.Start(tmp); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SftpBrowserPanel", exSwallowed); } catch { } }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), "图片打开失败:\n" + ex.Message, "预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }
            if (!Gdterm.Sftp.SftpEnhancements.IsTextFile(info.Name))
            {
                MessageBox.Show(FindForm(), "该类型暂不支持预览（仅文本/图片）。", "预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _status.Text = "预览加载 " + info.Name + " …";
            var remote = Combine(_currentPath, info.Name);
            Task.Run(async () =>
            {
                try { return await Gdterm.Sftp.SftpEnhancements.PreviewTextFileAsync(_sftp, remote, 100, CancellationToken.None); }
                catch (Exception ex) { return "预览失败: " + ex.Message; }
            }).ContinueWith(t =>
            {
                _status.Text = remote;
                PreviewBoxShim.Show(FindForm(), info.Name, t.Result ?? "");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>权限：WinSCP 式 3x3 对话框走 SFTP chmod（与 FilePaneControl.ShowPropsSelected 同行为）。</summary>
        private void EditPermissionSelected()
        {
            if (_sftp == null || !_sftp.IsConnected || _list.SelectedItems.Count == 0) return;
            var info = _list.SelectedItems[0].Tag as SftpFileInfo;
            if (info == null) return;
            int octal = Gdterm.Sftp.SftpEnhancements.ParsePermissionToOctal(info.Permissions ?? "");
            using (var dlg = new Gdterm.UI.Forms.SftpPermissionForm(info.Name, info.Permissions, octal))
            {
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    _sftp.ChmodAsync(Combine(_currentPath, info.Name), dlg.OctalMode, CancellationToken.None).GetAwaiter().GetResult();
                    _status.Text = "权限已修改";
                    RefreshList();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(), "修改权限失败: " + ex.Message, "SFTP", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string Combine(string basePath, string name)
        {
            if (string.IsNullOrEmpty(basePath) || basePath == "/") return "/" + name.TrimStart('/');
            return basePath.TrimEnd('/') + "/" + name.TrimStart('/');
        }

        private static string Prompt(string title, string label)
        {
            var f = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = GdtermColorTable.Surface
            };
            using (f)
            {
                f.Size = DpiScale.S(f, 360, 140);
                var lbl = new AntdUI.Label { Text = label, ForeColor = GdtermColorTable.Foreground, Location = DpiScale.P(f, 12, 12), AutoSize = true };
                var box = new AntdUI.Input { Location = DpiScale.P(f, 12, 40), Width = DpiScale.V(f, 320),
                    MinimumSize = new Size(0, Math.Max(DpiScale.V(f, 38), Services.FormFontPolicy.RowStep(f))),
                    BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground };
                var ok = new AntdUI.Button { Text = "确定", DialogResult = DialogResult.OK, Location = DpiScale.P(f, 250, 76),
                    Size = DpiScale.S(f, 84, 32) };
                f.Controls.AddRange(new Control[] { lbl, box, ok });
                f.AcceptButton = ok;
                return f.ShowDialog() == DialogResult.OK ? box.Text : null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    try { _sftp?.Disconnect(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SftpBrowserPanel", exSwallowed); } catch { } }
                    try { _sftp?.Dispose(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SftpBrowserPanel", exSwallowed); } catch { } }
                }
            }
            base.Dispose(disposing);
        }
    }
}
