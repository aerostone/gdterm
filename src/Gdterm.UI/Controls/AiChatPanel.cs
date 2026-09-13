using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Terminal;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// AI 助手聊天侧栏——流式逐 token 显示 + 会话上下文 + 命令一键发送。
    /// 后端 SendMessageStreamingAsync 早已存在；本面板是它缺的 UI。
    /// 发送链路走 TerminalControl.TrySendInput（带危险命令闸门），不直写 session。
    /// </summary>
    public class AiChatPanel : UserControl
    {
        private readonly IAiAssistantService _aiService;
        private readonly Func<TerminalControl> _getActiveTerminal;

        private RichTextBox _historyBox;
        private AntdUI.Input _inputBox;
        private AntdUI.Button _btnSend;
        private AntdUI.Button _btnClear;
        private AntdUI.Label _statusLabel;
        private FlowLayoutPanel _cmdBar;
        private CancellationTokenSource _cts;
        private bool _sending;

        public AiChatPanel(IAiAssistantService aiService, Func<TerminalControl> getActiveTerminal)
        {
            _aiService = aiService ?? throw new ArgumentNullException("aiService");
            _getActiveTerminal = getActiveTerminal;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();
        }

        private void BuildUI()
        {
            Font = FormFontPolicy.UiFont();

            _historyBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.None,
                Font = FormFontPolicy.UiFont(),
                HideSelection = false
            };
            Controls.Add(_historyBox);

            _cmdBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 4)),
                Visible = false
            };
            Controls.Add(_cmdBar);

            _statusLabel = new AntdUI.Label
            {
                Text = "AI 就绪：输入问题，Enter 发送",
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Font = FormFontPolicy.UiFont(-1f)
            };
            Controls.Add(_statusLabel);

            var bottomRow = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ColumnCount = 3,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, DpiScale.V(this, 4), 0, 0)
            };
            bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottomRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            _inputBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, fieldH),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                PlaceholderText = "问 AI：如 这台机器 CPU 为什么这么高？"
            };
            _inputBox.KeyDown += OnInputKeyDown;
            bottomRow.Controls.Add(_inputBox, 0, 0);

            _btnSend = new AntdUI.Button
            {
                Text = "发送",
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 4), DpiScale.V(this, 12), DpiScale.V(this, 4)),
                Margin = new Padding(DpiScale.V(this, 4), 0, 0, 0)
            };
            _btnSend.Click += (s, e) => BeginSend();
            bottomRow.Controls.Add(_btnSend, 1, 0);

            _btnClear = new AntdUI.Button
            {
                Text = "清空",
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 4), DpiScale.V(this, 10), DpiScale.V(this, 4)),
                Margin = new Padding(DpiScale.V(this, 4), 0, 0, 0)
            };
            _btnClear.Click += (s, e) => { try { _aiService.ClearHistory(); _historyBox.Clear(); AppendSys("已清空对话历史。"); } catch { } };
            bottomRow.Controls.Add(_btnClear, 2, 0);
            Controls.Add(bottomRow);

            AppendSys("AI 助手：回复中的命令会出现在下方快捷条，点一下即发往当前终端（过危险命令确认）。");
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                BeginSend();
            }
        }

        private void BeginSend()
        {
            if (_sending) return;
            var text = (_inputBox.Text ?? "").Trim();
            if (text.Length == 0) return;
            _inputBox.Text = "";
            _sending = true;
            _statusLabel.Text = "AI 思考中…";
            _cmdBar.Controls.Clear();
            _cmdBar.Visible = false;
            AppendUser(text);

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            TerminalControl tc = null;
            ITerminalSession session = null;
            try { tc = _getActiveTerminal != null ? _getActiveTerminal() : null; } catch { }
            try { session = tc != null ? tc.Session : null; } catch { }

            var assistantStart = _historyBox.TextLength;
            AppendAssistantPrefix();
            var acc = new System.Text.StringBuilder();

            Task.Run(async () =>
            {
                try
                {
                    var resp = await _aiService.SendMessageStreamingAsync(text, session, ct, token =>
                    {
                        try
                        {
                            acc.Append(token);
                            var snapshot = acc.ToString();
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    _historyBox.Select(assistantStart, _historyBox.TextLength - assistantStart);
                                    _historyBox.SelectedText = snapshot;
                                }
                                catch { }
                            }));
                        }
                        catch { }
                    }).ConfigureAwait(false);
                    BeginInvoke(new Action(() => OnSendDone(resp)));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _sending = false;
                        _statusLabel.Text = "发送失败";
                        AppendSys("AI 调用失败：" + ex.Message);
                    }));
                }
            });
        }

        private void OnSendDone(Gdterm.AI.Models.AiResponse resp)
        {
            _sending = false;
            try { if (_cts != null) { _cts.Dispose(); _cts = null; } } catch { }
            if (resp == null || !resp.IsSuccess)
            {
                _statusLabel.Text = "AI 未配置或调用失败";
                AppendSys(resp != null ? resp.ErrorMessage : "AI 返回为空。请检查 工具 → AI 助手设置。");
                return;
            }
            _statusLabel.Text = "AI 就绪";
            var cmds = resp.SuggestedCommands;
            if (cmds == null || cmds.Count == 0) return;
            _cmdBar.Controls.Clear();
            foreach (var cmd in cmds)
            {
                var c = cmd;
                if (string.IsNullOrWhiteSpace(c)) continue;
                var b = new AntdUI.Button
                {
                    Text = c.Length > 42 ? c.Substring(0, 42) + "…" : c,
                    AutoSize = true,
                    Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3)),
                    Margin = new Padding(0, 0, DpiScale.V(this, 4), 0)
                };
                b.Click += (s, e) =>
                {
                    try
                    {
                        var tc = _getActiveTerminal != null ? _getActiveTerminal() : null;
                        if (tc == null) { _statusLabel.Text = "无活动终端"; return; }
                        // 经命令行闸门：危险命令确认 + 审计（与片段搜索/底栏命令同链路）。
                        if (!tc.TrySendInput(c + "\r", isCommandLine: true)) return;
                        _statusLabel.Text = "已发送到终端";
                    }
                    catch (System.Exception exSwallowed) { try { Gdterm.UI.Diagnostics.DiagLog.Swallowed("AiChatPanel", exSwallowed); } catch { } }
                };
                _cmdBar.Controls.Add(b);
            }
            _cmdBar.Visible = _cmdBar.Controls.Count > 0;
        }

        private void AppendUser(string text)
        {
            AppendLine("你： " + text, GdtermColorTable.Accent, true);
        }

        private void AppendAssistantPrefix()
        {
            AppendLine("AI： ", GdtermColorTable.Foreground, false);
        }

        private void AppendSys(string text)
        {
            AppendLine(text, GdtermColorTable.Muted, false);
        }

        private void AppendLine(string text, Color color, bool bold)
        {
            try
            {
                int start = _historyBox.TextLength;
                _historyBox.AppendText(text + "\r\n");
                _historyBox.Select(start, text.Length);
                _historyBox.SelectionColor = color;
                _historyBox.SelectionFont = new Font(_historyBox.Font, bold ? FontStyle.Bold : FontStyle.Regular);
                _historyBox.Select(_historyBox.TextLength, 0);
                _historyBox.SelectionColor = _historyBox.ForeColor;
                _historyBox.ScrollToCaret();
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; } } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
