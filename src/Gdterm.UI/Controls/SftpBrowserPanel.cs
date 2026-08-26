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
        private TextBox _pathBox;
        private Label _status;
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
            BackColor = Color.FromArgb(30, 30, 30);
            Dock = DockStyle.Fill;

            var top = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(37, 37, 38) };
            _pathBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Text = "/"
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
            var btnDelete = MakeBtn("删除", (s, e) => DeleteSelected());

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 420,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            buttons.Controls.AddRange(new Control[] { btnUp, btnRefresh, btnUpload, btnDownload, btnMkdir, btnDelete });

            top.Controls.Add(_pathBox);
            top.Controls.Add(buttons);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5f)
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
            };

            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = Color.FromArgb(160, 160, 160),
                Text = "正在连接..."
            };

            Controls.Add(_list);
            Controls.Add(_status);
            Controls.Add(top);
        }

        private Button MakeBtn(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Width = 70,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
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
                    item.ForeColor = f.IsDirectory ? Color.FromArgb(100, 180, 255) : Color.FromArgb(220, 220, 220);
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
                        var task = _sftp.UploadAsync(dlg.FileName, remote, progress, cts.Token);
                        while (!task.IsCompleted)
                        {
                            await Task.Delay(100);
                            if (progressDlg.IsCancelled)
                            {
                                try { cts.Cancel(); } catch { }
                                break;
                            }
                            Application.DoEvents();
                        }
                        await task;
                        if (progressDlg.IsCancelled)
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
                        var task = _sftp.DownloadAsync(remote, dlg.FileName, progress, cts.Token);
                        while (!task.IsCompleted)
                        {
                            await Task.Delay(100);
                            if (progressDlg.IsCancelled)
                            {
                                try { cts.Cancel(); } catch { }
                                break;
                            }
                            Application.DoEvents();
                        }
                        await task;
                        if (progressDlg.IsCancelled)
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

        private static string Combine(string basePath, string name)
        {
            if (string.IsNullOrEmpty(basePath) || basePath == "/") return "/" + name.TrimStart('/');
            return basePath.TrimEnd('/') + "/" + name.TrimStart('/');
        }

        private static string Prompt(string title, string label)
        {
            using (var f = new Form
            {
                Text = title,
                Size = DpiScale.S(360, 140),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(35, 35, 35)
            })
            {
                var lbl = new Label { Text = label, ForeColor = Color.White, Location = DpiScale.P(12, 12), AutoSize = true };
                var box = new TextBox { Location = DpiScale.P(12, 40), Width = 320, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = DpiScale.P(250, 70) };
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
                    try { _sftp?.Disconnect(); } catch { }
                    try { _sftp?.Dispose(); } catch { }
                }
            }
            base.Dispose(disposing);
        }
    }
}
