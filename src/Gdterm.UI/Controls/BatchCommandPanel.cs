using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 批量命令执行面板——多选连接 + 同时执行 + 结果对比
    /// </summary>
    public class BatchCommandPanel : UserControl
    {
        private readonly BatchCommandExecutor _executor;
        private DangerousCommandDetector _dangerousDetector;
        private CommandGate _commandGate;
        private ListView _lvSessions;
        private ListShell _shell;
        private EmptyStateView _emptySessions;
        private AntdUI.Input _txtCommand;
        private RichTextBox _rtbResults;
        private AntdUI.Button _btnExecute;
        private AntdUI.Button _btnSelectAll;
        private Panel _leftPanel;
        private AntdUI.Label _lblStatus;
        private Dictionary<string, ITerminalSession> _sessions = new Dictionary<string, ITerminalSession>();
        // 批量命令历史（↑/↓ 召回）：批量入口的命令都是精心拼出来的，关掉面板就丢等于每次重写
        private readonly List<string> _history = new List<string>();
        private int _historyCursor = -1;
        private static readonly string HistoryPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "config", "batch-history.txt");

        public BatchCommandPanel()
        {
            _executor = new BatchCommandExecutor();
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();
            LoadHistory();
        }

        /// <summary>共享命令闸门（缺省时按检测器现建，会话豁免就不与其它入口互通了）。</summary>
        public void SetCommandGate(CommandGate gate)
        {
            _commandGate = gate ?? (_dangerousDetector != null ? new CommandGate(_dangerousDetector) : null);
        }

        public void SetDangerousDetector(DangerousCommandDetector detector)
        {
            _dangerousDetector = detector;
            if (_commandGate == null && detector != null) _commandGate = new CommandGate(detector);
        }

        /// <summary>按可用宽夹取命令输入框（不低于 120 设计 px，按钮与标签优先不被挤没）。</summary>
        private void FitCommandBox(Control row)
        {
            if (_txtCommand == null || row == null) return;
            int reserve = 0;
            foreach (Control c in row.Controls)
            {
                if (c == _txtCommand) continue;
                if (!c.Visible) continue;
                reserve += c.Width + c.Margin.Horizontal;
            }
            int avail = row.ClientSize.Width - row.Padding.Horizontal - reserve - _txtCommand.Margin.Horizontal;
            int min = DpiScale.V(this, 120);
            int w = Math.Max(min, avail);
            if (_txtCommand.Width != w) _txtCommand.Width = w;
        }

        public void SetSessions(Dictionary<string, ITerminalSession> sessions)
        {
            _sessions = sessions ?? new Dictionary<string, ITerminalSession>();
            RefreshSessions();
        }

        private void BuildUI()
        {
            var font = Services.FormFontPolicy.UiFont();

            // ── 顶部：命令输入 ──
            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 8))
            };

            var lblCmd = FormFontPolicy.FitSingleLineLabel(new AntdUI.Label { Text = L10n.T("命令:"), Margin = new Padding(0, DpiScale.V(this, 6), DpiScale.V(this, 8), 0), Font = font, ForeColor = GdtermColorTable.Foreground });
            // 输入框高度与项目 38 地板统一（原先 28 在大字号下文字被裁）
            // 宽度不写死 500：侧栏只有 ~360，固定宽会顶出右边界（CI 0.1.381 越界 x=16 w=500）。
            // 先给一个设计宽，随后由 FitCommandBox 按宿主实际宽夹取。
            _txtCommand = new AntdUI.Input { Width = DpiScale.V(this, 500), Height = FormFontPolicy.FieldHeight(this), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.MonoFont(), Margin = new Padding(0, DpiScale.V(this, 1), DpiScale.V(this, 8), 0)};
            _txtCommand.KeyDown += OnCommandKeyDown;

            _btnExecute = new AntdUI.Button {
                Text = L10n.T("▶ 执行"),
                Type = AntdUI.TTypeMini.Primary,
                Font = font,
                Margin = new Padding(0, 0, DpiScale.V(this, 8), 0)
            };
            Services.FormFontPolicy.SizeActionButton(this, _btnExecute, 10, 4);
            _btnExecute.Click += (s, e) => ExecuteCommand();

            _lblStatus = new AntdUI.Label { Text = "", Margin = new Padding(0, DpiScale.V(this, 6), 0, 0), Font = font, ForeColor = GdtermColorTable.Muted };
            FormFontPolicy.FitSingleLineLabel(_lblStatus);

            topPanel.Controls.AddRange(new Control[] { lblCmd, _txtCommand, _btnExecute, _lblStatus });
            // 宿主变宽/变窄时重算输入框宽度（侧栏 360 / 独立窗口 900 都要合适）
            topPanel.SizeChanged += (s2, e2) => FitCommandBox(topPanel);
            FitCommandBox(topPanel);

            // ── 左侧：会话列表 ──
            // 左栏不写死 250：d150 下它等于 375px，比 360 的侧栏宿主还宽 → 越界（矩阵第二批判红）。
            // 由 OnSizeChanged 里的 ClampLeftPane 按宿主宽度收口，右侧永远留够结果区。
            var leftPanel = new Panel { Dock = DockStyle.Left, BackColor = GdtermColorTable.Surface, Padding = new Padding(DpiScale.V(this, 4)) };
            _leftPanel = leftPanel;

            var leftHeader = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = GdtermColorTable.Surface, Padding = new Padding(DpiScale.V(this, 4)) };
            _btnSelectAll = new AntdUI.Button {
                Text = L10n.T("全选"),
                Margin = new Padding(0, 0, DpiScale.V(this, 6), 0),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(-1f)
            };
            Services.FormFontPolicy.SizeActionButton(this, _btnSelectAll, 10, 3);
            _btnSelectAll.Click += (s, e) => { foreach (ListViewItem item in _lvSessions.Items) item.Checked = true; };
            var btnDeselect = new AntdUI.Button {
                Text = L10n.T("取消"),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(-1f)
            };
            Services.FormFontPolicy.SizeActionButton(this, btnDeselect, 10, 3);
            btnDeselect.Click += (s, e) => { foreach (ListViewItem item in _lvSessions.Items) item.Checked = false; };
            leftHeader.Controls.AddRange(new Control[] { _btnSelectAll, btnDeselect });

            _lvSessions = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                FullRowSelect = true
            };
            _lvSessions.Columns.Add(L10n.T("会话"), 200);
            _lvSessions.Columns.Add(L10n.T("状态"), 40);

            // 列表壳：排序 + 计数（会话多时按名字/状态排；不加过滤——勾选批量执行依赖全量可见，
            // 过滤后“全选”只作用于可见子集，语义会变，故这里 showFilter:false 只开排序）
            _shell = new ListShell(_lvSessions, showFilter: false);

            leftPanel.Controls.Add(_shell);
            leftPanel.Controls.Add(leftHeader);
            // 会话列表空态：一个会话都没连上时，别让左侧只剩一片空白（R09 / UX 审计 C1）
            _emptySessions = EmptyStateView.Attach(leftPanel, L10n.T("还没有可广播的会话"),
                L10n.T("打开几个终端标签后，这里会列出可批量下发的会话"));
            RefreshSessions();   // 空态默认 Visible=false：构造期早于此挂载，这里补一次同步

            // ── 右侧：结果对比 ──
            _rtbResults = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                ReadOnly = true,
                WordWrap = false
            };

            Controls.Add(_rtbResults);
            Controls.Add(leftPanel);
            Controls.Add(topPanel);
            ClampLeftPane();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        /// <summary>
        /// 左栏宽度收口：期望 250 逻辑 px，但绝不超过"宿主宽 − 结果区下限"。
        /// 独立窗口（≥900）下仍是 250；侧栏 360 + d150 下退到 ~210，右侧不至于只剩一条缝。
        /// </summary>
        private void ClampLeftPane()
        {
            if (_leftPanel == null) return;
            int want = DpiScale.V(this, 250);
            int floor = DpiScale.V(this, 120);
            int reserve = DpiScale.V(this, 150);
            int room = ClientSize.Width - reserve;
            int w = Math.Min(want, room);
            if (w < floor) w = Math.Min(floor, Math.Max(0, ClientSize.Width));
            if (w > 0 && _leftPanel.Width != w) _leftPanel.Width = w;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ClampLeftPane();
        }

        private void RefreshSessions()
        {
            var items = new List<ListViewItem>();
            foreach (var kvp in _sessions)
            {
                var item = new ListViewItem(kvp.Key);
                item.SubItems.Add(kvp.Value?.IsConnected == true ? "●" : "○");
                item.Tag = kvp.Value;
                items.Add(item);
            }
            _shell.SetItems(items);
            if (_emptySessions != null) _emptySessions.SetEmpty(items.Count == 0);
        }

        private void OnCommandKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { ExecuteCommand(); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Up) { RecallHistory(-1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Down) { RecallHistory(+1); e.SuppressKeyPress = true; }
        }

        /// <summary>↑/↓ 在历史里上下移动（-1 更早，+1 更新；到底部回空）。</summary>
        private void RecallHistory(int delta)
        {
            if (_history.Count == 0) return;
            if (_historyCursor < 0) _historyCursor = _history.Count;   // 首次按 ↑ 从最新一条往上
            _historyCursor = Math.Max(0, Math.Min(_history.Count, _historyCursor + delta));
            _txtCommand.Text = _historyCursor >= _history.Count ? "" : _history[_historyCursor];
            try { _txtCommand.SelectionStart = _txtCommand.Text.Length; } catch (Exception) { }
        }

        /// <summary>命令入历史（去重、最近优先、上限 50），并落盘到 data/config/batch-history.json。</summary>
        private void RememberCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            _history.Remove(command);
            _history.Insert(0, command);
            while (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
            _historyCursor = -1;
            try
            {
                var dir = Path.GetDirectoryName(HistoryPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 一行一条：命令本身不含换行，txt 比 JSON 更好排查（可直接用记事本看/改）
                File.WriteAllLines(HistoryPath, _history.ToArray(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 历史写失败不影响执行本身，但必须留痕（便携版目录只读是最常见原因）
                try { DiagLog.Error("BatchCommand.History", ex); } catch (Exception) { }
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                _history.Clear();
                foreach (var line in File.ReadAllLines(HistoryPath, Encoding.UTF8))
                {
                    var v = (line ?? "").Trim();
                    if (v.Length == 0 || _history.Contains(v)) continue;
                    _history.Add(v);
                    if (_history.Count >= 50) break;
                }
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("BatchCommand.LoadHistory", ex); } catch (Exception) { }
            }
        }

        private async void ExecuteCommand()
        {
            var command = _txtCommand.Text.Trim();
            if (string.IsNullOrEmpty(command))
            {
                // 原来这里直接 return：点「执行」毫无反应，用户以为按钮坏了
                _lblStatus.Text = L10n.T("请先输入要执行的命令");
                _lblStatus.ForeColor = GdtermColorTable.Warning;
                ToastNotifier.Warning(L10n.T("请输入要批量执行的命令"));
                _txtCommand.Focus();
                return;
            }

            var selected = new Dictionary<string, ITerminalSession>();
            foreach (ListViewItem item in _lvSessions.Items)
            {
                if (item.Checked && item.Tag is ITerminalSession session && session.IsConnected)
                    selected[item.Text] = session;
            }

            if (selected.Count == 0)
            {
                _lblStatus.Text = L10n.T("请勾选至少一个会话");
                _lblStatus.ForeColor = GdtermColorTable.Danger;
                return;
            }

            // 危险命令闸门（批量入口）——一次发给 N 个会话，判定与审计统一走 DangerousCommandPrompt
            if (!DangerousCommandPrompt.Confirm(
                    FindForm(), _commandGate ?? (_dangerousDetector != null ? new CommandGate(_dangerousDetector) : null),
                    command, InputSource.BatchPanel, null, "batch"))
            {
                _lblStatus.Text = L10n.T("已取消危险命令");
                _lblStatus.ForeColor = GdtermColorTable.Warning;
                return;
            }

            RememberCommand(command);
            _btnExecute.Enabled = false;
            _lblStatus.Text = string.Format(L10n.T("正在向 {0} 个会话发送..."), selected.Count);
            _lblStatus.ForeColor = GdtermColorTable.Foreground;

            _rtbResults.Clear();
            AppendResult(L10n.T("━━━ 批量命令执行 ━━━\n"), GdtermColorTable.Success);
            AppendResult(string.Format(L10n.T("命令: {0}\n"), command), GdtermColorTable.Foreground);
            AppendResult(string.Format(L10n.T("目标: {0} 个会话\n\n"), selected.Count), GdtermColorTable.Muted);

            var results = await _executor.ExecuteAsync(selected, command, 15000);

            foreach (var result in results)
            {
                var color = result.IsSuccess ? GdtermColorTable.Success : GdtermColorTable.Danger;
                AppendResult(string.Format("┌─ {0} {1}\n", result.SessionId, result.IsSuccess ? "✓" : "✗"), color);
                foreach (var line in result.Output.Take(20))
                    AppendResult("│  " + line + "\n", GdtermColorTable.Muted);
                if (result.Output.Count > 20)
                    AppendResult(string.Format(L10n.T("│  ... ({0} 行)\n"), result.Output.Count), GdtermColorTable.Muted);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    AppendResult(L10n.T("│  错误: ") + result.ErrorMessage + "\n", GdtermColorTable.Danger);
                AppendResult("└────────────────\n\n", GdtermColorTable.Border);
            }

            _btnExecute.Enabled = true;
            _lblStatus.Text = string.Format(L10n.T("完成: {0} 成功 / {1} 总计"),
                results.Count(r => r.IsSuccess), results.Count);
            _lblStatus.ForeColor = GdtermColorTable.Success;
        }

        private void AppendResult(string text, Color color)
        {
            _rtbResults.SelectionStart = _rtbResults.TextLength;
            _rtbResults.SelectionLength = 0;
            _rtbResults.SelectionColor = color;
            _rtbResults.AppendText(text);
        }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
