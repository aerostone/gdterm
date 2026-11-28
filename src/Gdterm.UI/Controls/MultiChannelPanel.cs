using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.Terminal.Models;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    public class MultiChannelPanel : UserControl
    {
        private readonly MultiChannelManager _manager;
        private readonly MultiChannelRecorder _recorder;
        private readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly Dictionary<ITerminalSession, EventHandler<TerminalOutputEventArgs>> _recordHandlers =
            new Dictionary<ITerminalSession, EventHandler<TerminalOutputEventArgs>>();
        private ListView _list;
        private ListShell _shell;
        private EmptyStateView _empty;
        private AntdUI.Input _command;
        private AntdUI.Label _status, _targets;
        private RichTextBox _results;
        private AntdUI.Button _send, _record, _replay, _cancel;
        private CancellationTokenSource _run;
        private bool _busy, _refreshing;
        private string _fingerprint;
        private EventHandler<CommandSentEventArgs> _recordInput;
        public Action SyncSessions { get; set; }
        public CommandGate CommandGate { get; set; }
        public IAuditLogger AuditLogger { get; set; }
        internal Func<string, bool> ConfirmForTest { get; set; }

        public MultiChannelPanel(MultiChannelManager manager) : this(manager, null) { }
        public MultiChannelPanel(MultiChannelManager manager, MultiChannelRecorder recorder)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager)); _recorder = recorder;
            Font = FormFontPolicy.UiFont(); Dock = DockStyle.Fill; BackColor = GdtermColorTable.Background;
            BuildUI(); RefreshList();
            _refresh.Tick += (s, e) => { SyncSessions?.Invoke(); RefreshList(); };
            _refresh.Start(); UiA11y.AutoName(this);
        }

        private void BuildUI()
        {
            AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = DpiScale.V(this, 680), ColumnCount = 1, RowCount = 6,
                Padding = new Padding(DpiScale.V(this, 8)) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            _send = Button(L10n.T("广播"), "BroadcastSend", async () => await SendAsync(), true);
            _record = Button(L10n.T("停止录制"), "BroadcastRecord", ToggleRecord);
            _replay = Button(L10n.T("查看回放"), "BroadcastReplay", async () => await ReplayAsync());
            _cancel = Button(L10n.T("取消任务"), "BroadcastCancel", () => { _run?.Cancel(); _cancel.Enabled = false; });
            actions.Controls.AddRange(new Control[] {
                Button(L10n.T("全选就绪"), "BroadcastSelectAll", () => SelectVisible(true)),
                Button(L10n.T("清空选择"), "BroadcastDeselect", () => SelectVisible(false)),
                _send, _cancel, _record, _replay,
                Button(L10n.T("导出录制"), "BroadcastExport", ExportRecording),
                Button(L10n.T("打开录制"), "BroadcastOpen", OpenRecording) });
            layout.Controls.Add(actions, 0, 0);
            _targets = Hint("", "BroadcastTargets"); layout.Controls.Add(_targets, 0, 1);
            _list = new ListView { Name = "BroadcastSessions", Dock = DockStyle.Fill, View = View.Details,
                CheckBoxes = true, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true,
                Font = Font, BackColor = GdtermColorTable.Background, ForeColor = GdtermColorTable.Foreground };
            foreach (var name in new[] { L10n.T("目标主机"), L10n.T("状态"), L10n.T("已发送") })
                _list.Columns.Add(name, DpiScale.V(this, name == L10n.T("目标主机") ? 200 : 120));
            _list.ItemCheck += OnItemCheck;
            _shell = new ListShell(_list, L10n.T("按主机过滤…"));
            _list.Layout += (s, e) => UpdateActions();
            layout.Controls.Add(_shell, 0, 2);
            _empty = EmptyStateView.Attach(_shell, L10n.T("还没有可广播的会话"),
                L10n.T("打开终端连接后自动显示；只向就绪终端发送。"));
            _command = new AntdUI.Input { Name = "BroadcastCommand", Dock = DockStyle.Top, Multiline = true,
                Font = FormFontPolicy.MonoFont(), Height = FormFontPolicy.LineBox(FormFontPolicy.MonoFont(), this) * 2 + DpiScale.V(this, 18),
                PlaceholderText = L10n.T("Enter 广播；Shift+Enter 换行") };
            _command.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; _ = SendAsync(); }
            };
            layout.Controls.Add(_command, 0, 3);
            _results = new RichTextBox { Name = "BroadcastResults", Dock = DockStyle.Fill, ReadOnly = true,
                WordWrap = false, DetectUrls = false, Font = FormFontPolicy.MonoFont(),
                BackColor = GdtermColorTable.Background, ForeColor = GdtermColorTable.Foreground };
            layout.Controls.Add(_results, 0, 4);
            _status = Hint(L10n.T("就绪；回放仅查看录制，不执行命令。"), "BroadcastStatus");
            layout.Controls.Add(_status, 0, 5);
            Controls.Add(layout);
            SizeChanged += (s, e) => layout.Height = Math.Max(ClientSize.Height, DpiScale.V(this, 680));
        }
        private AntdUI.Label Hint(string text, string name)
        {
            var label = new AntdUI.Label { Name = name, Text = text, Font = Font, Dock = DockStyle.Top,
                ForeColor = GdtermColorTable.Muted, Margin = new Padding(0, DpiScale.V(this, 4), 0, DpiScale.V(this, 4)) };
            Action fit = () => {
                int height = FormFontPolicy.MeasureWrappedHeight(label.Text, Math.Max(DpiScale.V(this, 180), label.Width), Font, this) + DpiScale.V(this, 4);
                if (label.Height != height) label.Height = height;
            };
            label.SizeChanged += (s, e) => fit(); label.TextChanged += (s, e) => fit(); fit(); return label;
        }
        private AntdUI.Button Button(string text, string name, Action click, bool primary = false)
        {
            var button = new AntdUI.Button { Name = name, Text = text, Font = Font, Margin = new Padding(DpiScale.V(this, 3)) };
            if (primary) button.Type = AntdUI.TTypeMini.Primary;
            FormFontPolicy.SizeActionButton(this, button, 10, 4); button.Click += (s, e) => click(); return button;
        }
        private void SelectVisible(bool select)
        {
            if (_busy) return;
            var ids = _list.Items.Cast<ListViewItem>().Select(r => (string)r.Tag).ToArray();
            int rejected = 0;
            foreach (var id in ids)
                if (select) { if (!_manager.Select(id).Success) rejected++; }
                else _manager.Deselect(id);
            RefreshList();
            if (rejected > 0) _status.Text = L10n.Tf("✗ {0} 个会话未就绪", rejected);
        }
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_refreshing || _busy || e.Index < 0 || e.Index >= _list.Items.Count) return;
            // Resolve the stable ID synchronously; sorting/filtering cannot redirect a queued index.
            var item = _list.Items[e.Index];
            var id = (string)item.Tag;
            if (e.NewValue == CheckState.Checked)
            {
                var result = _manager.Select(id);
                if (!result.Success) { e.NewValue = CheckState.Unchecked; _status.Text = result.Message; }
            }
            else _manager.Deselect(id);
            UpdateActions();
        }
        public void RefreshList()
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke((Action)RefreshList); } catch (InvalidOperationException) { } return; }
            var all = _manager.GetAllSessions();
            var fingerprint = string.Join("|", all.Select(s => s.SessionId + ":" + s.DisplayName + ":" + s.IsConnected + ":" +
                s.IsSelected + ":" + s.ReadyState.Description + ":" + s.CommandCount));
            if (fingerprint != _fingerprint)
            {
                _fingerprint = fingerprint; _refreshing = true;
                try
                {
                    var rows = all.Select(s => {
                        var item = new ListViewItem(s.DisplayName + " [" + s.SessionId.Substring(0, Math.Min(8, s.SessionId.Length)) + "]") {
                            Tag = s.SessionId, Checked = s.IsSelected, ToolTipText = s.DisplayName + "\n" + s.SessionId + "\n" + s.ReadyState.Description
                        };
                        item.SubItems.Add(s.ReadyState.Description); item.SubItems.Add(s.CommandCount.ToString()); return item;
                    }).ToList();
                    _shell.SetItems(rows); _empty.SetEmpty(all.Count == 0);
                }
                finally { _refreshing = false; }
            }
            if (_recorder?.LimitReached == true) { StopRecording(); _status.Text = L10n.T("录制已达到上限并停止，请导出。"); }
            UpdateActions();
        }
        private void UpdateActions()
        {
            if (_targets == null || _list == null || _send == null) return;
            var targets = _manager.GetSelectedTargets();
            var visible = new HashSet<string>(_list.Items.Cast<ListViewItem>().Select(r => (string)r.Tag));
            var hidden = targets.Count(t => !visible.Contains(t.SessionId));
            _targets.Text = L10n.Tf("已选 {0} 台（隐藏 {1} 台）。全选/清空仅作用于可见行。", targets.Count, hidden);
            _send.Enabled = !_busy && targets.Count > 0;
            _record.Enabled = !_busy && _recorder != null;
            _replay.Enabled = !_busy && _recorder != null && !_recorder.IsRecording && _recorder.EntryCount > 0;
            _cancel.Enabled = _busy && _run != null && !_run.IsCancellationRequested;
            _record.Text = _recorder?.IsRecording == true ? L10n.T("停止录制") : L10n.T("录制");
            FormFontPolicy.SizeActionButton(this, _record, 10, 4);
        }
        internal async Task SendAsync()
        {
            if (_busy || IsDisposed || string.IsNullOrWhiteSpace(_command.Text)) return;
            _busy = true;
            try
            {
                SyncSessions?.Invoke(); RefreshList();
                var targets = _manager.GetSelectedTargets();
                if (targets.Count == 0) { _status.Text = L10n.T("请勾选至少一个会话"); return; }
                var command = _command.Text;
                var names = string.Join(", ", targets.Select(t => t.DisplayName + " [" + t.SessionId.Substring(0, Math.Min(8, t.SessionId.Length)) + "]"));
                bool allowed = ConfirmForTest != null ? ConfirmForTest(command) :
                    DangerousCommandPrompt.Confirm(FindForm(), CommandGate, command, InputSource.Broadcast, AuditLogger, names);
                if (!allowed) { _status.Text = L10n.T("已取消危险命令"); return; }
                _run = new CancellationTokenSource(); _list.Enabled = _command.Enabled = false; UpdateActions();
                _results.Clear(); _results.AppendText(L10n.Tf("目标：{0}\n", names));
                var results = await _manager.BroadcastCommandAsync(command, targets, _run.Token);
                if (IsDisposed || Disposing) return;
                foreach (var result in results)
                {
                    _results.AppendText(result.DisplayName + ": " + (result.Success ? L10n.T("已发送") : result.Error) + "\n");
                }
                int sent = results.Count(r => r.Success);
                _status.Text = L10n.Tf("已发送 {0}/{1} 台；命令执行结果请查看各终端。", sent, results.Count);
                if (sent == results.Count && sent > 0) _command.Clear();
            }
            catch (Exception ex) { DiagLog.Error("Broadcast.Send", ex); if (!IsDisposed) _status.Text = L10n.T("发送失败：") + ex.Message; }
            finally
            {
                _run?.Dispose(); _run = null; _busy = false;
                if (!IsDisposed && !Disposing) { _list.Enabled = _command.Enabled = true; RefreshList(); }
            }
        }
        private void ToggleRecord()
        {
            if (_recorder == null || _busy) return;
            if (_recorder.IsRecording) { StopRecording(); _status.Text = L10n.Tf("录制停止：{0} 条", _recorder.EntryCount); }
            else
            {
                var targets = _manager.GetSelectedTargets();
                if (targets.Count == 0) { _status.Text = L10n.T("请先选择要录制的会话"); return; }
                StopRecording(); _recorder.StartRecording();
                foreach (var target in targets)
                {
                    _recorder.RegisterSession(target.SessionId, target.DisplayName);
                    EventHandler<TerminalOutputEventArgs> handler = (s, e) => _recorder.RecordOutput(target.SessionId, e?.Text);
                    target.Session.OutputReceived += handler; _recordHandlers[target.Session] = handler;
                }
                var recorded = targets.ToDictionary(t => t.SessionId, t => t.Session);
                _recordInput = (sender, ev) => {
                    foreach (var id in ev.TargetSessionIds)
                    {
                        ITerminalSession original;
                        if (recorded.TryGetValue(id, out original) && ReferenceEquals(original, _manager.GetSession(id)))
                            _recorder.RecordInput(id, ev.Command);
                    }
                };
                _manager.CommandSent += _recordInput;
                _status.Text = L10n.T("正在录制选中会话的广播输入与终端输出。");
            }
            UpdateActions();
        }
        private void StopRecording()
        {
            if (_recordInput != null) _manager.CommandSent -= _recordInput;
            _recordInput = null;
            foreach (var kv in _recordHandlers) kv.Key.OutputReceived -= kv.Value;
            _recordHandlers.Clear(); _recorder?.StopRecording();
        }
        private async Task ReplayAsync()
        {
            if (_recorder == null || _busy || _recorder.IsRecording || _recorder.EntryCount == 0) return;
            _busy = true; _run = new CancellationTokenSource(); UpdateActions(); _results.Clear();
            try
            {
                var progress = new Progress<ReplayEvent>(ev => {
                    if (IsDisposed || Disposing) return;
                    var entry = ev.Entry;
                    if (_results.TextLength < 2 * 1024 * 1024)
                        _results.AppendText(string.Format("[{0:0.000}s] {1} {2}: {3}\n", entry.TimestampMs / 1000.0,
                            entry.SessionName, entry.Direction == EntryDirection.Input ? "←" : "→", entry.Content));
                });
                await _recorder.ReplayAsync(1, ev => ((IProgress<ReplayEvent>)progress).Report(ev), _run.Token);
                if (!IsDisposed) _status.Text = L10n.T("回放完成");
            }
            catch (OperationCanceledException) { if (!IsDisposed) _status.Text = L10n.T("回放已取消"); }
            catch (Exception ex) { if (!IsDisposed) _status.Text = L10n.T("回放失败: ") + ex.Message; }
            finally { _run.Dispose(); _run = null; _busy = false; if (!IsDisposed) UpdateActions(); }
        }
        private void ExportRecording()
        {
            if (_recorder == null || _busy || _recorder.IsRecording || _recorder.EntryCount == 0) return;
            using (var dialog = new SaveFileDialog { Filter = "Recording (*.gdrec)|*.gdrec|HTML (*.html)|*.html|Text (*.txt)|*.txt",
                FileName = "sync-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") })
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    try
                    {
                        if (dialog.FilterIndex == 1) _recorder.SaveToFile(dialog.FileName);
                        else File.WriteAllText(dialog.FileName, dialog.FilterIndex == 2 ? _recorder.ExportAsHtml() : _recorder.ExportAsText(), System.Text.Encoding.UTF8);
                        _status.Text = L10n.T("已导出 ") + Path.GetFileName(dialog.FileName);
                    }
                    catch (Exception ex) { _status.Text = L10n.T("导出失败: ") + ex.Message; }
        }
        private void OpenRecording()
        {
            if (_recorder == null || _busy || _recorder.IsRecording) return;
            using (var dialog = new OpenFileDialog { Filter = "Recording (*.gdrec)|*.gdrec" })
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    try { _recorder.LoadFromFile(dialog.FileName); _status.Text = L10n.Tf("已加载录制：{0} 条", _recorder.EntryCount); UpdateActions(); }
                    catch (Exception ex) { _status.Text = L10n.T("加载失败：") + ex.Message; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _refresh.Dispose(); _run?.Cancel(); StopRecording(); }
            base.Dispose(disposing);
        }
    }
}
