using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.UI.Services;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.Sftp;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;

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
        private EmptyStateView _remotePlaceholder;
        private FilePaneControl _remote;
        private SplitContainer _split;
        private AntdUI.Label _connStatus;
        private AntdUI.Label _queueLabel;
        private int _activeTransfers;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer _healthTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private AntdUI.Button _reconnect;
        private bool _connecting;
        private bool _connectRequested = true;
        private bool _closed;
        private bool _reportedConnected;
        private bool _followCwd = true;
        private string _pendingFollow;

        /// <summary>是否跟随终端当前目录（MobaXterm「follow terminal folder」）。</summary>
        public bool FollowTerminalDirectory
        {
            get { return _followCwd; }
            set { _followCwd = value; }
        }

        /// <summary>终端的 cwd 仅跟随同一连接配置和端点。</summary>
        public bool MatchesConnection(ConnectionConfig config)
        {
            return config != null && _config != null && !string.IsNullOrEmpty(config.Id)
                && config.Id == _config.Id && config.Port == _config.Port
                && string.Equals(config.Host, _config.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(config.Username, _config.Username, StringComparison.Ordinal);
        }

        /// <summary>
        /// 终端切目录 → 远端栏跟过去。force=true 时忽略「跟随」开关（Ctrl+Click 路径用）。
        /// 远端还没连上时先记住，连上后落到该目录。
        /// </summary>
        public void FollowTo(string remotePath, bool force = false)
        {
            if (_closed || string.IsNullOrEmpty(remotePath)) return;
            if (!force && !_followCwd) return;
            var pane = _remote;
            if (pane == null) { _pendingFollow = remotePath; return; }
            try
            {
                if (string.Equals(pane.CurrentPath, remotePath, StringComparison.Ordinal)) { _pendingFollow = null; return; }
                pane.Navigate(remotePath);
                _pendingFollow = null;
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("SftpDualPanePanel.FollowTo", ex); } catch { }
            }
        }

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
            _healthTimer.Tick += (sender, args) => CheckConnection();
            _healthTimer.Start();
        }

        // ── UI ──────────────────────────────────────────────
        private void BuildUI()
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            _connStatus = new AntdUI.Label {
                Dock = DockStyle.Top,
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 4), DpiScale.V(this, 6), DpiScale.V(this, 4)),
                Text = L10n.T("  连接 ") + (_config != null ? _config.Host : "?") + " …",
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = GdtermColorTable.Surface
            };
            _connStatus.Font = FormFontPolicy.UiFont();
            _connStatus.Height = FormFontPolicy.LineBox(_connStatus.Font, this) + _connStatus.Padding.Vertical;
            _connStatus.FontChanged += (sender, args) => _connStatus.Height = FormFontPolicy.LineBox(_connStatus.Font, this) + _connStatus.Padding.Vertical;

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                FixedPanel = FixedPanel.None,
                BackColor = GdtermColorTable.Border
            };

            _local = new FilePaneControl(new LocalFilePaneProvider());
            _local.EntriesDropped += entries => OnTransfer(entries, targetIsRemote: false); // 拖到本地=下载
            _local.ResumeToPeerRequested += entries => OnTransfer(entries, true, true);
            _local.TransferToPeerRequested += entries => OnTransfer(entries, targetIsRemote: true); // 本地→对侧=上传
            AddPane(_split.Panel1, _local, L10n.T("本地"), GdtermColorTable.Info);

            // Panel2：连接成功前只有占位——升级为标准空态层（带「重试连接」入口，
            // 原来连接失败后这个占位就永远停在那儿，只能关标签页重开）
            _remotePlaceholder = EmptyStateView.Attach(_split.Panel2, L10n.T("正在连接 ") + (_config != null ? _config.Host : "") + " …",
                L10n.T("连接成功后这里显示远端目录，可拖拽或双击在两栏间传输"));
            _split.Panel2.Controls.Add(_remotePlaceholder);

            var queueBar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 3), DpiScale.V(this, 6), DpiScale.V(this, 3))
            };
            queueBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            queueBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _queueLabel = new AntdUI.Label {
                Dock = DockStyle.Fill,
                Text = L10n.T("传输队列空闲"),
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _queueLabel.Font = FormFontPolicy.UiFont();
            _queueLabel.Height = FormFontPolicy.LineBox(_queueLabel.Font, this);
            _reconnect = new AntdUI.Button { Text = L10n.T("重新连接"), Name = "SftpReconnectButton", Enabled = false };
            FormFontPolicy.SizeActionButton(this, _reconnect, 12, 4);
            _reconnect.Click += (sender, args) => BeginConnect();
            queueBar.Controls.Add(_reconnect);
            queueBar.Controls.Add(_queueLabel);
            _queueLabel.AutoEllipsis = true;

            _connStatus.AutoEllipsis = true;

            Controls.Add(_split);
            Controls.Add(queueBar);
            Controls.Add(_connStatus);

            _split.SizeChanged += (s, e) =>
            {
                try { _split.SplitterDistance = Math.Max(100, _split.Width / 2); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("SftpDualPanePanel", exSwallowed); } catch { } }
            };
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private static void AddPane(SplitterPanel host, FilePaneControl pane, string title, Color titleColor, Control extraTop = null)
        {
            var header = new AntdUI.Label {
                Dock = DockStyle.Top,
                Height = FormFontPolicy.RowStep(pane) - 9,
                AutoEllipsis = true,
                Text = "  " + title,
                ForeColor = titleColor,
                BackColor = GdtermColorTable.Surface,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Services.FormFontPolicy.UiFont(0f, FontStyle.Bold)
            };
            // 停靠顺序 = 添加顺序的反序：先加面板，再加附加条，最后加标题（标题最靠上）
            host.Controls.Add(pane);
            if (extraTop != null) host.Controls.Add(extraTop);
            host.Controls.Add(header);
        }

        // ── 连接 ──────────────────────────────────────────
        protected override void OnLoad(EventArgs e)
        {
            // 字体度量可能在构造期间创建句柄；真正装入宿主后才排队连接。
            base.OnLoad(e);
            BeginInvoke(new Action(() => { if (!_closed && _connectRequested) BeginConnect(); }));
        }

        private async void BeginConnect()
        {
            if (_closed || _connecting || _activeTransfers > 0) return;
            if (!IsHandleCreated) { _connectRequested = true; return; }
            _connectRequested = false;
            _connecting = true;
            _reconnect.Enabled = false;
            _reportedConnected = false;
            _connStatus.Text = L10n.T("正在连接 ") + _config.Host + " …";
            var ct = _lifetime.Token;
            ISftpService service = null;
            try
            {
                if (_remote != null) { _pendingFollow = _remote.CurrentPath; _remote.Enabled = false; }
                _sftp?.Dispose();
                service = _factory.Create();
                _sftp = service;
                if (_config.RequiresTunnel)
                {
                    if (_tunnelManager == null) throw new InvalidOperationException(L10n.T("隧道服务不可用"));
                    var ep = await _tunnelManager.EstablishAsync(_config, _credential, ct);
                    ct.ThrowIfCancellationRequested();
                    await service.ConnectViaTunnelAsync(_config, _credential, ep, ct);
                }
                else await service.ConnectAsync(_config, _credential, ct);
                ct.ThrowIfCancellationRequested();
                if (_closed) return;
                _connStatus.Text = L10n.T("  已连接 ") + _config.Host + "  ·  " + (_credential.Username ?? _config.Username ?? "");
                _connStatus.ForeColor = GdtermColorTable.Accent;
                _reportedConnected = true;
                foreach (Control child in _split.Panel2.Controls.Cast<Control>().ToArray()) child.Dispose();
                _remotePlaceholder = null;
                _remote = new FilePaneControl(new SftpFilePaneProvider(service, _config.Host));
                _remote.AcceptedDragSource = _local.Provider;
                _local.AcceptedDragSource = _remote.Provider;
                _remote.EntriesDropped += entries => OnTransfer(entries, true);
                _remote.TransferToPeerRequested += entries => OnTransfer(entries, false);
                _remote.ResumeToPeerRequested += entries => OnTransfer(entries, false, true);
                var follow = new AntdUI.Checkbox
                {
                    Text = L10n.T("跟随终端目录"), Checked = _followCwd, AutoSize = true, Dock = DockStyle.Top,
                    Padding = new Padding(DpiScale.V(this, 6), 0, 0, 0), BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Muted, Font = FormFontPolicy.UiFont()
                };
                follow.CheckedChanged += (sender, args) => { _followCwd = follow.Checked; };
                AddPane(_split.Panel2, _remote, L10n.T("远程 ") + _config.Host, GdtermColorTable.Accent, follow);
                _remote.Navigate(string.IsNullOrEmpty(_pendingFollow) ? "/" : _pendingFollow);
            }
            catch (OperationCanceledException) { service?.Dispose(); }
            catch (Exception ex)
            {
                service?.Dispose();
                if (_closed) return;
                _connStatus.Text = L10n.T("  连接失败: ") + ex.Message;
                _connStatus.ForeColor = GdtermColorTable.Danger;
                _remotePlaceholder?.SetEmpty(true, L10n.T("连接失败"), L10n.T("请点击重新连接后重试。"));
                DiagLog.Info("SftpDualPane.Connect", "failed: " + ex.Message);
            }
            finally
            {
                _connecting = false;
                if (!_closed) _reconnect.Enabled = !_reportedConnected;
            }
        }

        private void CheckConnection()
        {
            if (_closed || _connecting) return;
            bool connected = _sftp != null && _sftp.IsConnected;
            if (_reportedConnected && !connected)
            {
                _reportedConnected = false;
                _connStatus.Text = L10n.T("连接已断开，请重新连接。");
                _connStatus.ForeColor = GdtermColorTable.Danger;
                if (_remote != null) _remote.Enabled = false;
                _local.AcceptedDragSource = null;
            }
            _reconnect.Enabled = !connected && _activeTransfers == 0;
        }

        // ── 跨面板传输 ────────────────────────────────────
        private async void OnTransfer(FileEntry[] entries, bool targetIsRemote, bool resume = false)
        {
            if (_closed || _activeTransfers > 0 || entries == null || entries.Length == 0) return;
            if (_sftp == null || !_sftp.IsConnected)
            {
                CheckConnection();
                ToastNotifier.Error(L10n.T("连接已断开，请重新连接。"));
                return;
            }
            var targetPane = targetIsRemote ? _remote : _local;
            var targetPath = targetPane?.CurrentPath;
            if (string.IsNullOrEmpty(targetPath) || targetPane.IsBusy)
            {
                ToastNotifier.Error(L10n.T("请先打开一个可写入的目标目录。"));
                return;
            }
            var service = _sftp;
            var direction = targetIsRemote ? L10n.T("上传") : L10n.T("下载");
            var dialog = new TransferProgressDialog(direction);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            EventHandler cancel = (sender, args) => cts.Cancel();
            dialog.CancelRequested += cancel;
            _activeTransfers++;
            UpdateQueueStatus();
            dialog.Show(FindForm());
            int completed = 0, count = 0;
            try
            {
                var runner = new SftpTransferRunner(service);
                var plan = await Task.Run(() => runner.PlanAsync(entries, targetIsRemote, targetPath, cts.Token), cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                count = plan.Files.Count;
                if (!resume && plan.Conflicts > 0)
                {
                    var answer = MessageBox.Show(dialog, string.Format(L10n.T("目标中已有 {0} 个文件。覆盖这些文件？"), plan.Conflicts),
                        L10n.T("确认覆盖"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer != DialogResult.Yes) { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); }
                }
                var progress = new Progress<SftpTransferProgress>(value =>
                {
                    if (_closed) return;
                    completed = value.Completed;
                    var detail = string.Format("{0}/{1}  {2}", value.Completed, value.Count, value.Path);
                    dialog.Report(value.Bytes, value.Total, detail);
                    _queueLabel.Text = direction + "  " + detail;
                });
                TransferCenterPanel.Record(direction + " → " + targetPath);
                await runner.ExecuteAsync(plan, resume, progress, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                completed = count;
                if (!_closed)
                {
                    var message = string.Format(L10n.T("传输完成：{0} 个文件"), completed);
                    dialog.Complete(true, message);
                    TransferCenterPanel.Record(message + " → " + targetPath);
                    UpdateQueueStatus(message);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_closed)
                {
                    var message = string.Format(L10n.T("已取消，已完成 {0}/{1} 个文件；未完成文件可校验后续传。"), completed, count);
                    dialog.Complete(false, message, true);
                    TransferCenterPanel.Record(message);
                }
            }
            catch (Exception ex)
            {
                if (!_closed)
                {
                    var message = string.Format(L10n.T("传输中断，已完成 {0}/{1} 个文件："), completed, count) + ex.Message;
                    dialog.Complete(false, message);
                    TransferCenterPanel.Record(message);
                }
            }
            finally
            {
                dialog.CancelRequested -= cancel;
                cts.Dispose();
                _activeTransfers--;
                if (_closed) dialog.Dispose();
                else
                {
                    UpdateQueueStatus();
                    CheckConnection();
                    if (!targetPane.IsDisposed && (!targetIsRemote || service.IsConnected)) targetPane.Refresh();
                }
            }
        }

        private void UpdateQueueStatus(string idleText = null)
        {
            if (_activeTransfers > 0)
                _queueLabel.Text = L10n.T("  传输中… (") + _activeTransfers + L10n.T(" 个任务)");
            else
                _queueLabel.Text = "  " + (idleText ?? L10n.T("传输队列空闲"));
        }

        // ── 生命周期 ──────────────────────────────────────
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                _healthTimer.Dispose();
                _lifetime.Cancel();
                _sftp?.Dispose();
                _lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
