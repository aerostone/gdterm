using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 代码片段搜索面板——Ctrl+Shift+P 模糊搜索 + 变量填充弹窗
    /// </summary>
    public class SnippetSearchPanel : Panel
    {
        private TextBox _txtSearch;
        private ListView _lvResults;
        private Label _lblHint;
        private readonly List<QuickCommand> _commands;
        private List<QuickCommand> _filtered;
        private ITerminalSession _activeSession;
        private TerminalControl _activeTerminal;

        /// <summary>当片段被选中并发送时触发</summary>
        public event Action<string, QuickCommand> SnippetSent;

        public SnippetSearchPanel(List<QuickCommand> commands)
        {
            _commands = commands ?? new List<QuickCommand>();
            _filtered = new List<QuickCommand>(_commands);
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            Visible = false;
            BuildUI();
        }

        public void SetActiveTerminal(TerminalControl terminal)
        {
            _activeTerminal = terminal;
            _activeSession = terminal != null ? terminal.Session : null;
        }

        public void SetActiveSession(ITerminalSession session)
        {
            _activeTerminal = null;
            _activeSession = session;
        }

        private void BuildUI()
        {
            // 搜索框
            _txtSearch = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", 11f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _txtSearch.TextChanged += (s, e) => FilterResults();
            _txtSearch.KeyDown += OnSearchKeyDown;

            // 提示
            _lblHint = new Label
            {
                Text = "输入关键词搜索快捷命令 | Enter 执行 | Esc 关闭",
                Dock = DockStyle.Top,
                Height = 22,
                Font = Services.FormFontPolicy.UiFont(-1f),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = GdtermColorTable.Background,
                TextAlign = ContentAlignment.MiddleLeft
            };

            // 结果列表
            _lvResults = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(),
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.None
            };
            _lvResults.Columns.Add("匹配结果", 600);
            _lvResults.DoubleClick += (s, e) => ExecuteSelected();

            Controls.Add(_lvResults);
            Controls.Add(_lblHint);
            Controls.Add(_txtSearch);
        }

        /// <summary>显示搜索面板</summary>
        public void ShowAndFocus()
        {
            Visible = true;
            BringToFront();
            _txtSearch.Clear();
            _txtSearch.Focus();
            FilterResults();
        }

        /// <summary>更新命令列表</summary>
        public void SetCommands(List<QuickCommand> commands)
        {
            _commands.Clear();
            if (commands != null) _commands.AddRange(commands);
            FilterResults();
        }

        private void FilterResults()
        {
            var query = _txtSearch?.Text?.Trim();
            _lvResults.Items.Clear();

            if (string.IsNullOrEmpty(query))
            {
                _filtered = _commands.Take(20).ToList();
            }
            else
            {
                _filtered = _commands
                    .Where(c => FuzzyMatch(c.Name, query) || FuzzyMatch(c.Command, query) || FuzzyMatch(c.Group, query))
                    .Take(20)
                    .ToList();
            }

            foreach (var cmd in _filtered)
            {
                var item = new ListViewItem(string.Format("[{0}] {1} — {2}", cmd.Group, cmd.Name, cmd.Command));
                item.Tag = cmd;
                _lvResults.Items.Add(item);
            }

            if (_lvResults.Items.Count > 0)
                _lvResults.Items[0].Selected = true;
        }

        private bool FuzzyMatch(string text, string query)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return false;
            return text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Visible = false; return; }
            if (e.KeyCode == Keys.Enter) { ExecuteSelected(); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Down && _lvResults.Items.Count > 0)
            {
                int idx = _lvResults.SelectedIndices.Count > 0 ? _lvResults.SelectedIndices[0] + 1 : 0;
                if (idx < _lvResults.Items.Count) { _lvResults.Items[idx].Selected = true; _lvResults.Items[idx].EnsureVisible(); }
                e.SuppressKeyPress = true;
            }
            if (e.KeyCode == Keys.Up && _lvResults.Items.Count > 0)
            {
                int idx = _lvResults.SelectedIndices.Count > 0 ? _lvResults.SelectedIndices[0] - 1 : 0;
                if (idx >= 0) { _lvResults.Items[idx].Selected = true; _lvResults.Items[idx].EnsureVisible(); }
                e.SuppressKeyPress = true;
            }
        }

        private void ExecuteSelected()
        {
            if (_lvResults.SelectedItems.Count == 0) return;
            var cmd = _lvResults.SelectedItems[0].Tag as QuickCommand;
            if (cmd == null) return;

            var connected = (_activeTerminal != null && _activeTerminal.Session != null && _activeTerminal.Session.IsConnected)
                || (_activeSession?.IsConnected == true);
            if (!connected)
            {
                Visible = false;
                return;
            }

            string toSend = null;
            if (cmd.Command.Contains("{"))
            {
                toSend = ShowVariableDialog(cmd);
                if (toSend == null)
                {
                    Visible = false;
                    return;
                }
            }
            else
            {
                toSend = (cmd.PreCommand != null ? cmd.PreCommand + " && " : "") + cmd.Command;
            }

            // 不直发 session——由 MainForm 经 TerminalControl 闸门发送
            SnippetSent?.Invoke(toSend, cmd);

            Visible = false;
        }

        private string ShowVariableDialog(QuickCommand cmd)
        {
            // 检测命令中的占位符
            var placeholders = new List<string>();
            var command = cmd.Command;
            int idx = 0;
            while ((idx = command.IndexOf("{", idx)) >= 0)
            {
                var end = command.IndexOf("}", idx);
                if (end < 0) break;
                var ph = command.Substring(idx, end - idx + 1);
                if (!placeholders.Contains(ph) && !ph.StartsWith("{env:"))
                    placeholders.Add(ph);
                idx = end + 1;
            }

            if (placeholders.Count == 0)
                return (cmd.PreCommand != null ? cmd.PreCommand + " && " : "") + cmd.Command;

            var form = new Form
            {
                Text = "填写变量 — " + cmd.Name,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false
            };
            form.Size = DpiScale.S(form, 400, 40 + placeholders.Count * 40 + 60);

            var inputs = new Dictionary<string, TextBox>();
            int y = 15;
            foreach (var ph in placeholders)
            {
                var lbl = new Label { Text = ph + ":", Location = new Point(15, y + 3), AutoSize = true, Font = Services.FormFontPolicy.UiFont(), ForeColor = GdtermColorTable.Foreground };
                var txt = new TextBox { Location = DpiScale.P(form, 100, y), Size = DpiScale.S(form, 260, 24), BackColor = Color.FromArgb(45, 45, 48), ForeColor = GdtermColorTable.Foreground, Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle };
                form.Controls.AddRange(new Control[] { lbl, txt });
                inputs[ph] = txt;
                y += 36;
            }

            var btnOk = new Button { Text = "执行", Size = DpiScale.S(form, 80, 28), Location = DpiScale.P(form, 190, y), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = GdtermColorTable.Accent, ForeColor = Color.White };
            var btnCancel = new Button { Text = "取消", Size = DpiScale.S(form, 80, 28), Location = DpiScale.P(form, 280, y), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = GdtermColorTable.Hover, ForeColor = GdtermColorTable.Foreground };
            form.Controls.AddRange(new Control[] { btnOk, btnCancel });
            form.AcceptButton = btnOk; form.CancelButton = btnCancel;

            if (form.ShowDialog(this) != DialogResult.OK) return null;

            var result = cmd.Command;
            foreach (var kvp in inputs)
                result = result.Replace(kvp.Key, kvp.Value.Text);

            if (!string.IsNullOrEmpty(cmd.PreCommand))
                result = cmd.PreCommand + " && " + result;
            return result;
        }

        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
