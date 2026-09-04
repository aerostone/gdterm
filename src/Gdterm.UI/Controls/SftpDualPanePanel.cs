using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Sftp;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// SFTP 双栏浏览器——本地/远端 SplitContainer 布局，拖拽或右键传输，递归目录，
    /// 底部传输队列条。协议层完全复用 ISftpService，无新增依赖。
    /// 布局参考 WindTerm Explorer 与 xSSH-File-Transfer-Client 的双栏交互。
    /// 远端栏在 SFTP 连接成功后才创建（provider 需要活的 ISftpService）。
    /// </summary>
    public sealed class SftpDualPanePanel : UserControl
    {
        private readonly ConnectionConfig _config;
        private readonly CredentialPayload _credential;
        private readonly ISftpServiceFactory _factory;
        private readonly ITunnelManager _tunnelManager;
        private ISftpService _sftp;
        private FilePaneControl _local;
        private FilePaneControl _remote;
        private SplitContainer _split;
        private Label _connStatus;
        private Label _queueLabel;
        private int _activeTransfers;

        public SftpDualPanePanel(
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
            _local.Navigate(null); // 本地盘符伪根立即初始化，不等远端连接
            BeginConnect();
        }

        // ── UI ──────────────────────────────────────────────
        private void BuildUI()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            _connStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                Text = "  连接 " + (_config != null ? _config.Host : "?") + " …",
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = GdtermColorTable.Surface
            };

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                BackColor = GdtermColorTable.Border
            };

            _local = new FilePaneControl(new LocalFilePaneProvider());
            _local.EntriesDropped += entries => OnTransfer(entries, targetIsRemote: false); // 拖到本地=下载
            _local.TransferToPeerRequested += entries => OnTransfer(entries, targetIsRemote: true); // 本地→对侧=上传
            AddPane(_split.Panel1, _local, "本地", Color.FromArgb(88, 166, 255));

            // Panel2：连接成功前只放占位提示
            var placeholder = new Label
            {
                Dock = DockStyle.Fill,
                Text = "正在连接 " + (_config != null ? _config.Host : "") + " …",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = GdtermColorTable.Muted,
                BackColor = GdtermColorTable.Background
            };
            _split.Panel2.Controls.Add(placeholder);

            var queueBar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = GdtermColorTable.Surface };
            _queueLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "  传输队列空闲",
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            queueBar.Controls.Add(_queueLabel);

            Controls.Add(_split);
            Controls.Add(queueBar);
            Controls.Add(_connStatus);

            HandleCreated += (s, e) =>
            {
                try { _split.SplitterDistance = Math.Max(100, _split.Width / 2); } catch { }
            };
        }

        private static void AddPane(SplitterPanel host, FilePaneControl pane, string title, Color titleColor)
        {
            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "  " + title,
                ForeColor = titleColor,
                BackColor = GdtermColorTable.Surface,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };
            host.Controls.Add(pane);
            host.Controls.Add(header);
        }

        // ── 连接 ──────────────────────────────────────────
        private async void BeginConnect()
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

                _connStatus.Text = "  已连接 " + _config.Host + "  ·  " + (_config.Username ?? "");
                _connStatus.ForeColor = Color.FromArgb(0, 255, 65);

                // 连接成功：用远端栏替换占位
                _split.Panel2.Controls.Clear();
                _remote = new FilePaneControl(new SftpFilePaneProvider(_sftp, _config.Host));
                _remote.EntriesDropped += entries => OnTransfer(entries, targetIsRemote: true); // 拖到远程=上传
                _remote.TransferToPeerRequested += entries => OnTransfer(entries, targetIsRemote: false); // 远程→对侧=下载
                AddPane(_split.Panel2, _remote, "远程 " + _config.Host, Color.FromArgb(0, 255, 65));
                _remote.Navigate("/");
            }
            catch (Exception ex)
            {
                _connStatus.Text = "  连接失败: " + ex.Message + "  （关闭此标签页后重试）";
                _connStatus.ForeColor = Color.FromArgb(248, 81, 73);
                try { DiagLog.Info("SftpDualPane.Connect", "failed: " + ex.Message); } catch { }
            }
        }

        // ── 跨面板传输 ────────────────────────────────────
        private void OnTransfer(FileEntry[] entries, bool targetIsRemote)
        {
            if (_sftp == null || !_sftp.IsConnected) return;
            var targetPane = targetIsRemote ? _remote : _local;
            if (targetPane == null) return;
            var targetPath = targetPane.CurrentPath;
            if (string.IsNullOrEmpty(targetPath)) return; // 伪根（此电脑）不可作传输目标（无真实路径）
            var upload = targetIsRemote;
            var direction = upload ? "上传" : "下载";

            _activeTransfers++;
            UpdateQueueStatus();
            TransferCenterPanel.Record("双栏" + direction + "开始  " + entries.Length + " 项  → " + targetPath);

            Task.Run(async () =>
            {
                int ok = 0, fail = 0;
                var errors = new List<string>();
                foreach (var e in entries)
                {
                    var name = e.Name;
                    try
                    {
                        ReportQueue("正在" + direction + " " + name + " …");
                        if (upload)
                        {
                            if (e.IsDirectory)
                                await UploadDirectoryRecursive(e.FullPath, targetPath, name);
                            else
                                await _sftp.UploadAsync(e.FullPath, CombineRemote(targetPath, name), null, CancellationToken.None);
                        }
                        else
                        {
                            if (e.IsDirectory)
                                await DownloadDirectoryRecursive(e.FullPath, targetPath, name);
                            else
                                await _sftp.DownloadAsync(e.FullPath, Path.Combine(targetPath, name), null, CancellationToken.None);
                        }
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        errors.Add(name + ": " + ex.Message);
                    }
                }
                return new { ok, fail, errors };
            }).ContinueWith(t =>
            {
                _activeTransfers--;
                if (t.IsFaulted)
                {
                    UpdateQueueStatus();
                    MessageBox.Show(this, "传输失败:\n" + t.Exception.GetBaseException().Message,
                        "SFTP 传输", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                var r = t.Result;
                var msg = "传输完成: " + r.ok + " 成功" + (r.fail > 0 ? "，" + r.fail + " 失败" : "");
                UpdateQueueStatus(msg);
                TransferCenterPanel.Record("双栏" + direction + "完成  " + msg + "  → " + targetPath);
                if (r.fail > 0)
                {
                    MessageBox.Show(this, "部分传输失败:\n" + string.Join("\n", r.errors.Take(10).ToArray()),
                        "SFTP 传输", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                targetPane.Refresh();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ReportQueue(string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => ReportQueue(text))); } catch { }
                return;
            }
            _queueLabel.Text = "  " + text;
        }

        private void UpdateQueueStatus(string idleText = null)
        {
            if (_activeTransfers > 0)
                _queueLabel.Text = "  传输中… (" + _activeTransfers + " 个任务)";
            else
                _queueLabel.Text = "  " + (idleText ?? "传输队列空闲");
        }

        // ── 递归目录传输 ──────────────────────────────────
        // 目标目录已存在时不中断（重传/增量场景）：CreateDirectoryAsync 失败时先确认目录确实存在，
        // 存在则继续写入子项（SSH.NET CreateDirectory 对已存在路径抛异常）。
        private async Task EnsureRemoteDir(string remoteDir)
        {
            try
            {
                await _sftp.CreateDirectoryAsync(remoteDir, CancellationToken.None);
            }
            catch
            {
                try { await _sftp.ListDirectoryAsync(remoteDir, CancellationToken.None); }
                catch (Exception ex2)
                {
                    throw new Exception("无法创建远程目录 " + remoteDir + ": " + ex2.Message, ex2);
                }
            }
        }

        private async Task UploadDirectoryRecursive(string localDir, string remoteBase, string name)
        {
            var remoteDir = CombineRemote(remoteBase, name);
            await EnsureRemoteDir(remoteDir);
            foreach (var file in Directory.EnumerateFiles(localDir))
                await _sftp.UploadAsync(file, CombineRemote(remoteDir, Path.GetFileName(file)), null, CancellationToken.None);
            foreach (var dir in Directory.EnumerateDirectories(localDir))
                await UploadDirectoryRecursive(dir, remoteDir, Path.GetFileName(dir));
        }

        private async Task DownloadDirectoryRecursive(string remoteDir, string localBase, string name)
        {
            var localDir = Path.Combine(localBase, name);
            Directory.CreateDirectory(localDir);
            var items = await _sftp.ListDirectoryAsync(remoteDir, CancellationToken.None);
            foreach (var f in items)
            {
                if (f.Name == "." || f.Name == "..") continue;
                if (f.IsDirectory)
                    await DownloadDirectoryRecursive(f.FullPath, localDir, f.Name);
                else
                    await _sftp.DownloadAsync(f.FullPath, Path.Combine(localDir, f.Name), null, CancellationToken.None);
            }
        }

        private static string CombineRemote(string basePath, string name)
        {
            if (string.IsNullOrEmpty(basePath) || basePath == "/") return "/" + name.TrimStart('/');
            return basePath.TrimEnd('/') + "/" + name.TrimStart('/');
        }

        // ── 生命周期 ──────────────────────────────────────
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _sftp?.Disconnect(); } catch { }
                try { _sftp?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
