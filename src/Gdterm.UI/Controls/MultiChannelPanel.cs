using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Terminal;
using Gdterm.UI.Services;

using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 多通道输入面板——WindTerm 风格，显示所有会话列表，支持勾选/全选/广播输入
    /// 带终端就绪状态检测：只有命令提示符状态的终端才允许加入
    /// </summary>
    public class MultiChannelPanel : UserControl
    {
        private readonly MultiChannelManager _manager;
        private readonly MultiChannelRecorder _recorder;
        private CancellationTokenSource _replayCts;
        private ListView _sessionList;
        private AntdUI.Button _btnSelectAll;
        private AntdUI.Button _btnDeselectAll;
        private AntdUI.Button _btnBroadcast;
        private AntdUI.Button _btnRecord;
        private AntdUI.Button _btnReplay;
        private AntdUI.Button _btnExport;
        private AntdUI.Label _statusLabel;
        private AntdUI.Input _commandInput;
        private EventHandler<ChannelSessionEventArgs> _onSessionRegistered;
        private EventHandler<ChannelSessionEventArgs> _onSessionUnregistered;
        private EventHandler<BroadcastStateChangedEventArgs> _onBroadcastStateChanged;

        /// <summary>
        /// 广播命令事件（用户在输入框输入命令后触发）
        /// </summary>
        public event EventHandler<string> BroadcastCommandRequested;

        public MultiChannelPanel(MultiChannelManager manager)
            : this(manager, null)
        {
        }

        /// <summary>录制器由持有人注入（MainForm 单例）；null 时录制按钮置灰，广播不受影响。</summary>
        public MultiChannelPanel(MultiChannelManager manager, MultiChannelRecorder recorder)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _recorder = recorder;
            InitializeComponent();
            WireEvents();
        }

        private void InitializeComponent()
        {
            Size = DpiScale.S(this, 300, 400);
            BackColor = GdtermColorTable.Background;

            // 工具栏
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(DpiScale.V(this, 3))
            };

            _btnSelectAll = CreateToolbarButton("全选就绪");
            _btnDeselectAll = CreateToolbarButton("取消");
            _btnBroadcast = CreateToolbarButton("广播");
            _btnBroadcast.Enabled = false;
            _btnRecord = CreateToolbarButton("录制");
            _btnReplay = CreateToolbarButton("回放");
            _btnExport = CreateToolbarButton("导出");
            // 录制器未注入时录制链置灰（广播不受影响；SidePanelFactory 正常注入单例故常态可用）。
            bool recOk = _recorder != null;
            _btnRecord.Enabled = recOk;
            _btnReplay.Enabled = recOk;
            _btnExport.Enabled = recOk;

            toolbar.Controls.AddRange(new Control[] { _btnSelectAll, _btnDeselectAll, _btnBroadcast, _btnRecord, _btnReplay, _btnExport });

            // 会话列表
            _sessionList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _sessionList.Columns.Add("主机", 100);
            _sessionList.Columns.Add("分组", 50);
            _sessionList.Columns.Add("状态", 60);
            _sessionList.Columns.Add("终端", 55);
            _sessionList.Columns.Add("命令数", 45);

            // 命令输入框
            var inputPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(DpiScale.V(this, 3))
            };
            inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _commandInput = new AntdUI.Input {
                Dock = DockStyle.Top,
                // 原 28 在大字号下裁字 → 38 地板（用户点名的"多通道没做完"区）
                Height = FormFontPolicy.FieldHeight(this),
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9f)
            };

            var inputHint = new AntdUI.Label {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = "输入命令后按 Enter 广播到所有选中会话",
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };

            inputPanel.Controls.Add(_commandInput, 0, 0);
            inputPanel.Controls.Add(inputHint, 0, 1);

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Text = "就绪",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 3), 0, 0, 0),
                BackColor = GdtermColorTable.Surface
            };

            Controls.Add(_sessionList);
            Controls.Add(toolbar);
            Controls.Add(inputPanel);
            Controls.Add(_statusLabel);
        }

        private AntdUI.Button CreateToolbarButton(string text)
        {
            return new AntdUI.Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3)),
                Margin = new Padding(DpiScale.V(this, 2))
            };
        }

        private void WireEvents()
        {
            _btnSelectAll.Click += (s, e) =>
            {
                var results = _manager.SelectAll();
                ShowSelectionResults(results);
                RefreshList();
            };

            _btnDeselectAll.Click += (s, e) =>
            {
                _manager.DeselectAll();
                RefreshList();
            };

            _btnBroadcast.Click += (s, e) => ExecuteBroadcast();
            _btnRecord.Click += (s, e) => ToggleRecord();
            _btnReplay.Click += (s, e) => ReplayRecording();
            _btnExport.Click += (s, e) => ExportRecording();

            _sessionList.ItemCheck += OnSessionItemCheck;

            _commandInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ExecuteBroadcast();
                    e.SuppressKeyPress = true;
                }
            };

            _onSessionRegistered = (s, e) => RefreshList();
            _onSessionUnregistered = (s, e) => RefreshList();
            _onBroadcastStateChanged = (s, e) =>
            {
                if (_btnBroadcast != null)
                    _btnBroadcast.Enabled = e.IsBroadcasting;
                UpdateStatus();
            };
            _manager.SessionRegistered += _onSessionRegistered;
            _manager.SessionUnregistered += _onSessionUnregistered;
            _manager.BroadcastStateChanged += _onBroadcastStateChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _manager != null)
            {
                if (_onSessionRegistered != null)
                    _manager.SessionRegistered -= _onSessionRegistered;
                if (_onSessionUnregistered != null)
                    _manager.SessionUnregistered -= _onSessionUnregistered;
                if (_onBroadcastStateChanged != null)
                    _manager.BroadcastStateChanged -= _onBroadcastStateChanged;
            }
            base.Dispose(disposing);
        }

        private void OnSessionItemCheck(object sender, ItemCheckEventArgs e)
        {
            // 延迟处理，因为事件在状态改变前触发
            BeginInvoke(new Action(() =>
            {
                var item = _sessionList.Items[e.Index];
                var sessionId = item.Tag as string;
                if (sessionId != null)
                {
                    if (e.NewValue == CheckState.Checked)
                    {
                        // 尝试选择——有就绪检测
                        var result = _manager.Select(sessionId);
                        if (!result.Success)
                        {
                            // 选择失败，恢复未勾选状态
                            item.Checked = false;
                            _statusLabel.Text = $"✗ {item.SubItems[0].Text}: {result.Message}";
                            _statusLabel.ForeColor = GdtermColorTable.Danger;
                            return;
                        }
                    }
                    else
                    {
                        _manager.Deselect(sessionId);
                    }
                    UpdateStatus();
                }
            }));
        }

        private void ExecuteBroadcast()
        {
            var command = _commandInput.Text;
            if (string.IsNullOrWhiteSpace(command)) return;

            try { if (_recorder != null && _recorder.IsRecording) _recorder.RecordInput("broadcast", command); }
            catch (System.Exception exSwallowed) { try { Gdterm.UI.Diagnostics.DiagLog.Swallowed("MultiChannel", exSwallowed); } catch { } }
            BroadcastCommandRequested?.Invoke(this, command);
            _commandInput.Clear();
        }

        private string RecordDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "sync-recordings"); }
        }

        /// <summary>录制开关：开始时注册当前全部会话名并记一条输入锚点；再按停止。</summary>
        private void ToggleRecord()
        {
            if (_recorder == null) return;
            try
            {
                if (_recorder.IsRecording)
                {
                    _recorder.StopRecording();
                    _btnRecord.Text = "录制";
                    _statusLabel.Text = "录制停止（" + _recorder.EntryCount + " 条）";
                    return;
                }
                _recorder.StartRecording();
                try
                {
                    foreach (var s in _manager.GetAllSessions())
                        _recorder.RegisterSession(s.SessionId, s.DisplayName ?? s.SessionId);
                }
                catch (System.Exception exSwallowed) { try { Gdterm.UI.Diagnostics.DiagLog.Swallowed("MultiChannel", exSwallowed); } catch { } }
                _btnRecord.Text = "停止";
                _statusLabel.Text = "录制中…（广播输入记入时间线）";
            }
            catch (System.Exception ex) { _statusLabel.Text = "录制失败: " + ex.Message; }
        }

        /// <summary>回放：输入事件按原时间线重发到同名会话（1x）。输出事件仅记时间线不回显——远端状态已变，重放输出无意义。</summary>
        private void ReplayRecording()
        {
            if (_recorder == null || _recorder.IsRecording) return;
            if (_recorder.EntryCount == 0) { _statusLabel.Text = "无录制内容"; return; }
            try
            {
                if (_replayCts != null) { try { _replayCts.Cancel(); } catch { } }
                _replayCts = new CancellationTokenSource();
                var token = _replayCts.Token;
                _statusLabel.Text = "回放中…";
                Task.Run(async () =>
                {
                    try
                    {
                        // 同名会话映射：录制 sessionId → 当前同显示名会话；找不到则跳过该条。
                        var map = new System.Collections.Generic.Dictionary<string, ITerminalSession>();
                        foreach (var s in _manager.GetAllSessions())
                        {
                            try
                            {
                                var sess = _manager.GetSession(s.SessionId);
                                if (sess != null) map[s.SessionId] = sess;
                            }
                            catch { }
                        }
                        await _recorder.ReplayAsync(map, 1.0, null, token);
                        BeginInvoke(new Action(() => { _statusLabel.Text = "回放完成"; }));
                    }
                    catch (OperationCanceledException) { BeginInvoke(new Action(() => { _statusLabel.Text = "回放已取消"; })); }
                    catch (System.Exception ex) { BeginInvoke(new Action(() => { _statusLabel.Text = "回放失败: " + ex.Message; })); }
                }, token);
            }
            catch (System.Exception ex) { _statusLabel.Text = "回放失败: " + ex.Message; }
        }

        /// <summary>导出：落盘 JSON 录制 + 同名 HTML 时间线报告（与宏录制 data/macros 分目录）。</summary>
        private void ExportRecording()
        {
            if (_recorder == null || _recorder.IsRecording) return;
            if (_recorder.EntryCount == 0) { _statusLabel.Text = "无录制内容"; return; }
            try
            {
                Directory.CreateDirectory(RecordDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var jsonPath = Path.Combine(RecordDir, "sync-" + stamp + ".gdrec");
                var htmlPath = Path.Combine(RecordDir, "sync-" + stamp + ".html");
                _recorder.SaveToFile(jsonPath);
                File.WriteAllText(htmlPath, _recorder.ExportAsHtml(), System.Text.Encoding.UTF8);
                _statusLabel.Text = "已导出 " + Path.GetFileName(jsonPath);
            }
            catch (System.Exception ex) { _statusLabel.Text = "导出失败: " + ex.Message; }
        }

        /// <summary>
        /// 刷新会话列表
        /// </summary>
        public void RefreshList()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(RefreshList));
                return;
            }

            _sessionList.BeginUpdate();
            _sessionList.Items.Clear();

            var sessions = _manager.GetAllSessions();
            foreach (var session in sessions)
            {
                var item = new ListViewItem(session.DisplayName);
                item.SubItems.Add(session.Group);

                // 连接状态
                item.SubItems.Add(session.IsConnected ? "● 已连接" : "○ 断开");

                // 终端就绪状态
                var readyText = "—";
                if (session.IsConnected && session.ReadyState != null)
                {
                    readyText = session.ReadyState.IsReady ? "✓ 就绪" : "✗ 忙";
                }
                item.SubItems.Add(readyText);

                item.SubItems.Add(session.CommandCount.ToString());
                item.Tag = session.SessionId;
                item.Checked = session.IsSelected;

                // 未连接或非就绪的终端灰色显示
                if (!session.IsConnected)
                    item.ForeColor = GdtermColorTable.Muted;
                else if (session.ReadyState != null && !session.ReadyState.IsReady)
                    item.ForeColor = GdtermColorTable.Warning;

                _sessionList.Items.Add(item);
            }

            _sessionList.EndUpdate();
            UpdateStatus();
        }

        private void ShowSelectionResults(System.Collections.Generic.List<SelectResult> results)
        {
            var rejected = results.FindAll(r => !r.Success);
            if (rejected.Count > 0)
            {
                var msg = string.Join("\n", rejected.ConvertAll(r => $"• {r.Message}"));
                _statusLabel.Text = $"✗ {rejected.Count} 个会话未就绪";
                _statusLabel.ForeColor = GdtermColorTable.Danger;
            }
            else
            {
                _statusLabel.ForeColor = GdtermColorTable.Foreground;
            }
        }

        private void UpdateStatus()
        {
            var total = _manager.GetAllSessions().Count;
            var selected = _manager.SelectedCount;
            _statusLabel.ForeColor = GdtermColorTable.Foreground;
            _statusLabel.Text = selected > 0
                ? $"已选中 {selected}/{total} 个就绪会话，广播模式已激活"
                : $"共 {total} 个会话，未选择广播目标";
        }
    }
}
