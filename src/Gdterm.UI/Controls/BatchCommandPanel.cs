using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.Terminal;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 批量命令执行面板——多选连接 + 同时执行 + 结果对比
    /// </summary>
    public class BatchCommandPanel : UserControl
    {
        private readonly BatchCommandExecutor _executor;
        private DangerousCommandDetector _dangerousDetector;
        private ListView _lvSessions;
        private AntdUI.Input _txtCommand;
        private RichTextBox _rtbResults;
        private AntdUI.Button _btnExecute;
        private AntdUI.Button _btnSelectAll;
        private AntdUI.Label _lblStatus;
        private Dictionary<string, ITerminalSession> _sessions = new Dictionary<string, ITerminalSession>();

        public BatchCommandPanel()
        {
            _executor = new BatchCommandExecutor();
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();
        }

        public void SetDangerousDetector(DangerousCommandDetector detector)
        {
            _dangerousDetector = detector;
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
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = GdtermColorTable.Surface, Padding = new Padding(8) };

            var lblCmd = new AntdUI.Label { Text = "命令:", Location = DpiScale.P(this, 8, 8), AutoSize = true, Font = font, ForeColor = GdtermColorTable.Foreground };
            _txtCommand = new AntdUI.Input { Location = DpiScale.P(this, 8, 28), Size = DpiScale.S(this, 500, 24), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = new Font("Consolas", 9f)};
            _txtCommand.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ExecuteCommand(); e.SuppressKeyPress = true; } };

            _btnExecute = new AntdUI.Button { Text = "▶ 执行", Location = DpiScale.P(this, 520, 26), Size = DpiScale.S(this, 80, 28), BackColor = GdtermColorTable.Accent, ForeColor = Color.White, Font = font };
            _btnExecute.Click += (s, e) => ExecuteCommand();

            _lblStatus = new AntdUI.Label { Text = "", Location = DpiScale.P(this, 610, 30), AutoSize = true, Font = font, ForeColor = GdtermColorTable.Muted };

            topPanel.Controls.AddRange(new Control[] { lblCmd, _txtCommand, _btnExecute, _lblStatus });

            // ── 左侧：会话列表 ──
            var leftPanel = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = GdtermColorTable.Surface, Padding = new Padding(4) };

            var leftHeader = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = GdtermColorTable.Surface };
            _btnSelectAll = new AntdUI.Button { Text = "全选", Location = DpiScale.P(this, 4, 4), AutoSize = true, BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.UiFont(-1f) };
            _btnSelectAll.Click += (s, e) => { foreach (ListViewItem item in _lvSessions.Items) item.Checked = true; };
            var btnDeselect = new AntdUI.Button { Text = "取消", Location = DpiScale.P(this, 64, 4), AutoSize = true, BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = Services.FormFontPolicy.UiFont(-1f) };
            btnDeselect.Click += (s, e) => { foreach (ListViewItem item in _lvSessions.Items) item.Checked = false; };
            leftHeader.Controls.AddRange(new Control[] { _btnSelectAll, btnDeselect });

            _lvSessions = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", 8.5f)
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
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = new Font("Consolas", 9f),
                ReadOnly = true
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
                _lblStatus.ForeColor = GdtermColorTable.Danger;
                return;
            }

            // 危险命令闸门（批量入口）
            if (_dangerousDetector != null)
            {
                try
                {
                    var check = _dangerousDetector.Check(command);
                    if (check != null && check.IsDangerous)
                    {
                        using (var dlg = new DangerousCommandDialog(command, check))
                        {
                            dlg.ShowDialog(FindForm());
                            if (!dlg.IsConfirmed)
                            {
                                _lblStatus.Text = "已取消危险命令";
                                _lblStatus.ForeColor = GdtermColorTable.Warning;
                                return;
                            }
                            if (dlg.RememberChoice)
                            {
                                try { _dangerousDetector.AddToWhitelist(command); } catch { }
                            }
                        }
                    }
                }
                catch { }
            }

            _btnExecute.Enabled = false;
            _lblStatus.Text = string.Format("正在向 {0} 个会话发送...", selected.Count);
            _lblStatus.ForeColor = GdtermColorTable.Foreground;

            _rtbResults.Clear();
            AppendResult("━━━ 批量命令执行 ━━━\n", GdtermColorTable.Success);
            AppendResult(string.Format("命令: {0}\n", command), GdtermColorTable.Foreground);
            AppendResult(string.Format("目标: {0} 个会话\n\n", selected.Count), GdtermColorTable.Muted);

            var results = await _executor.ExecuteAsync(selected, command, 15000);

            foreach (var result in results)
            {
                var color = result.IsSuccess ? GdtermColorTable.Success : GdtermColorTable.Danger;
                AppendResult(string.Format("┌─ {0} {1}\n", result.SessionId, result.IsSuccess ? "✓" : "✗"), color);
                foreach (var line in result.Output.Take(20))
                    AppendResult("│  " + line + "\n", GdtermColorTable.Muted);
                if (result.Output.Count > 20)
                    AppendResult(string.Format("│  ... ({0} 行)\n", result.Output.Count), GdtermColorTable.Muted);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    AppendResult("│  错误: " + result.ErrorMessage + "\n", GdtermColorTable.Danger);
                AppendResult("└────────────────\n\n", GdtermColorTable.Border);
            }

            _btnExecute.Enabled = true;
            _lblStatus.Text = string.Format("完成: {0} 成功 / {1} 总计",
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
