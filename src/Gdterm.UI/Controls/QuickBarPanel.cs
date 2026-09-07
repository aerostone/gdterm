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
    /// 快捷命令栏——WindTerm 风格的底部快捷命令面板
    /// 支持分组切换、按钮点击发送命令、占位符替换、自定义编辑
    /// </summary>
    public class QuickBarPanel : UserControl
    {
        private readonly List<QuickCommand> _commands;
        private readonly Dictionary<string, ITerminalSession> _sessions;

        private FlowLayoutPanel _buttonPanel;
        private Panel _groupPanel;
        private string _activeGroup;
        private ITerminalSession _activeSession;
        private TerminalControl _activeTerminal;
        private string _hostName;
        private string _userName;
        private Dictionary<string, AntdUI.Button> _groupButtons;
        // 快捷命令固定两行，其他命令走“更多”菜单，避免系统滚动条占用按钮空间。
        private const int DesignHeight = 66;
        private const int MaxVisibleCommands = 4;

        // ── 事件 ──
        /// <summary>当命令发送到终端时触发</summary>
        public event Action<string, string> CommandSent; // command, groupName

        /// <summary>当请求编辑命令时触发</summary>
        public event Action<QuickCommand> EditRequested;

        /// <summary>当请求添加命令时触发</summary>
        public event Action<string> AddRequested; // groupName

        public QuickBarPanel()
        {
            _commands = new List<QuickCommand>();
            _sessions = new Dictionary<string, ITerminalSession>();
            _groupButtons = new Dictionary<string, AntdUI.Button>();
            BuildUI();
        }

        public QuickBarPanel(List<QuickCommand> commands) : this()
        {
            _commands = commands ?? new List<QuickCommand>();
            RefreshGroups();
        }

        // ── 公共方法 ──

        /// <summary>绑定活动终端控件（优先，发送走 TerminalControl 危险命令闸门）</summary>
        public void SetActiveTerminal(TerminalControl terminal, string host = null, string user = null)
        {
            _activeTerminal = terminal;
            _activeSession = terminal != null ? terminal.Session : null;
            _hostName = host ?? _hostName ?? "";
            _userName = user ?? _userName ?? "";
        }

        /// <summary>仅绑定会话（无闸门，兼容旧调用）</summary>
        public void SetActiveSession(ITerminalSession session, string host = null, string user = null)
        {
            _activeTerminal = null;
            _activeSession = session;
            _hostName = host ?? "";
            _userName = user ?? "";
        }

        /// <summary>刷新命令列表</summary>
        public void SetCommands(List<QuickCommand> commands)
        {
            _commands.Clear();
            if (commands != null) _commands.AddRange(commands);
            RefreshGroups();
            RefreshButtons();
        }

        /// <summary>添加命令到指定分组</summary>
        public void AddCommand(QuickCommand cmd)
        {
            _commands.Add(cmd);
            RefreshGroups();
            RefreshButtons();
        }

        /// <summary>删除命令</summary>
        public void RemoveCommand(string commandId)
        {
            _commands.RemoveAll(c => c.Id == commandId);
            RefreshGroups();
            RefreshButtons();
        }

        // ── UI 构建 ──

        private void BuildUI()
        {
            Dock = DockStyle.Bottom;
            AutoSize = false;
            MinimumSize = new Size(0, DpiScale.V(this, DesignHeight));
            BackColor = GdtermColorTable.Surface;
            Padding = new Padding(0);

            // 左侧：分组标签
            _groupPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(4, 4, 0, 4),
                WrapContents = false
            };

            // 右侧：固定两行；溢出命令由“更多”菜单承接，不显示系统滚动条。
            _buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(4, 3, 4, 3),
                WrapContents = true,
                AutoScroll = false
            };

            Controls.Add(_buttonPanel);
            Controls.Add(_groupPanel);

            // 右键菜单
            var ctx = new ContextMenuStrip();
            ctx.BackColor = GdtermColorTable.Surface;
            ctx.ForeColor = GdtermColorTable.Foreground;
            ctx.Renderer = new DarkMenuRenderer();

            var miAdd = new ToolStripMenuItem("➕ 添加快捷命令");
            miAdd.Click += (s, e) => AddRequested?.Invoke(_activeGroup ?? "自定义");
            ctx.Items.Add(miAdd);

            var miEdit = new ToolStripMenuItem("✏️ 编辑");
            miEdit.Click += OnEditClick;
            ctx.Items.Add(miEdit);

            var miDelete = new ToolStripMenuItem("🗑️ 删除");
            miDelete.Click += OnDeleteClick;
            ctx.Items.Add(miDelete);

            ctx.Items.Add(new ToolStripSeparator());

            var miSendRaw = new ToolStripMenuItem("📋 复制命令");
            miSendRaw.Click += OnCopyClick;
            ctx.Items.Add(miSendRaw);

            ContextMenuStrip = ctx;
        }

        private void RefreshGroups()
        {
            _groupPanel.Controls.Clear();
            _groupButtons.Clear();

            var groups = _commands
                .Select(c => c.Group)
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            // 添加"全部"按钮
            var allBtn = CreateGroupButton("全部");
            allBtn.Click += (s, e) => SetActiveGroup(null);
            _groupPanel.Controls.Add(allBtn);
            _groupButtons["全部"] = allBtn;

            foreach (var group in groups)
            {
                var btn = CreateGroupButton(group);
                var groupName = group;
                btn.Click += (s, e) => SetActiveGroup(groupName);
                _groupPanel.Controls.Add(btn);
                _groupButtons[groupName] = btn;
            }

            // 默认选中"全部"
            if (_activeGroup == null)
            {
                HighlightGroupButton("全部");
            }
        }

        private AntdUI.Button CreateGroupButton(string text)
        {
            var btn = new AntdUI.Button {
                Text = text,
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(1, 0, 1, 0),
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 3), DpiScale.V(this, 6), DpiScale.V(this, 3))
            };
            return btn;
        }

        private void SetActiveGroup(string group)
        {
            _activeGroup = group;
            HighlightGroupButton(group ?? "全部");
            RefreshButtons();
        }

        private void HighlightGroupButton(string name)
        {
            foreach (var kvp in _groupButtons)
            {
                if (kvp.Key == name)
                {
                    kvp.Value.ForeColor = GdtermColorTable.Success;
                    kvp.Value.Font = Services.FormFontPolicy.UiFont(-0.5f, FontStyle.Bold);
                }
                else
                {
                    kvp.Value.ForeColor = GdtermColorTable.Muted;
                    kvp.Value.Font = Services.FormFontPolicy.UiFont(-0.5f);
                }
            }
        }

        private void RefreshButtons()
        {
            _buttonPanel.Controls.Clear();

            var filtered = _activeGroup == null
                ? _commands.OrderBy(c => c.Group).ThenBy(c => c.SortOrder).ToList()
                : _commands.Where(c => c.Group == _activeGroup).OrderBy(c => c.SortOrder).ToList();

            var visible = filtered.Take(MaxVisibleCommands).ToList();
            foreach (var cmd in visible)
            {
                var btn = CreateCommandButton(cmd);
                _buttonPanel.Controls.Add(btn);
            }

            if (filtered.Count > visible.Count)
                _buttonPanel.Controls.Add(CreateMoreButton(filtered.Skip(visible.Count)));

            // 末尾的"+"按钮
            var addBtn = new AntdUI.Button {
                Text = "+",
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 7), DpiScale.V(this, 3), DpiScale.V(this, 7), DpiScale.V(this, 3)),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(2),
                TextAlign = ContentAlignment.MiddleCenter
            };
            addBtn.Click += (s, e) => AddRequested?.Invoke(_activeGroup ?? "自定义");
            _buttonPanel.Controls.Add(addBtn);
        }

        private AntdUI.Button CreateMoreButton(IEnumerable<QuickCommand> commands)
        {
            var more = new AntdUI.Button {
                Text = "...",
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 7), DpiScale.V(this, 3), DpiScale.V(this, 7), DpiScale.V(this, 3)),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(2),
                TextAlign = ContentAlignment.MiddleCenter
            };
            more.ToolTipText2("显示其余快捷命令");

            var menu = new ContextMenuStrip {
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Renderer = new DarkMenuRenderer()
            };
            foreach (var cmd in commands)
            {
                var command = cmd;
                var item = new ToolStripMenuItem(command.Name ?? "(未命名命令)");
                item.ToolTipText = command.Description ?? command.Command ?? "";
                item.Click += (s, e) => SendCommand(command, more);
                menu.Items.Add(item);
            }
            more.ContextMenuStrip = menu;
            more.Click += (s, e) => menu.Show(more, new Point(0, more.Height));
            return more;
        }

        private AntdUI.Button CreateCommandButton(QuickCommand cmd)
        {
            var btn = new AntdUI.Button {
                Text = cmd.Name,
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(2),
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3)),
                Tag = cmd
            };

            // 需要 root 的命令用橙色边框
            if (cmd.RequiresRoot)
            {
            }

            // 单击发送命令
            btn.Click += (s, e) => SendCommand(cmd, btn);

            // 右键菜单
            var ctx = new ContextMenuStrip();
            ctx.BackColor = GdtermColorTable.Surface;
            ctx.ForeColor = GdtermColorTable.Foreground;
            ctx.Renderer = new DarkMenuRenderer();

            var miEdit = new ToolStripMenuItem("编辑");
            miEdit.Click += (s, e) => EditRequested?.Invoke(cmd);
            ctx.Items.Add(miEdit);

            var miCopy = new ToolStripMenuItem("复制命令");
            miCopy.Click += (s, e) => Clipboard.SetText(ResolveCommand(cmd));
            ctx.Items.Add(miCopy);

            var miDelete = new ToolStripMenuItem("删除");
            miDelete.Click += (s, e) => RemoveCommand(cmd.Id);
            ctx.Items.Add(miDelete);

            btn.ContextMenuStrip = ctx;

            // Tooltip
            var tip = new ToolTip();
            tip.SetToolTip(btn, string.Format("{0}\n命令: {1}{2}",
                cmd.Description ?? cmd.Name,
                cmd.PreCommand != null ? cmd.PreCommand + " && " : "",
                cmd.Command));

            return btn;
        }

        private void SendCommand(QuickCommand cmd, AntdUI.Button button)
        {
            if (cmd == null || button == null) return;
            var connected = (_activeTerminal != null && _activeTerminal.Session != null && _activeTerminal.Session.IsConnected)
                || (_activeSession?.IsConnected == true);
            if (!connected)
            {
                ShowTooltip(button, "没有活动的终端会话");
                return;
            }

            var resolved = ResolveCommand(cmd);
            try
            {
                // 不直发 ITerminalSession——由 MainForm 经 TerminalControl 闸门发送
                CommandSent?.Invoke(resolved, cmd.Group);
                FlashButton(button, GdtermColorTable.Success);
            }
            catch (Exception ex)
            {
                ShowTooltip(button, "发送失败: " + ex.Message);
            }
        }

        /// <summary>解析命令占位符</summary>
        private string ResolveCommand(QuickCommand cmd)
        {
            var command = cmd.Command ?? "";

            // 占位符替换
            command = command.Replace("{host}", _hostName ?? "")
                             .Replace("{user}", _userName ?? "")
                             .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
                             .Replace("{time}", DateTime.Now.ToString("HH:mm:ss"))
                             .Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // 环境变量占位符
            if (command.Contains("{env:"))
            {
                var idx = 0;
                while ((idx = command.IndexOf("{env:", idx)) >= 0)
                {
                    var end = command.IndexOf("}", idx);
                    if (end < 0) break;
                    var varName = command.Substring(idx + 5, end - idx - 5);
                    var varValue = Environment.GetEnvironmentVariable(varName) ?? "";
                    command = command.Substring(0, idx) + varValue + command.Substring(end + 1);
                }
            }

            // 组装完整命令
            var full = command;
            if (!string.IsNullOrEmpty(cmd.PreCommand))
                full = cmd.PreCommand + " && " + full;
            if (!string.IsNullOrEmpty(cmd.PostCommand))
                full = full + " && " + cmd.PostCommand;

            return full + "\r";
        }

        // ── 视觉反馈 ──

        private void FlashButton(AntdUI.Button btn, Color flashColor)
        {
            var original = btn.BackColor;
            btn.BackColor = flashColor;
            btn.ForeColor = GdtermColorTable.OnAccent;
            var timer = new Timer { Interval = 300 };
            timer.Tick += (s, e) =>
            {
                btn.BackColor = original;
                btn.ForeColor = GdtermColorTable.Foreground;
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        }

        private void ShowTooltip(Control control, string message)
        {
            var tip = new ToolTip();
            tip.Show(message, control, 0, control.Height + 4, 2000);
        }

        /// <summary>返回两行快捷栏的字体驱动高度，避免字号增大后按钮被容器裁切。</summary>
        public int GetPreferredHeight()
        {
            int row = FormFontPolicy.RowStep(this);
            return Math.Max(DpiScale.V(this, DesignHeight), row * 2 + DpiScale.V(this, 6));
        }

        // ── 右键菜单处理 ──

        private QuickCommand GetCommandFromContext()
        {
            var menu = ContextMenuStrip ?? (ActiveControl?.ContextMenuStrip);
            if (menu == null) return null;
            // 从当前鼠标位置找到对应的按钮
            foreach (Control c in _buttonPanel.Controls)
            {
                var btn = c as Button;
                if (btn?.Tag is QuickCommand cmd && btn.RectangleToScreen(btn.ClientRectangle).Contains(Cursor.Position))
                    return cmd;
            }
            return null;
        }

        private void OnEditClick(object sender, EventArgs e)
        {
            var cmd = FindCommandAtCursor();
            if (cmd != null) EditRequested?.Invoke(cmd);
        }

        private void OnDeleteClick(object sender, EventArgs e)
        {
            var cmd = FindCommandAtCursor();
            if (cmd != null)
            {
                if (MessageBox.Show(string.Format("确定删除 \"{0}\"?", cmd.Name), "gdterm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    RemoveCommand(cmd.Id);
                }
            }
        }

        private void OnCopyClick(object sender, EventArgs e)
        {
            var cmd = FindCommandAtCursor();
            if (cmd != null) Clipboard.SetText(ResolveCommand(cmd));
        }

        private QuickCommand FindCommandAtCursor()
        {
            var point = _buttonPanel.PointToClient(Cursor.Position);
            var control = _buttonPanel.GetChildAtPoint(point);
            return (control as Button)?.Tag as QuickCommand;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buttonPanel?.Dispose();
                _groupPanel?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>深色菜单渲染器</summary>
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            e.Item.BackColor = e.Item.Selected ? GdtermColorTable.Border : GdtermColorTable.Surface;
            e.Item.ForeColor = GdtermColorTable.Foreground;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.FillRectangle(new SolidBrush(GdtermColorTable.Surface), e.AffectedBounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            e.Graphics.FillRectangle(new SolidBrush(GdtermColorTable.Border), 0, 3, e.Item.Width, 1);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = GdtermColorTable.Foreground;
            base.OnRenderItemText(e);
        }
    }
}
