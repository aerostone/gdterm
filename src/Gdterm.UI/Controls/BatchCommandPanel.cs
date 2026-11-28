using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    public class BatchCommandPanel : UserControl
    {
        private readonly BatchCommandExecutor _executor = new BatchCommandExecutor();
        private readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly Dictionary<string, ListViewItem> _rows = new Dictionary<string, ListViewItem>();
        private Dictionary<string, ITerminalSession> _sessions = new Dictionary<string, ITerminalSession>();
        private readonly List<string> _history = new List<string>();
        private int _historyCursor = -1;
        private string _draft = "";
        private CancellationTokenSource _run;
        private CommandGate _gate;
        private ListView _list;
        private ListShell _shell;
        private EmptyStateView _empty;
        private AntdUI.Input _command;
        private AntdUI.InputNumber _timeout;
        private RichTextBox _results;
        private AntdUI.Button _execute, _cancel;
        private AntdUI.Label _status, _targets;
        private bool _busy, _displayTruncated;
        public Func<Dictionary<string, ITerminalSession>> SessionProvider { get; set; }
        public IAuditLogger AuditLogger { get; set; }
        // Injectable confirmation only for native interaction fixtures; production always uses the unified prompt.
        internal Func<string, bool> ConfirmForTest { get; set; }
        internal Func<Dictionary<string, ITerminalSession>, string, int, CancellationToken, Task<List<BatchCommandResult>>> ExecuteForTest { get; set; }
        private static string HistoryPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "batch-history.json");

        public BatchCommandPanel()
        {
            Font = FormFontPolicy.UiFont();
            Dock = DockStyle.Fill; BackColor = GdtermColorTable.Background;
            BuildUI(); LoadHistory();
            _refresh.Tick += (s, e) => RefreshFromProvider();
            _refresh.Start();
            _executor.SessionCompleted += OnSessionCompleted;
            UiA11y.AutoName(this);
        }
        public void SetCommandGate(CommandGate gate) { _gate = gate; }
        public void SetDangerousDetector(DangerousCommandDetector detector)
        { if (_gate == null && detector != null) _gate = new CommandGate(detector); }

        private void BuildUI()
        {
            AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = DpiScale.V(this, 680), ColumnCount = 1, RowCount = 7,
                Padding = new Padding(DpiScale.V(this, 8)) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var hint = Hint(L10n.T("SSH 独立命令，不继承终端目录/变量。\nEnter 换行；Ctrl+Enter 执行；Alt+↑/↓ 历史。"), "BatchHint");
            layout.Controls.Add(hint, 0, 0);
            _command = new AntdUI.Input { Name = "BatchCommand", Dock = DockStyle.Top, Multiline = true,
                Height = FormFontPolicy.LineBox(FormFontPolicy.MonoFont(), this) * 3 + DpiScale.V(this, 18),
                Font = FormFontPolicy.MonoFont(), PlaceholderText = L10n.T("输入命令或多行脚本…") };
            _command.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ExecuteCommandAsync(); }
                else if (e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
                { e.SuppressKeyPress = true; RecallHistory(e.KeyCode == Keys.Up ? 1 : -1); }
            };
            layout.Controls.Add(_command, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            _execute = Button(L10n.T("执行"), "BatchExecute", async () => await ExecuteCommandAsync(), true);
            _cancel = Button(L10n.T("取消执行"), "BatchCancel", () => { _run?.Cancel(); _cancel.Enabled = false; });
            _cancel.Enabled = false;
            var timeoutLabel = FormFontPolicy.FitSingleLineLabel(new AntdUI.Label { Text = L10n.T("超时（秒）"), Font = Font });
            _timeout = new AntdUI.InputNumber { Name = "BatchTimeout", Width = DpiScale.V(this, 86),
                Height = FormFontPolicy.FieldHeight(this), Minimum = 1, Maximum = 3600, Value = 30 };
            actions.Controls.AddRange(new Control[] { _execute, _cancel, timeoutLabel, _timeout,
                Button(L10n.T("全选"), "BatchSelectAll", () => SelectAll(true)),
                Button(L10n.T("清空选择"), "BatchDeselect", () => SelectAll(false)),
                Button(L10n.T("复制结果"), "BatchCopy", CopyResults),
                Button(L10n.T("导出结果"), "BatchExport", ExportResults) });
            layout.Controls.Add(actions, 0, 2);
            _targets = Hint(L10n.T("请勾选至少一个 SSH 会话"), "BatchTargets");
            layout.Controls.Add(_targets, 0, 3);
            _list = new ListView { Name = "BatchSessions", Dock = DockStyle.Fill, View = View.Details,
                CheckBoxes = true, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true,
                BackColor = GdtermColorTable.Background, ForeColor = GdtermColorTable.Foreground, Font = Font };
            _list.Columns.Add(L10n.T("目标主机"), DpiScale.V(this, 210));
            _list.Columns.Add(L10n.T("状态"), DpiScale.V(this, 130));
            _list.ItemChecked += (s, e) => UpdateTargets();
            _shell = new ListShell(_list, showFilter: false);
            layout.Controls.Add(_shell, 0, 4);
            _empty = EmptyStateView.Attach(_shell, L10n.T("还没有 SSH 会话"), L10n.T("连接 SSH 后会自动出现在这里。"));
            _empty.SetEmpty(true);
            _results = new RichTextBox { Name = "BatchResults", Dock = DockStyle.Fill, ReadOnly = true,
                WordWrap = false, Font = FormFontPolicy.MonoFont(), BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground, DetectUrls = false };
            layout.Controls.Add(_results, 0, 5);
            _status = Hint(L10n.T("就绪"), "BatchStatus");
            layout.Controls.Add(_status, 0, 6);
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
            var button = new AntdUI.Button { Name = name, Text = text, Font = Font,
                Margin = new Padding(DpiScale.V(this, 3)) };
            if (primary) button.Type = AntdUI.TTypeMini.Primary;
            FormFontPolicy.SizeActionButton(this, button, 10, 4);
            button.Click += (s, e) => click(); return button;
        }
        private void RefreshFromProvider()
        {
            if (IsDisposed || Disposing || SessionProvider == null) return;
            try { SetSessions(SessionProvider()); }
            catch (Exception ex) { DiagLog.Swallowed("Batch.Refresh", ex); _status.Text = L10n.T("刷新会话失败：") + ex.Message; }
        }
        public void SetSessions(Dictionary<string, ITerminalSession> sessions)
        {
            var next = sessions ?? new Dictionary<string, ITerminalSession>();
            bool changed = next.Count != _sessions.Count || next.Any(kv => !_sessions.ContainsKey(kv.Key) || !ReferenceEquals(_sessions[kv.Key], kv.Value));
            _sessions = new Dictionary<string, ITerminalSession>(next);
            if (!changed) return;
            foreach (var id in _rows.Keys.Except(next.Keys).ToList()) _rows.Remove(id);
            foreach (var kv in next)
            {
                ListViewItem item;
                if (!_rows.TryGetValue(kv.Key, out item) || !ReferenceEquals(item.Tag, kv.Value))
                {
                    var host = kv.Value?.Hostname ?? kv.Key;
                    item = new ListViewItem(host + " [" + kv.Key.Substring(0, Math.Min(8, kv.Key.Length)) + "]") { Name = kv.Key, Tag = kv.Value, ToolTipText = host + "\n" + kv.Key };
                    item.SubItems.Add(L10n.T("就绪")); _rows[kv.Key] = item;
                }
            }
            _shell.SetItems(_rows.Values); _empty.SetEmpty(next.Count == 0); UpdateTargets();
        }
        private void SelectAll(bool selected)
        { if (_busy) return; foreach (ListViewItem item in _list.Items) item.Checked = selected; UpdateTargets(); }
        private void UpdateTargets()
        {
            if (_targets == null || _list == null) return;
            var names = _rows.Values.Where(r => r.Checked).Select(r => r.Text).ToArray();
            _targets.Text = names.Length == 0 ? L10n.T("请勾选至少一个 SSH 会话") :
                L10n.Tf("目标 {0} 台：{1}", names.Length, string.Join(", ", names.Take(3))) + (names.Length > 3 ? " …" : "");
        }
        internal async Task ExecuteCommandAsync()
        {
            if (_busy || IsDisposed) return;
            _busy = true;
            try
            {
                RefreshFromProvider();
                var command = _command.Text;
                var selected = _rows.Values.Where(r => r.Checked).ToDictionary(r => r.Name, r => (ITerminalSession)r.Tag);
                if (string.IsNullOrWhiteSpace(command)) { _status.Text = L10n.T("请先输入要执行的命令"); _command.Focus(); return; }
                if (selected.Count == 0) { _status.Text = L10n.T("请勾选至少一个 SSH 会话"); return; }
                var names = string.Join(", ", selected.Select(kv => (kv.Value?.Hostname ?? kv.Key) + " [" + kv.Key.Substring(0, Math.Min(8, kv.Key.Length)) + "]"));
                bool allowed = ConfirmForTest != null ? ConfirmForTest(command) :
                    DangerousCommandPrompt.Confirm(FindForm(), _gate, command, InputSource.BatchPanel, AuditLogger, names);
                if (!allowed) { _status.Text = L10n.T("已取消危险命令"); return; }
                _run = new CancellationTokenSource();
                _execute.Enabled = _command.Enabled = _timeout.Enabled = _list.Enabled = false; _cancel.Enabled = true;
                _results.Clear(); _displayTruncated = false;
                AppendResult(L10n.Tf("目标：{0}\n命令：\n{1}\n\n", names, command));
                RememberCommand(command);
                _status.Text = L10n.Tf("正在向 {0} 个会话发送...", selected.Count);
                foreach (var row in _rows.Values.Where(r => r.Checked)) row.SubItems[1].Text = L10n.T("执行中...");
                var results = await (ExecuteForTest != null
                    ? ExecuteForTest(selected, command, (int)_timeout.Value * 1000, _run.Token)
                    : _executor.ExecuteAsync(selected, command, (int)_timeout.Value * 1000, _run.Token));
                if (IsDisposed || Disposing) return;
                if (ExecuteForTest != null) foreach (var result in results) ShowResult(result);
                var success = results.Count(r => r.IsSuccess);
                _status.Text = L10n.Tf("完成：{0} 成功，{1} 失败，{2} 超时，{3} 取消", success,
                    results.Count(r => r.Status == BatchCommandStatus.Failed), results.Count(r => r.Status == BatchCommandStatus.TimedOut),
                    results.Count(r => r.Status == BatchCommandStatus.Cancelled));
                _status.ForeColor = success == results.Count ? GdtermColorTable.Success : GdtermColorTable.Warning;
            }
            catch (Exception ex)
            {
                DiagLog.Error("Batch.Execute", ex);
                if (!IsDisposed && !Disposing) _status.Text = L10n.T("执行失败：") + ex.Message;
            }
            finally
            {
                _run?.Dispose(); _run = null; _busy = false;
                if (!IsDisposed && !Disposing)
                { _execute.Enabled = _command.Enabled = _timeout.Enabled = _list.Enabled = true; _cancel.Enabled = false; }
            }
        }
        private void OnSessionCompleted(BatchCommandResult result)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) ShowResult(result); })); }
            catch (InvalidOperationException) { }
        }
        private void ShowResult(BatchCommandResult result)
        {
            ListViewItem row;
            string status = result.IsSuccess ? L10n.T("成功") : result.Status == BatchCommandStatus.Cancelled ? L10n.T("已取消") :
                result.Status == BatchCommandStatus.TimedOut ? L10n.T("执行超时") : L10n.T("失败");
            if (_rows.TryGetValue(result.SessionId, out row)) row.SubItems[1].Text = status;
            AppendResult(string.Format("\n━━ {0} [{1}] — {2} ({3:0} ms) ━━\n", result.HostName, result.SessionId, status, result.DurationMs));
            AppendResult(string.Join("\n", result.Output) + "\n");
            if (!string.IsNullOrEmpty(result.StandardError)) AppendResult("stderr:\n" + result.StandardError + "\n");
            if (!string.IsNullOrEmpty(result.ErrorMessage)) AppendResult(result.ErrorMessage + "\n");
            if (result.OutputTruncated) AppendResult(L10n.T("输出过长，已截断。\n"));
        }
        private void AppendResult(string text)
        {
            const int limit = 2 * 1024 * 1024;
            if (_displayTruncated) return;
            int room = limit - _results.TextLength;
            _results.AppendText(text.Length > room ? text.Substring(0, Math.Max(0, room)) : text);
            if (text.Length > room) { _displayTruncated = true; _results.AppendText("\n" + L10n.T("显示与导出已达到上限，后续输出已省略。")); }
        }
        private void CopyResults()
        {
            if (_results.TextLength == 0) return;
            try { Clipboard.SetText(_results.Text); _status.Text = L10n.T("已复制结果"); }
            catch (Exception ex) { _status.Text = ex.Message; }
        }
        private void ExportResults()
        {
            if (_results.TextLength == 0) return;
            using (var dialog = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "batch-results.txt" })
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                    try { File.WriteAllText(dialog.FileName, _results.Text, Encoding.UTF8); _status.Text = L10n.T("结果已导出"); }
                    catch (Exception ex) { _status.Text = L10n.T("导出失败: ") + ex.Message; }
        }
        private void RecallHistory(int delta)
        {
            if (_history.Count == 0 || _busy) return;
            if (_historyCursor == -1) _draft = _command.Text;
            _historyCursor = Math.Max(-1, Math.Min(_history.Count - 1, _historyCursor + delta));
            _command.Text = _historyCursor == -1 ? _draft : _history[_historyCursor];
            _command.SelectionStart = _command.Text.Length;
        }
        private void RememberCommand(string command)
        {
            _history.Remove(command); _history.Insert(0, command);
            if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
            _historyCursor = -1;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
                using (var stream = File.Create(HistoryPath)) new DataContractJsonSerializer(typeof(List<string>)).WriteObject(stream, _history);
            }
            catch (Exception ex) { DiagLog.Swallowed("Batch.History", ex); }
        }
        private void LoadHistory()
        {
            try
            {
                IEnumerable<string> lines = null;
                if (File.Exists(HistoryPath))
                    using (var stream = File.OpenRead(HistoryPath)) lines = (List<string>)new DataContractJsonSerializer(typeof(List<string>)).ReadObject(stream);
                else
                { var legacy = Path.ChangeExtension(HistoryPath, ".txt"); if (File.Exists(legacy)) lines = File.ReadAllLines(legacy); }
                if (lines != null) _history.AddRange(lines.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(50));
            }
            catch (Exception ex) { DiagLog.Swallowed("Batch.History", ex); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _refresh.Dispose(); _run?.Cancel(); _executor.SessionCompleted -= OnSessionCompleted; }
            base.Dispose(disposing);
        }
    }
}
