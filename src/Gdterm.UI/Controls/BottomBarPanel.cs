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
    /// 单栏合并底栏（原型 v2 方案 A + B2 验收通过，对应 design/prototype/v2-optimized.html）。
    /// 把原来三行（分组 28 + 命令 36 + 状态 25 = 89px）压成一行 30px：
    ///   [分组▾(单选)] [命令横排…] │ ●隧道 ●密码库 ●AI ●安全 │ 185×32 UTF-8
    /// ── 分组单选（B2）──
    ///   下拉选某一组 → 栏内只展示该组命令；选「tmux 键组」时以 webtmux 工具栏样式
    ///   渲染（window/scroll/pane/copy/session 五组，组间 1px 分隔线），📌 可钉住常驻。
    ///   未钉住的分组选择是临时的：点击底栏外任意处回落到「全部」。
    /// ── 状态项（P3）──
    ///   原链接蓝文字 → 彩点 + 中性短词，点击路由保留（StatusClicked 事件不变）。
    /// 高度策略：单行 = max(30 设计px, RowStep)，字体驱动，大字号不裁切。
    /// </summary>
    public class BottomBarPanel : UserControl
    {
        // ── 常量（设计 px）──
        private const int DesignHeight = 30;

        // ── 状态路由（与原 StatusBarControl 相同的键）──
        public event EventHandler<string> StatusClicked;

        // ── 快捷命令事件（与原 QuickBarPanel 相同）──
        public event Action<string, string> CommandSent;   // command, groupName
        public event Action<QuickCommand> EditRequested;
        public event Action<string> AddRequested;          // groupName

        private readonly List<QuickCommand> _commands = new List<QuickCommand>();
        private ITerminalSession _activeSession;
        private TerminalControl _activeTerminal;
        private string _hostName = "";
        private string _userName = "";

        // ── UI ──
        private AntdUI.Button _groupBtn;          // [分组 ▾]
        private ContextMenuStrip _groupMenu;      // 单选下拉
        private Panel _cmdHost;                   // 命令横排宿主（横向滚动）
        private AntdUI.Button _pinBtn;            // 📌 钉住（仅 tmux 组显示）
        private ToolStripStatusLabel _connectionStatus;
        private ToolStripStatusLabel _tunnelStatus;
        private ToolStripStatusLabel _keepassStatus;
        private ToolStripStatusLabel _aiStatus;
        private ToolStripStatusLabel _securityStatus;
        // P3 彩点（语义色），与上面的中性短词配对
        private ToolStripStatusLabel _tunnelDot, _keepassDot, _aiDot, _securityDot;
        private ToolStripStatusLabel _terminalSizeLabel;
        private ToolStripStatusLabel _encodingLabel;
        private StatusStrip _statusStrip;

        // ── 分组单选状态 ──
        private string _activeGroup;     // null = 全部
        private bool _isTmux;            // 当前展示 tmux 键组
        private bool _tmuxPinned;        // tmux 组钉住
        private string _prefix = "\u0002"; // tmux 前缀 C-b
        private bool _filterInstalled;
        private BarClickAwayFilter _clickAwayFilter;

        public BottomBarPanel()
        {
            BuildUI();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // B2 回落：点击底栏外任意处 → 未钉住的分组回到「全部」。
            // 用 IMessageFilter 而非 Parent.Click（后者收不到终端区点击）。
            if (!_filterInstalled)
            {
                _clickAwayFilter = new BarClickAwayFilter(this);
                Application.AddMessageFilter(_clickAwayFilter);
                _filterInstalled = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_clickAwayFilter != null)
                {
                    try { Application.RemoveMessageFilter(_clickAwayFilter); } catch { }
                    _clickAwayFilter = null;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>鼠标点击是否落在本底栏内（Filter 判定用）。</summary>
        internal bool ContainsScreenPoint(Point screenPt)
        {
            try { return RectangleToScreen(ClientRectangle).Contains(screenPt); }
            catch { return false; }
        }

        /// <summary>底栏外点击：未钉住的分组回落到「全部」。</summary>
        internal void NotifyClickAway()
        {
            if (IsDisposed) return;
            if (_isTmux && !_tmuxPinned) SelectGroup(null);
            else if (!_isTmux && _activeGroup != null) SelectGroup(null);
        }

        // ═══════════ 公共 API（对齐原 QuickBarPanel / StatusBarControl）═══════════

        /// <summary>绑定活动终端控件（优先，发送走 TerminalControl 危险命令闸门）。</summary>
        public void SetActiveTerminal(TerminalControl terminal, string host = null, string user = null)
        {
            _activeTerminal = terminal;
            _activeSession = terminal != null ? terminal.Session : null;
            if (host != null) _hostName = host;
            if (user != null) _userName = user;
        }

        public void SetActiveSession(ITerminalSession session, string host = null, string user = null)
        {
            _activeTerminal = null;
            _activeSession = session;
            if (host != null) _hostName = host;
            if (user != null) _userName = user;
        }

        public void SetCommands(List<QuickCommand> commands)
        {
            _commands.Clear();
            if (commands != null) _commands.AddRange(commands);
            RefreshGroupMenu();
            RefreshCommands();
        }

        public void UpdateTerminalInfo(int columns, int rows, string encoding)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, int, string>(UpdateTerminalInfo), columns, rows, encoding); return; }
            try
            {
                _terminalSizeLabel.Text = columns > 0 && rows > 0 ? (columns + "×" + rows) : "";
                _encodingLabel.Text = string.IsNullOrEmpty(encoding) ? "" : encoding;
            }
            catch { }
        }

        public void UpdateSecurityStatus(bool locked)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(UpdateSecurityStatus), locked); return; }
            _securityDot.ForeColor = locked ? GdtermColorTable.Muted : GdtermColorTable.Success;
            SetStatusTip(_securityStatus, locked ? "安全: 已锁定（点击修改主密码）" : "安全: 已解锁（点击修改主密码）");
        }

        public void UpdateKeePassStatus(bool unlocked)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(UpdateKeePassStatus), unlocked); return; }
            _keepassDot.ForeColor = unlocked ? GdtermColorTable.Success : GdtermColorTable.Muted;
            SetStatusTip(_keepassStatus, unlocked ? "密码库: 已解锁" : "密码库: 锁定");
        }

        /// <summary>Alt+8 / 状态栏 ⚡：tmux 键组与全部之间快速切换。</summary>
        public void ToggleTmuxGroup()
        {
            if (_isTmux) { SelectGroup(null); }
            else SelectGroup("__tmux__");
        }

        /// <summary>字体/行高变化后重算高度（由 MainForm.ApplyGlobalUIFont 调用）。</summary>
        public int GetPreferredHeight()
        {
            return Math.Max(DpiScale.V(this, DesignHeight), FormFontPolicy.RowStep(this));
        }

        // ═══════════ UI 构建 ═══════════

        private void BuildUI()
        {
            Dock = DockStyle.Bottom;
            BackColor = GdtermColorTable.Background;
            Height = GetPreferredHeight();

            // 左：分组下拉按钮
            _groupBtn = new AntdUI.Button
            {
                Text = "全部 ▾",
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                Font = FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(DpiScale.V(this, 6), 0, 0, 0),
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 2), DpiScale.V(this, 8), DpiScale.V(this, 2)),
                TabStop = false
            };
            _groupBtn.Click += (s, e) => { RefreshGroupMenu(); _groupMenu.Show(_groupBtn, new Point(0, _groupBtn.Height)); };

            _groupMenu = new ContextMenuStrip
            {
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                ShowCheckMargin = true,
                ShowImageMargin = false
            };

            // 中：命令横排（横向滚动，隐藏滚动条）
            _cmdHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                AutoScroll = false,
                Padding = new Padding(0)
            };

            // 右：状态栏（彩点+短词，压缩化）
            _statusStrip = BuildStatusStrip();

            // 📌 钉住按钮（仅 tmux 组显示）
            _pinBtn = new AntdUI.Button
            {
                Text = "📌",
                AutoSize = true,
                Visible = false,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Muted,
                // 📌 是 emoji，雅黑无此字形 → 必须走 Segoe UI Emoji（项目既有例外，同 KeePassUnlockForm）
                Font = new Font("Segoe UI Emoji", FormFontPolicy.UiFont().SizeInPoints - 0.5f),
                Cursor = Cursors.Hand,
                Margin = new Padding(DpiScale.V(this, 4), 0, DpiScale.V(this, 4), 0),
                TabStop = false
            };
            _pinBtn.Click += (s, e) => { _tmuxPinned = !_tmuxPinned; RefreshCommands(); };

            // 布局：Dock 顺序 Fill 必须最先 Add（z-order 语义）
            Controls.Add(_cmdHost);
            Controls.Add(_statusStrip);
            Controls.Add(_pinBtn);
            Controls.Add(_groupBtn);

            _statusStrip.Dock = DockStyle.Right;
            _statusStrip.AutoSize = true;
            _pinBtn.Dock = DockStyle.Right;
            _groupBtn.Dock = DockStyle.Left;
            // 命令宿主上下各留 2px（DPI），按钮行不贴栏底——修"最后一行太靠下"
            _cmdHost.Padding = new Padding(0, DpiScale.V(this, 2), 0, DpiScale.V(this, 2));

            RefreshCommands();
        }

        private StatusStrip BuildStatusStrip()
        {
            var strip = new StatusStrip
            {
                RenderMode = ToolStripRenderMode.ManagerRenderMode,
                SizingGrip = false,
                AutoSize = true,
                BackColor = GdtermColorTable.Background
            };

            _connectionStatus = new ToolStripStatusLabel("就绪") { ForeColor = GdtermColorTable.Muted, AutoSize = true, Spring = false };
            _terminalSizeLabel = new ToolStripStatusLabel("80×24") { ForeColor = GdtermColorTable.Muted, AutoSize = true };
            _encodingLabel = new ToolStripStatusLabel("UTF-8") { ForeColor = GdtermColorTable.Muted, AutoSize = true };

            strip.Items.Add(_connectionStatus);
            strip.Items.Add(new ToolStripSeparator());
            // P3：彩点(●语义色) + 中性短词。旧实现用 ⚡🔑✦🔒 emoji，雅黑无字形→豆腐块，且丢了短词。
            _tunnelStatus   = MakeStatusPair(strip, out _tunnelDot,   "隧道",   GdtermColorTable.Muted,   "隧道: 无（点击打开端口转发面板）", "tunnel");
            _keepassStatus  = MakeStatusPair(strip, out _keepassDot,  "密码库", GdtermColorTable.Muted,   "密码库: 锁定（点击打开密码库管理）", "keepass");
            _aiStatus       = MakeStatusPair(strip, out _aiDot,       "AI",     GdtermColorTable.Success, "AI: 就绪（点击打开 AI 助手设置）", "ai");
            _securityStatus = MakeStatusPair(strip, out _securityDot, "安全",   GdtermColorTable.Muted,   "安全: 已锁定（点击修改主密码）", "security");
            strip.Items.Add(_terminalSizeLabel);
            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(_encodingLabel);
            return strip;
        }

        /// <summary>P3 状态项 = 彩点(●，语义色，纯指示) + 中性短词(可点链接)，两段各自路由到同一 key。</summary>
        private ToolStripStatusLabel MakeStatusPair(StatusStrip strip, out ToolStripStatusLabel dot,
            string word, Color dotColor, string tip, string key)
        {
            dot = new ToolStripStatusLabel("●")
            {
                ForeColor = dotColor,
                AutoSize = true,
                ToolTipText = tip,
                Font = FormFontPolicy.UiFont(-2f),
                Margin = new Padding(DpiScale.V(this, 8), 0, 0, 0)
            };
            dot.Click += (s, e) => StatusClicked?.Invoke(this, key);
            var text = new ToolStripStatusLabel(word)
            {
                ForeColor = GdtermColorTable.Muted,
                AutoSize = true,
                ToolTipText = tip,
                // ToolStripItem 无 Cursor；用 link 语义（悬停下划线）表达可点，与旧 StatusBarControl 一致
                IsLink = true,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Margin = new Padding(DpiScale.V(this, 2), 0, 0, 0)
            };
            text.Click += (s, e) => StatusClicked?.Invoke(this, key);
            text.MouseEnter += (s, e) => text.ForeColor = GdtermColorTable.Foreground;
            text.MouseLeave += (s, e) => text.ForeColor = GdtermColorTable.Muted;
            strip.Items.Add(dot);
            strip.Items.Add(text);
            return text;
        }

        private static void SetStatusTip(ToolStripStatusLabel label, string tip)
        {
            try { label.ToolTipText = tip; } catch { }
        }

        // ═══════════ 分组单选（B2）═══════════

        private void RefreshGroupMenu()
        {
            _groupMenu.Items.Clear();

            var groups = _commands
                .Select(c => c.Group)
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            AddGroupMenuItem("全部", null);
            foreach (var g in groups) AddGroupMenuItem(g, g);
            _groupMenu.Items.Add(new ToolStripSeparator());
            AddGroupMenuItem("tmux 键组", "__tmux__");

            // 当前项勾选态
            string currentKey = _isTmux ? "__tmux__" : (_activeGroup ?? "");
            foreach (ToolStripMenuItem item in _groupMenu.Items.OfType<ToolStripMenuItem>())
                item.Checked = (item.Tag as string) == currentKey;
        }

        private void AddGroupMenuItem(string text, string key)
        {
            var item = new ToolStripMenuItem(text) { Tag = key };
            item.Click += (s, e) => SelectGroup(key);
            _groupMenu.Items.Add(item);
        }

        /// <summary>选组：null=全部；"__tmux__"=tmux 键组；其他=对应命令分组。</summary>
        private void SelectGroup(string key)
        {
            if (key == "__tmux__")
            {
                _isTmux = true;
                _activeGroup = null;
            }
            else
            {
                _isTmux = false;
                _activeGroup = key;
                if (key != null) _tmuxPinned = false; // 离开 tmux 组时取消钉住
            }
            RefreshGroupMenu();
            RefreshCommands();
        }

        // ═══════════ 命令区渲染 ═══════════

        private void RefreshCommands()
        {
            _cmdHost.Controls.Clear();
            int dpi = DpiScale.V(this, 1);
            int x = DpiScale.V(this, 6);
            int avail = Math.Max(0, _cmdHost.ClientSize.Width - DpiScale.V(this, 8));

            if (_isTmux)
            {
                // 钉住态直接写在分组钮上，用户看得见 pin 生效（修"点击就恢复"的误解）
                _groupBtn.Text = _tmuxPinned ? "tmux 📌 ▾" : "tmux ▾";
                _groupBtn.ForeColor = GdtermColorTable.Accent;
                _pinBtn.Visible = true;
                _pinBtn.ForeColor = _tmuxPinned ? GdtermColorTable.Accent : GdtermColorTable.Muted;
                _pinBtn.ToolTipText2(_tmuxPinned ? "已钉住：tmux 键组常驻" : "钉住：tmux 键组常驻展示");
                x = BuildTmuxKeys(x, dpi, avail);
            }
            else
            {
                _groupBtn.Text = (_activeGroup ?? "全部") + " ▾";
                _groupBtn.ForeColor = _activeGroup == null ? GdtermColorTable.Muted : GdtermColorTable.Accent;
                _pinBtn.Visible = false;

                var filtered = _activeGroup == null
                    ? _commands.OrderBy(c => c.Group).ThenBy(c => c.SortOrder).ToList()
                    : _commands.Where(c => c.Group == _activeGroup).OrderBy(c => c.SortOrder).ToList();

                foreach (var cmd in filtered)
                {
                    var btn = CreateCommandButton(cmd);
                    int need = x + FittedWidth(btn) + DpiScale.V(this, 8);
                    if (need > avail && filtered.Count > 1)
                    {
                        // 溢出：收进「…」菜单（对齐原 QuickBarPanel 模式），不裁剪
                        x = PlaceMoreButton(x, dpi, filtered.SkipWhile(c => !ReferenceEquals(c, cmd)).ToList());
                        break;
                    }
                    x = PlaceControl(btn, x, dpi);
                }

                // 「+」添加按钮
                var add = new AntdUI.Button
                {
                    Text = "+",
                    AutoSize = true,
                    BackColor = GdtermColorTable.Background,
                    ForeColor = GdtermColorTable.Muted,
                    Font = FormFontPolicy.UiFont(-0.5f),
                    Cursor = Cursors.Hand,
                    TabStop = false,
                    Padding = new Padding(DpiScale.V(this, 7), DpiScale.V(this, 2), DpiScale.V(this, 7), DpiScale.V(this, 2))
                };
                add.Click += (s, e) => AddRequested?.Invoke(_activeGroup ?? "自定义");
                add.ToolTipText2("添加快捷命令");
                x = PlaceControl(add, x, dpi);
            }

            // 分组按钮：同样绕开 AntdUI AutoSize（它不看 Padding），显式按实测文字宽预留
            _groupBtn.AutoSize = false;
            int gw = FittedWidth(_groupBtn);
            _groupBtn.MinimumSize = new Size(gw, 0);
            _groupBtn.Width = gw;
        }

        /// <summary>溢出命令收进「…」弹出菜单（不裁剪不滚动）。</summary>
        private int PlaceMoreButton(int x, int dpi, List<QuickCommand> rest)
        {
            var more = new AntdUI.Button
            {
                Text = "…",
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                Font = FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = new Padding(DpiScale.V(this, 7), DpiScale.V(this, 2), DpiScale.V(this, 7), DpiScale.V(this, 2))
            };
            more.ToolTipText2("显示其余快捷命令");
            var menu = new ContextMenuStrip
            {
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground
            };
            foreach (var cmd in rest)
            {
                var c = cmd;
                var item = new ToolStripMenuItem(c.Name ?? "(未命名命令)") { ToolTipText = c.Description ?? c.Command ?? "" };
                item.Click += (s, e) => SendCommand(c, more);
                menu.Items.Add(item);
            }
            more.ContextMenuStrip = menu;
            more.Click += (s, e) => menu.Show(more, new Point(0, more.Height));
            return PlaceControl(more, x, dpi);
        }

        private int PlaceControl(Control c, int x, int dpi, int innerPx = 6)
        {
            _cmdHost.Controls.Add(c);
            // AntdUI.Button 的 AutoSize(PSize)=文字宽+固定gap(文字高×1.02)，不看 Padding；
            // 且一旦关 AutoSize，PreferredSize 退化为 base(≈MinimumSize)，拿不到文字度量。
            // → 完全自算宽高，显式 Size + 锁 MinimumSize，绕开 AntdUI 一切自动度量。
            // 宽预算见 FittedWidth：Padding.Horizontal + sps双侧 + wave边框 + GDI差 + innerPx×2。
            int w = FittedWidth(c.Text, c.Font, innerPx, c.Padding);
            // y 按宿主居中：_cmdHost 是 Fill 区，行高=栏高-两端留白，不再用整栏 Height 贴底算
            int hostH = _cmdHost.ClientSize.Height > 0 ? _cmdHost.ClientSize.Height : Height;
            int h = Math.Max(FittedHeight(c.Text, c.Font), DpiScale.V(this, 22));
            c.AutoSize = false;
            c.MinimumSize = new Size(w, h);
            c.Size = new Size(w, h);
            c.Location = new Point(x, Math.Max(0, (hostH - c.Height) / 2));
            return x + w + DpiScale.V(this, 6);
        }

        /// <summary>
        /// 实测文字宽 + 库内扣减全预算 + 1px 边框×2，保证文字不贴边不换行。
        /// AntdUI Button 绘制链（Button.cs OnDraw → PaintTextLoading）：
        ///   文字区 = Width − Padding.Horizontal − ReadRect(wave+边框≈5px) − sps双侧(文字高×0.8≈10px)。
        ///   另 GDI(TextRenderer) vs GDI+(AntdUI) 量尺差约 3px。
        /// 旧公式 textW + innerPx×2 + 2 在 innerPx=10/Padding(4) 下余量 20px，
        /// 库内吃掉 8+10+5+3=26px → 负预算 → 窄键(◀Win/改名,/o切换/:命令)换行/遮住。
        /// 新公式：余量 = Padding.Horizontal(是什么给什么) + sps双侧(按真实字高算) + wave边框5 + GDI差4。
        /// </summary>
        private int FittedWidth(string text, Font font, int innerPx, Padding pad)
        {
            int textW = string.IsNullOrEmpty(text) ? DpiScale.V(this, 8)
                : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int textH = TextRenderer.MeasureText(string.IsNullOrEmpty(text) ? " " : text, font).Height;
            int spsBoth = (int)Math.Ceiling(textH * 0.8);          // 库内 sps 双侧
            const int waveBorder = 5;                              // ReadRect: WaveSize(4)+边框(1)
            const int gdiSlop = 4;                                 // GDI vs GDI+ 量尺差
            return textW + pad.Horizontal + spsBoth + DpiScale.V(this, waveBorder + gdiSlop)
                + DpiScale.V(this, innerPx * 2) + 2;
        }
        private int FittedWidth(Control c) { return FittedWidth(c.Text, c.Font, 10, c.Padding); }

        private int FittedHeight(string text, Font font)
        {
            return TextRenderer.MeasureText(string.IsNullOrEmpty(text) ? " " : text, font).Height
                + DpiScale.V(this, 8);
        }
        private int FittedHeight(Control c) { return FittedHeight(c.Text, c.Font); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 宽度变化 → 溢出判定重算；高度变化 → 按钮重垂直居中
            if (_cmdHost != null && !_cmdHost.IsDisposed) RefreshCommands();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_cmdHost != null && !_cmdHost.IsDisposed) RefreshCommands();
        }

        private AntdUI.Button CreateCommandButton(QuickCommand cmd)
        {
            var btn = new AntdUI.Button
            {
                Text = cmd.Name,
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 2), DpiScale.V(this, 8), DpiScale.V(this, 2)),
                Tag = cmd
            };
            btn.Click += (s, e) => SendCommand(cmd, btn);

            var ctx = new ContextMenuStrip
            {
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground
            };
            var miEdit = new ToolStripMenuItem("编辑"); miEdit.Click += (s, e) => EditRequested?.Invoke(cmd);
            var miCopy = new ToolStripMenuItem("复制命令"); miCopy.Click += (s, e) => { try { Clipboard.SetText(ResolveCommand(cmd)); } catch { } };
            var miDel = new ToolStripMenuItem("删除"); miDel.Click += (s, e) =>
            {
                // 先落盘再刷内存：有注入走 store.Delete（失败则不动），无注入仅内存删
                if (DeletePersisted != null)
                {
                    bool ok = false;
                    try { ok = DeletePersisted(cmd.Id); } catch { }
                    if (ok) RemoveCommand(cmd.Id);
                }
                else RemoveCommand(cmd.Id);
            };
            ctx.Items.Add(miEdit); ctx.Items.Add(miCopy); ctx.Items.Add(miDel);
            btn.ContextMenuStrip = ctx;

            btn.ToolTipText2(string.Format("{0}\n命令: {1}{2}",
                cmd.Description ?? cmd.Name,
                cmd.PreCommand != null ? cmd.PreCommand + " && " : "",
                cmd.Command));
            return btn;
        }

        /// <summary>
        /// tmux 键组——对齐 webtmux TOOLBAR_GROUPS（window/scroll/pane/copy/session），
        /// 组间 1px 分隔线。Raw 非空时直发（PgUp/PgDn），否则 prefix+key。
        /// </summary>
        private sealed class TmuxKey
        {
            public string Label;
            public string Key;    // prefix 后的键；null = Raw 直发
            public string Raw;
            public string Tip;
        }

        private static readonly object TmuxSep = new object(); // 组分隔哨兵

        private int BuildTmuxKeys(int x, int dpi, int avail)
        {
            string pfx = _prefix == "\u0001" ? "C-a" : "C-b";
            var keyFont = FormFontPolicy.UiFont(-0.5f);

            // 前缀选择器：tmux(C-b) 与 screen(C-a) 都兼容，点选切换 _prefix
            x = PlaceControl(CreatePrefixButton("C-b", "\u0002", "tmux 默认前缀 C-b"), x, dpi);
            x = PlaceControl(CreatePrefixButton("C-a", "\u0001", "screen 默认前缀 C-a"), x, dpi);
            x = PlaceTmuxSeparator(x);

            var groups = new[]
            {
                // window：前后窗 + 重命名 + 杀窗口（PC 高频，,& 不用翻菜单找）
                new[] { new TmuxKey{Label="◀Win",Key="p",Tip="prev window"}, new TmuxKey{Label="Win▶",Key="n",Tip="next window"}, new TmuxKey{Label="改名,",Key=",",Tip="rename window"}, new TmuxKey{Label="杀窗&",Key="&",Tip="kill window"} },
                new[] { new TmuxKey{Label="▲Buf",Key="[",Tip="copy mode ↑"}, new TmuxKey{Label="PgUp",Raw="\u001b[5~",Tip="page up"}, new TmuxKey{Label="PgDn",Raw="\u001b[6~",Tip="page down"} },
                // pane：o 轮切是 PC 最高频，切 pane 不翻菜单
                new[] { new TmuxKey{Label="|Pane",Key="%",Tip="split vertical"}, new TmuxKey{Label="—Pane",Key="\"",Tip="split horizontal"}, new TmuxKey{Label="o切换",Key="o",Tip="next pane"}, new TmuxKey{Label="Zoom",Key="z",Tip="zoom pane"}, new TmuxKey{Label="Kill○",Key="x",Tip="kill pane"} },
                new[] { new TmuxKey{Label="Copy",Key="[",Tip="copy mode"}, new TmuxKey{Label="Paste",Key="]",Tip="paste buffer"} },
                // session：s 会话列表 / : 命令模式 / t 时钟是 PC 高频
                new[] { new TmuxKey{Label="NewWin",Key="c",Tip="new window"}, new TmuxKey{Label="List",Key="w",Tip="list windows"}, new TmuxKey{Label="s会话",Key="s",Tip="list sessions"}, new TmuxKey{Label=":命令",Key=":",Tip="command prompt"}, new TmuxKey{Label="Detach",Key="d",Tip="detach"} }
            };

            // 展平为「键 / 组分隔」序列：放不下时把剩余整段搬进「…」菜单，绝不静默丢键
            var slots = new List<object>();
            for (int gi = 0; gi < groups.Length; gi++)
            {
                if (gi > 0) slots.Add(TmuxSep);
                foreach (var k in groups[gi]) slots.Add(k);
            }

            int placed = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] is TmuxKey)
                {
                    var key = (TmuxKey)slots[i];
                    if (x + FittedWidth(key.Label, keyFont, 6, TmuxKeyPadding()) > avail) break; // 从此键起溢出
                    x = PlaceControl(MakeTmuxKeyButton(key, pfx, keyFont), x, dpi, 6);
                }
                else
                {
                    x = PlaceTmuxSeparator(x);
                }
                placed = i + 1;
            }
            if (placed < slots.Count)
                x = PlaceTmuxMoreButton(x, dpi, slots.GetRange(placed, slots.Count - placed), pfx, keyFont);
            return x;
        }

        private int PlaceTmuxSeparator(int x)
        {
            var sep = new Panel { Width = 1, BackColor = GdtermColorTable.Border, Margin = new Padding(0), TabStop = false };
            _cmdHost.Controls.Add(sep);
            sep.Location = new Point(x, DpiScale.V(this, 6));
            sep.Height = Math.Max(4, Height - DpiScale.V(this, 12));
            return x + 1 + DpiScale.V(this, 6);
        }

        /// <summary>tmux 键统一 Padding（放置与溢出判定同源）。</summary>
        private Padding TmuxKeyPadding()
        {
            return new Padding(DpiScale.V(this, 6), DpiScale.V(this, 2), DpiScale.V(this, 6), DpiScale.V(this, 2));
        }

        private AntdUI.Button MakeTmuxKeyButton(TmuxKey k, string pfx, Font font)
        {
            var btn = new AntdUI.Button
            {
                Text = k.Label,
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = font,
                Cursor = Cursors.Hand,
                TabStop = false,
                // Padding(6)：与 FittedWidth 预算同源，窄键(◀Win/改名,/o切换/:命令)不换行
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 2), DpiScale.V(this, 6), DpiScale.V(this, 2))
            };
            btn.Click += (s, e) => SendTmuxKey(k.Raw ?? (_prefix + k.Key));
            btn.ToolTipText2("tmux/screen · " + k.Tip + (k.Raw == null ? " (" + pfx + "+" + k.Key + ")" : ""));
            return btn;
        }

        private int PlaceTmuxMoreButton(int x, int dpi, List<object> rest, string pfx, Font font)
        {
            var more = new AntdUI.Button
            {
                Text = "…",
                AutoSize = true,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                Font = font,
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = new Padding(DpiScale.V(this, 6), DpiScale.V(this, 2), DpiScale.V(this, 6), DpiScale.V(this, 2))
            };
            more.ToolTipText2("更多 tmux/screen 键");
            var menu = new ContextMenuStrip { BackColor = GdtermColorTable.Surface2, ForeColor = GdtermColorTable.Foreground };
            foreach (var s in rest)
            {
                if (s == TmuxSep) { menu.Items.Add(new ToolStripSeparator()); continue; }
                var kk = (TmuxKey)s;
                var item = new ToolStripMenuItem(kk.Label + (kk.Raw == null ? "  (" + pfx + "+" + kk.Key + ")" : "")) { ToolTipText = kk.Tip };
                item.Click += (sender, e) => SendTmuxKey(kk.Raw ?? (_prefix + kk.Key));
                menu.Items.Add(item);
            }
            more.ContextMenuStrip = menu;
            more.Click += (s, e) => menu.Show(more, new Point(0, more.Height));
            return PlaceControl(more, x, dpi);
        }

        /// <summary>前缀切换按钮（C-b/C-a）。激活态：Surface2 底 + Accent 粗体字；关态 Muted。宽度走 PlaceControl 实测。</summary>
        private AntdUI.Button CreatePrefixButton(string label, string val, string tip)
        {
            bool active = _prefix == val;
            var b = new AntdUI.Button
            {
                Text = label,
                AutoSize = true,
                BackColor = active ? GdtermColorTable.Surface2 : GdtermColorTable.Surface,
                ForeColor = active ? GdtermColorTable.Accent : GdtermColorTable.Muted,
                Font = active ? FormFontPolicy.UiFont(-0.5f, FontStyle.Bold) : FormFontPolicy.UiFont(-0.5f),
                Cursor = Cursors.Hand,
                TabStop = false,
                Padding = new Padding(DpiScale.V(this, 4), DpiScale.V(this, 2), DpiScale.V(this, 4), DpiScale.V(this, 2))
            };
            b.Click += (s, e) => { if (_prefix != val) { _prefix = val; RefreshCommands(); } };
            b.ToolTipText2(tip);
            return b;
        }

        private void SendTmuxKey(string payload)
        {
            // tmux 控制序列不是 shell 命令行，走 TrySendInput 绕过危险命令闸门
            try { _activeTerminal?.TrySendInput(payload); } catch { }
            if (_activeTerminal == null)
            {
                try { _activeSession?.SendInput(payload); } catch { }
            }
        }

        // ═══════════ 命令发送（原 QuickBarPanel 逻辑）═══════════

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
            try
            {
                CommandSent?.Invoke(ResolveCommand(cmd), cmd.Group);
                FlashButton(button, GdtermColorTable.Success);
            }
            catch (Exception ex)
            {
                ShowTooltip(button, "发送失败: " + ex.Message);
            }
        }

        private string ResolveCommand(QuickCommand cmd)
        {
            var command = cmd.Command ?? "";
            command = command.Replace("{host}", _hostName ?? "")
                             .Replace("{user}", _userName ?? "")
                             .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
                             .Replace("{time}", DateTime.Now.ToString("HH:mm:ss"))
                             .Replace("{datetime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
            var full = command;
            if (!string.IsNullOrEmpty(cmd.PreCommand)) full = cmd.PreCommand + " && " + full;
            if (!string.IsNullOrEmpty(cmd.PostCommand)) full = full + " && " + cmd.PostCommand;
            return full + "\r";
        }

        public void RemoveCommand(string commandId)
        {
            _commands.RemoveAll(c => c.Id == commandId);
            RefreshGroupMenu();
            RefreshCommands();
        }

        /// <summary>右键删除 → 同步落盘（由 MainForm 注入，无注入时仅内存删）。</summary>
        public Func<string, bool> DeletePersisted { get; set; }

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
    }

    /// <summary>
    /// B2 回落过滤器：点击底栏外任意处（含终端区/菜单/其他控件）触发 NotifyClickAway。
    /// 只监听鼠标左键按下消息；Filter 生命周期跟随控件句柄（弱引用防泄漏）。
    /// </summary>
    internal sealed class BarClickAwayFilter : IMessageFilter
    {
        private readonly System.WeakReference _bar;
        public BarClickAwayFilter(BottomBarPanel bar) { _bar = new System.WeakReference(bar); }

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x0201, WM_NCLBUTTONDOWN = 0x00A1, WM_RBUTTONDOWN = 0x0204;
            if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_NCLBUTTONDOWN && m.Msg != WM_RBUTTONDOWN) return false;

            var bar = _bar.Target as BottomBarPanel;
            if (bar == null || bar.IsDisposed) return false;

            try
            {
                var pos = Cursor.Position;
                if (!bar.ContainsScreenPoint(pos)) bar.NotifyClickAway();
            }
            catch { }
            return false; // 不吞消息，仅观察
        }
    }
}
