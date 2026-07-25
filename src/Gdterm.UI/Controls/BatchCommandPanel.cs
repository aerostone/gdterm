using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Terminal;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 批量命令执行面板——多选连接 + 同时执行 + 结果对比
    /// </summary>
    public class BatchCommandPanel : UserControl
    {
        private readonly BatchCommandExecutor _executor;
        private ListView _lvSessions;
        private TextBox _txtCommand;
        private RichTextBox _rtbResults;
        private Button _btnExecute, _btnSelectAll, _btnClear;
        private Label _lblStatus;
        private Dictionary<string, ITerminalSession> _sessions = new Dictionary<string, ITerminalSession>();

        public BatchCommandPanel()
        {
            _executor = new BatchCommandExecutor();
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 30, 30);
            BuildUI();
        }

        public void SetSessions(Dictionary<string, ITerminalSession> sessions)
        {
            _sessions = sessions ?? new Dictionary<string, ITerminalSession>();
            RefreshSessions();
        }

        private void BuildUI()
        {
            var font = new Font("Microsoft YaHei", 9f);

            // ── 顶部：命令输入 ──
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Color.FromArgb(37, 37, 38), Padding = new Padding(8) };

            var lblCmd = new Label { Text = "命令:", Location = new Point(8, 8), AutoSize = true, Font = font, ForeColor = Color.FromArgb(204, 204, 204) };
            _txtCommand = new TextBox { Location = new Point(8, 28), Size = new Size(500, 24), BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle };
            _txtCommand.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ExecuteCommand(); e.SuppressKeyPress = true; } };

            _btnExecute = new Button { Text = "▶ 执行", Location = new Point(520, 26), Size = new Size(80, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Font = font };
            _btnExecute.FlatAppearance.BorderSize = 0;
            _btnExecute.Click += (s, e) => ExecuteCommand();

            _lblStatus = new Label { Text = "", Location = new Point(610, 30), AutoSize = true, Font = font, ForeColor = Color.FromArgb(130, 130, 130) };

            topPanel.Controls.AddRange(new Control[] { lblCmd, _txtCommand, _btnExecute, _lblStatus });

            // ── 左侧：会话列表 ──
            var leftPanel = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Color.FromArgb(37, 37, 38), Padding = new Padding(4) };

            var leftHeader = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Color.FromArgb(37, 37, 38) };
            _btnSelectAll = new Button { Text = "全选", Location = new Point(4, 4), Size = new Size(55, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Microsoft YaHei", 8f) };
            _btnSelectAll.FlatAppearance.BorderSize = 0;
            _btnSelectAll.Click += (s, e) => { foreach (ListViewItem item in _lvSessions.Items) item.Checked = true; };
            var btnDeselect = new Button { Text = "取消", Location = new Point(64, 4), Size = new Size(55, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.FromArgb(204, 204, 204), Font = new Font("Microsoft YaHei", 8f) };
            btnDeselect.FlatAppearance.BorderSize = 0;
            btnDeselect.Click += (s, e) => { foreach (ListViewItem item in _lvSessions.Items) item.Checked = false; };
            leftHeader.Controls.AddRange(new Control[] { _btnSelectAll, btnDeselect });

            _lvSessions = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.None,
                FullRowSelect = true
            };
            _lvSessions.Columns.Add("会话", 200);
            _lvSessions.Columns.Add("状态", 40);

            leftPanel.Controls.Add(_lvSessions);
            leftPanel.Controls.Add(leftHeader);

            // ── 右侧：结果对比 ──
            _rtbResults = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(25, 25, 25),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 9f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = false
            };

            Controls.Add(_rtbResults);
            Controls.Add(leftPanel);
            Controls.Add(topPanel);
        }

        private void RefreshSessions()
        {
            _lvSessions.Items.Clear();
            foreach (var kvp in _sessions)
            {
                var item = new ListViewItem(kvp.Key);
                item.SubItems.Add(kvp.Value?.IsConnected == true ? "●" : "○");
                item.Tag = kvp.Value;
                _lvSessions.Items.Add(item);
            }
        }

        private async void ExecuteCommand()
        {
            var command = _txtCommand.Text.Trim();
            if (string.IsNullOrEmpty(command)) return;

            var selected = new Dictionary<string, ITerminalSession>();
            foreach (ListViewItem item in _lvSessions.Items)
            {
                if (item.Checked && item.Tag is ITerminalSession session && session.IsConnected)
                    selected[item.Text] = session;
            }

            if (selected.Count == 0)
            {
                _lblStatus.Text = "请勾选至少一个会话";
                _lblStatus.ForeColor = Color.FromArgb(255, 100, 100);
                return;
            }

            _btnExecute.Enabled = false;
            _lblStatus.Text = string.Format("正在向 {0} 个会话发送...", selected.Count);
            _lblStatus.ForeColor = Color.FromArgb(200, 200, 200);

            _rtbResults.Clear();
            AppendResult("━━━ 批量命令执行 ━━━\n", Color.FromArgb(78, 201, 176));
            AppendResult(string.Format("命令: {0}\n", command), Color.FromArgb(204, 204, 204));
            AppendResult(string.Format("目标: {0} 个会话\n\n", selected.Count), Color.FromArgb(130, 130, 130));

            var results = await _executor.ExecuteAsync(selected, command, 15000);

            foreach (var result in results)
            {
                var color = result.IsSuccess ? Color.FromArgb(78, 201, 176) : Color.FromArgb(255, 80, 80);
                AppendResult(string.Format("┌─ {0} {1}\n", result.SessionId, result.IsSuccess ? "✓" : "✗"), color);
                foreach (var line in result.Output.Take(20))
                    AppendResult("│  " + line + "\n", Color.FromArgb(180, 180, 180));
                if (result.Output.Count > 20)
                    AppendResult(string.Format("│  ... ({0} 行)\n", result.Output.Count), Color.FromArgb(100, 100, 100));
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    AppendResult("│  错误: " + result.ErrorMessage + "\n", Color.FromArgb(255, 80, 80));
                AppendResult("└────────────────\n\n", Color.FromArgb(80, 80, 80));
            }

            _btnExecute.Enabled = true;
            _lblStatus.Text = string.Format("完成: {0} 成功 / {1} 总计",
                results.Count(r => r.IsSuccess), results.Count);
            _lblStatus.ForeColor = Color.FromArgb(78, 201, 176);
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
