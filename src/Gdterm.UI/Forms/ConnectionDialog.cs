using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 新建/编辑连接对话框——支持 SSH、RDP、Serial 三种协议。
    ///
    /// 设计原则「简洁优先」：默认只显示必填的 6 行（名称/协议/主机/端口/用户名/分组）
    /// 加一行凭据；协议专属选项（SSH 隧道 / RDP 选项 / 串口参数）和备注收进
    /// 「更多选项」折叠区，点开才出现。编辑已有连接且配置过高级选项时自动展开。
    /// </summary>
    public class ConnectionDialog : Form
    {
        private readonly ConnectionConfig _config;
        private readonly bool _isNew;

        // 基本信息
        private TextBox _nameBox;
        private ComboBox _protocolCombo;
        private TextBox _hostBox;
        private NumericUpDown _portBox;
        private TextBox _usernameBox;
        private TextBox _groupPathBox;

        // SSH 高级
        private CheckBox _tunnelCheck;
        private TextBox _tunnelHostBox;
        private NumericUpDown _tunnelPortBox;
        private TextBox _tunnelUserBox;

        // RDP
        private TextBox _domainBox;
        private CheckBox _rdpDriveCheck;
        private CheckBox _rdpClipboardCheck;
        private CheckBox _rdpPrinterCheck;
        private NumericUpDown _rdpColorDepth;
        private CheckBox _rdpFullScreenCheck;
        private CheckBox _rdpNlaCheck;
        /// <summary>强制 NLA（FreeRDP 下加 /sec:nla，禁止降级 legacy security）</summary>
        private CheckBox _rdpForceNlaCheck;
        /// <summary>负载均衡路由 token（FreeRDP 下加 /load-balance-info）</summary>
        private TextBox _rdpLoadBalanceBox;
        /// <summary>RDP 渲染引擎：0=自动（优先 FreeRDP） 1=FreeRDP 2=系统 mstsc（ActiveX）。旧堡垒机对 mstsc 兼容性最好。</summary>
        private ComboBox _rdpEngineCombo;

        // Serial
        private ComboBox _serialPortCombo;
        private ComboBox _serialBaudCombo;
        private ComboBox _serialDataBitsCombo;
        private ComboBox _serialStopBitsCombo;
        private ComboBox _serialParityCombo;

        // KeePass
        private TextBox _credentialRefBox;
        private Label _credentialTitleLabel;

        // 布局
        private Panel _advancedHost;
        private FlowLayoutPanel _advFlow;
        private TableLayoutPanel _secSsh;
        private TableLayoutPanel _secRdp;
        private TableLayoutPanel _secSerial;
        private TextBox _notesBox;
        private LinkLabel _moreLink;
        private bool _expanded;
        private int _expandedDelta;   // 本次展开实际增加的高度（收起时原样减回，兼容工作区封顶）
        private Panel _btnPanel;      // 日志用
        private Button _okBtn;

        private readonly IKeePassService _keepass;

        public ConnectionConfig Result { get; private set; }

        public ConnectionDialog(ConnectionConfig existing = null, IKeePassService keepass = null)
        {
            _config = existing ?? new ConnectionConfig();
            _keepass = keepass;
            _isNew = existing == null;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadFromConfig();
            DiagLog.Info("ConnDialog", "ctor isNew=" + _isNew
                + " ClientSize=" + ClientSize.Width + "x" + ClientSize.Height
                + " font=" + Font.Name + "/" + Font.Size.ToString("0.#") + "pt"
                + " screen=" + Screen.FromControl(this).Bounds.Width + "x" + Screen.FromControl(this).Bounds.Height
                + " expanded=" + _expanded);
        }

        /// <summary>
        /// 新建连接，预填分组路径（供右键分组节点 “新建连接到本分组” 使用）。
        /// 永远作为新建模式，不进入编辑分支。
        /// </summary>
        public ConnectionDialog(string defaultGroupPath, IKeePassService keepass = null)
        {
            _config = new ConnectionConfig { GroupPath = defaultGroupPath ?? string.Empty };
            _keepass = keepass;
            _isNew = true;
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadFromConfig();
        }

        private void InitializeComponent()
        {
            Text = _isNew ? "新建连接" : $"编辑连接 — {_config.Name}";
            ClientSize = DpiScale.S(this, 560, 330);
            // 跟随字体/DPI 自动整体缩放（否则 144dpi 下控件行高变大而窗体不变，底部按钮被挤出可视区）
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(30, 30, 30);
            Font = Services.FormFontPolicy.UiFont();

            // ===== 顶部：基本信息 + 凭据 + 更多选项开关 =====
            var topPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(12, 10, 12, 4) };

            var basicLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true
            };
            basicLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            basicLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _nameBox = AddRow(basicLayout, 0, "名称", new TextBox());
            WinFormsCompat.SetCueBanner(_nameBox, "可选，留空则用 主机:端口");
            _protocolCombo = AddRow(basicLayout, 1, "协议", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _protocolCombo.Items.AddRange(new object[] { "SSH", "RDP", "Serial" });
            _protocolCombo.SelectedIndexChanged += OnProtocolChanged;
            _hostBox = AddRow(basicLayout, 2, "主机", new TextBox());
            WinFormsCompat.SetCueBanner(_hostBox, "IP 或主机名，如 192.168.1.10");
            _portBox = AddRow(basicLayout, 3, "端口", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 });
            _usernameBox = AddRow(basicLayout, 4, "用户名", new TextBox());
            _groupPathBox = AddRow(basicLayout, 5, "分组", new TextBox());
            WinFormsCompat.SetCueBanner(_groupPathBox, "如: Web/生产");
            topPanel.Controls.Add(basicLayout);

            // ===== 凭据行（原独立标签页收为一行）=====
            var credRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            credRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            credRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            credRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // 隐藏的 UUID 存储框（不入布局，仅作保存时读取的存储）
            _credentialRefBox = new TextBox { Visible = false, Enabled = false };
            credRow.Controls.Add(new Label
            {
                Text = "凭据",
                ForeColor = Color.FromArgb(204, 204, 204),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 8, 0),
                Height = 30
            }, 0, 0);
            _credentialTitleLabel = new Label
            {
                Text = "未选（按主机+用户名自动匹配）",
                ForeColor = Color.FromArgb(100, 100, 100),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 30
            };
            credRow.Controls.Add(_credentialTitleLabel, 1, 0);
            var credBtns = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
            var btnPickCred = new Button
            {
                Text = "选择凭据...",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                // 随全局字体/DPI 自动适配尺寸：固定 Size 在 11pt@144dpi 下文字撑满按钮显得过大
                AutoSize = true,
                Padding = new Padding(2, 2, 2, 2),
                Margin = new Padding(0, 2, 6, 0)
            };
            btnPickCred.Click += OnPickCredential;
            var btnClearCred = new Button
            {
                Text = "清除",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                AutoSize = true,
                Padding = new Padding(4, 2, 4, 2),
                Margin = new Padding(0, 2, 0, 0)
            };
            btnClearCred.Click += OnClearCredential;
            credBtns.Controls.Add(btnPickCred);
            credBtns.Controls.Add(btnClearCred);
            credRow.Controls.Add(credBtns, 2, 0);
            topPanel.Controls.Add(credRow);

            // ===== 更多选项 开关 =====
            var linkRow = new Panel { Dock = DockStyle.Top, AutoSize = true, Height = 28, Padding = new Padding(0, 6, 0, 0) };
            _moreLink = new LinkLabel
            {
                Text = "更多选项 ▾",
                AutoSize = true,
                Location = DpiScale.P(this, 0, 6),
                LinkColor = Color.FromArgb(120, 180, 255),
                ActiveLinkColor = Color.White
            };
            _moreLink.LinkBehavior = LinkBehavior.HoverUnderline;
            _moreLink.Click += (s, e) => ToggleAdvanced();
            linkRow.Controls.Add(_moreLink);
            topPanel.Controls.Add(linkRow);

            // ===== 高级区：协议专属选项 + 备注（默认折叠）=====
            // AutoScroll：内容超过剩余空间时出滚动条，保证保存/取消按钮永远可见
            _advancedHost = new Panel { Dock = DockStyle.Fill, Visible = false, AutoScroll = true, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(12, 4, 12, 4) };
            _advFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // --- SSH 区 ---
            _secSsh = MakeSection("SSH 隧道 / 跳板");
            var sshLayout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            sshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            sshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _tunnelCheck = new CheckBox { Text = "使用 SSH 隧道（跳板机）", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true };
            sshLayout.Controls.Add(_tunnelCheck, 0, 0); sshLayout.SetColumnSpan(_tunnelCheck, 2);
            _tunnelHostBox = AddRow(sshLayout, 1, "跳板主机", new TextBox());
            _tunnelPortBox = AddRow(sshLayout, 2, "跳板端口", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 });
            _tunnelUserBox = AddRow(sshLayout, 3, "跳板用户", new TextBox());
            SectionContent(_secSsh, sshLayout);
            _advFlow.Controls.Add(_secSsh);

            // --- RDP 区 ---
            _secRdp = MakeSection("RDP 选项");
            var rdpGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
            rdpGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            rdpGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // 域名仅 RDP 域账户有意义——从基本信息移到这里
            _domainBox = AddRow(rdpGrid, 0, "RDP域名", new TextBox());
            WinFormsCompat.SetCueBanner(_domainBox, "域账户如 CONTOSO，普通账户留空");
            var rdpChecks = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
            _rdpDriveCheck = new CheckBox { Text = "本地磁盘", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true };
            _rdpClipboardCheck = new CheckBox { Text = "剪贴板", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true, Checked = true };
            _rdpPrinterCheck = new CheckBox { Text = "打印机", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true };
            _rdpFullScreenCheck = new CheckBox { Text = "全屏", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true };
            _rdpNlaCheck = new CheckBox { Text = "NLA 认证", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true, Checked = true };
            _rdpForceNlaCheck = new CheckBox { Text = "强制 NLA", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true, Checked = false };
            rdpChecks.Controls.AddRange(new Control[] { _rdpDriveCheck, _rdpClipboardCheck, _rdpPrinterCheck, _rdpFullScreenCheck, _rdpNlaCheck, _rdpForceNlaCheck });
            var depthPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            depthPanel.Controls.Add(new Label { Text = "色深:", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true });
            _rdpColorDepth = new NumericUpDown { Minimum = 8, Maximum = 32, Value = 32, Increment = 8, Width = 60 };
            depthPanel.Controls.Add(_rdpColorDepth);
            rdpGrid.Controls.Add(rdpChecks, 1, 1);
            rdpGrid.Controls.Add(depthPanel, 1, 2);
            // 引擎选择：旧堡垒机/代理常与 FreeRDP 不兼容（重定向 PDU 处理差异），
            // 系统自带 mstsc（ActiveX 嵌入）是微软自家实现，兼容性最好
            var enginePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            enginePanel.Controls.Add(new Label { Text = "渲染引擎:", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true });
            _rdpEngineCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            _rdpEngineCombo.Items.AddRange(new object[] { "自动（优先 FreeRDP）", "FreeRDP 进程嵌入", "系统 mstsc（兼容模式）" });
            enginePanel.Controls.Add(_rdpEngineCombo);
            rdpGrid.Controls.Add(enginePanel, 1, 3);
            // 负载均衡 token：堡垒机/NetScaler 下发的 LB_LOAD_BALANCE_INFO Cookie（如 tsv://... 或 Cookie: msts=...）
            var lbPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            lbPanel.Controls.Add(new Label { Text = "负载均衡:", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true });
            _rdpLoadBalanceBox = new TextBox { Width = 230 };
            WinFormsCompat.SetCueBanner(_rdpLoadBalanceBox, "如 Cookie: msts=NSFVERIFYHASH=... (选填)");
            lbPanel.Controls.Add(_rdpLoadBalanceBox);
            rdpGrid.Controls.Add(lbPanel, 1, 4);
            SectionContent(_secRdp, rdpGrid);
            _advFlow.Controls.Add(_secRdp);

            // --- Serial 区 ---
            _secSerial = MakeSection("串口参数");
            var serialLayout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            serialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            serialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _serialPortCombo = AddRow(serialLayout, 0, "端口", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
            _serialPortCombo.Items.AddRange(new object[] { "COM1", "COM2", "COM3", "COM4", "/dev/ttyS0", "/dev/ttyUSB0" });
            _serialBaudCombo = AddRow(serialLayout, 1, "波特率", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialBaudCombo.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200" });
            _serialBaudCombo.SelectedItem = "9600";
            _serialDataBitsCombo = AddRow(serialLayout, 2, "数据位", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialDataBitsCombo.Items.AddRange(new object[] { "5", "6", "7", "8" });
            _serialDataBitsCombo.SelectedItem = "8";
            _serialStopBitsCombo = AddRow(serialLayout, 3, "停止位", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialStopBitsCombo.Items.AddRange(new object[] { "1", "1.5", "2" });
            _serialStopBitsCombo.SelectedItem = "1";
            _serialParityCombo = AddRow(serialLayout, 4, "校验位", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialParityCombo.Items.AddRange(new object[] { "None", "Odd", "Even", "Mark", "Space" });
            _serialParityCombo.SelectedItem = "None";
            SectionContent(_secSerial, serialLayout);
            _advFlow.Controls.Add(_secSerial);

            // --- 备注 ---
            var notesSec = MakeSection("备注");
            _notesBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Width = 512,
                Height = 56,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f)
            };
            WinFormsCompat.SetCueBanner(_notesBox, "服务器用途、特殊配置、注意事项...");
            SectionContent(notesSec, _notesBox);
            _advFlow.Controls.Add(notesSec);

            _advancedHost.Controls.Add(_advFlow);

            // ===== 底部按钮 =====
            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = Color.FromArgb(37, 37, 38) };
            _btnPanel = btnPanel;
            // 按钮交给布局引擎（RightToLeft 流式：先加的靠右）。
            // 不用绝对坐标 + Anchor.Right：在字体/DPI 缩放下会双重补偿漂出窗体右边界，
            // 0.1.118 实测 okBtn X=1009 > ClientSize 宽 808 → “保存按钮不见了”的根因。
            var btnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.FromArgb(37, 37, 38),
                Padding = new Padding(0, 7, 16, 0)
            };
            var okBtn = new Button
            {
                Text = _isNew ? "创建" : "保存",
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = DpiScale.S(this, 80, 30),
                Margin = new Padding(0)
            };
            var cancelBtn = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                FlatStyle = FlatStyle.Flat,
                Size = DpiScale.S(this, 80, 30),
                Margin = new Padding(0, 0, 8, 0)
            };
            okBtn.Click += (s, e) => { SaveToConfig(); };
            _okBtn = okBtn;
            btnFlow.Controls.Add(okBtn);      // RightToLeft：第一个在最右
            btnFlow.Controls.Add(cancelBtn);
            btnPanel.Controls.Add(btnFlow);

            // Dock 顺序：后添加的先布局——Top 先钉住，Bottom 再钉住，Fill 吃剩余空间
            Controls.Add(_advancedHost);
            Controls.Add(btnPanel);
            Controls.Add(topPanel);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
        }

        /// <summary>高级区小节：标题行 + 内容行（TableLayoutPanel 避免绝对定位重叠）。</summary>
        private TableLayoutPanel MakeSection(string title)
        {
            var t = new TableLayoutPanel
            {
                ColumnCount = 1,
                RowCount = 2,
                AutoSize = true,
                Width = 516,
                Margin = new Padding(0, 0, 0, 10)
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label
            {
                Text = title,
                ForeColor = Color.FromArgb(150, 150, 155),
                AutoSize = true,
                Font = Services.FormFontPolicy.UiFont(0f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 3)
            }, 0, 0);
            return t;
        }

        /// <summary>把内容控件放进小节第 1 行。</summary>
        private void SectionContent(TableLayoutPanel section, Control content)
        {
            content.Dock = DockStyle.Fill;
            content.Margin = new Padding(0);
            section.Controls.Add(content, 0, 1);
        }

        /// <summary>展开/收起高级区并同步调整窗口高度。</summary>
        private void ToggleAdvanced()
        {
            bool expand = !_expanded;
            DiagLog.Info("ConnDialog", "ToggleAdvanced begin expand=" + expand
                + " Height=" + Height + " advFlowPref=" + Math.Max(_advFlow.Height, _advFlow.PreferredSize.Height));
            if (expand)
            {
                _advancedHost.Visible = true;
                _advancedHost.PerformLayout();
                int h = Math.Max(_advFlow.Height, _advFlow.PreferredSize.Height);
                int workH = Screen.FromControl(this).WorkingArea.Height;
                int desired = Height + h + 16;
                if (desired > workH - 32)
                {
                    // FixedDialog 不能手拉也不能滚窗体：封顶到工作区，剩余内容靠高级区滚动条
                    Height = workH - 32;
                    DiagLog.Info("ConnDialog", "ToggleAdvanced capped desired=" + desired + " -> " + Height + " (workArea=" + workH + ")");
                }
                else
                {
                    Height = desired;
                }
                _expandedDelta = h + 16; // 记账用名义值；封顶时收起也按名义值减回，保证能回到原始高度附近
                _moreLink.Text = "收起高级选项 ▴";
            }
            else
            {
                Height -= _expandedDelta > 0 ? _expandedDelta : Math.Max(_advFlow.Height, _advFlow.PreferredSize.Height) + 16;
                if (Height < MinimumTrackingHeight()) Height = MinimumTrackingHeight();
                _advancedHost.Visible = false;
                _moreLink.Text = "更多选项 ▾";
            }
            _expanded = expand;
            DiagLog.Info("ConnDialog", "ToggleAdvanced done Height=" + Height);
        }

        /// <summary>折叠态的合理最低高度：按顶部面板首选高度 + 按钮栏推算。</summary>
        private int MinimumTrackingHeight()
        {
            return 330; // 设计基准值，防止异常情况下窗体塌成一条线
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 可见性必须同时检查横向和纵向——0.1.118 只查了纵向，按钮横向飞出时误报 true
            bool visOk = _okBtn.Bottom <= ClientSize.Height && _okBtn.Height > 0
                         && _okBtn.Left >= 0 && _okBtn.Right <= ClientSize.Width && _okBtn.Width > 0;
            DiagLog.Info("ConnDialog", "shown ClientSize=" + ClientSize.Width + "x" + ClientSize.Height
                + " okBtnBounds=" + _okBtn.Bounds + " visibleInForm=" + visOk
                + " btnPanelBottom=" + _btnPanel.Bottom + " workArea=" + Screen.FromControl(this).WorkingArea);
        }

        /// <summary>编辑既有连接时若配置过高级选项，自动展开让用户看到当前状态。</summary>
        private void MaybeAutoExpand()
        {
            bool hasAdvanced =
                (_config.JumpChain != null && _config.JumpChain.Hops != null && _config.JumpChain.Hops.Count > 0)
                || (_config.Metadata != null &&
                    (_config.Metadata.ContainsKey("rdp_drives") || _config.Metadata.ContainsKey("rdp_fullscreen")
                     || (_config.Metadata.ContainsKey("rdp_nla") && _config.Metadata["rdp_nla"] == "false")
                     || (_config.Metadata.ContainsKey("rdp_force_nla") && _config.Metadata["rdp_force_nla"] == "true")
                     || (_config.Metadata.ContainsKey("rdp_loadbalance") && !string.IsNullOrEmpty(_config.Metadata["rdp_loadbalance"]))
                     || (_config.Metadata.ContainsKey("rdp_clipboard") && _config.Metadata["rdp_clipboard"] == "false")
                     || (_config.Metadata.ContainsKey("rdp_engine") && _config.Metadata["rdp_engine"] != "auto")))
                || _config.Serial != null
                || (_config.Metadata != null && _config.Metadata.ContainsKey("notes")
                    && !string.IsNullOrEmpty(_config.Metadata["notes"]));
            if (hasAdvanced && !_expanded) ToggleAdvanced();
        }

        private T AddRow<T>(TableLayoutPanel layout, int row, string label, T control) where T : Control
        {
            var text = label ?? "";
            if (text.EndsWith(":") || text.EndsWith("："))
                text = text.TrimEnd(':', '：');
            layout.Controls.Add(new Label
            {
                Text = text,
                ForeColor = Color.FromArgb(204, 204, 204),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 8, 0),
                Height = 30
            }, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 4);
            layout.Controls.Add(control, 1, row);
            return control;
        }

        private void OnProtocolChanged(object sender, EventArgs e)
        {
            var proto = (string)_protocolCombo.SelectedItem;
            _portBox.Value = proto == "SSH" ? 22 : proto == "RDP" ? 3389 : 9600;
            // 高级区只显示当前协议相关的分节
            bool isSsh = proto == "SSH", isRdp = proto == "RDP", isSerial = proto == "Serial";
            _secSsh.Visible = isSsh;
            _secRdp.Visible = isRdp;
            _secSerial.Visible = isSerial;
            _domainBox.Enabled = isRdp;
            if (_advancedHost.Visible) _advancedHost.PerformLayout();
        }

        private void LoadFromConfig()
        {
            _nameBox.Text = _config.Name ?? "";
            _protocolCombo.SelectedItem = _config.Protocol == ProtocolType.RDP ? "RDP" :
                                          _config.Protocol == ProtocolType.Serial ? "Serial" : "SSH";
            _hostBox.Text = _config.Host ?? "";
            _portBox.Value = _config.Port > 0 ? _config.Port : 22;
            _usernameBox.Text = _config.Username ?? "";
            _groupPathBox.Text = _config.GroupPath ?? "";
            _credentialRefBox.Text = _config.CredentialRefId ?? "";
            RefreshCredentialTitle();

            // 备注（从 Metadata 取）
            if (_config.Metadata != null && _config.Metadata.ContainsKey("notes"))
                _notesBox.Text = _config.Metadata["notes"];

            // SSH 跳板（JumpChain 首跳）
            if (_config.JumpChain != null && _config.JumpChain.Hops != null && _config.JumpChain.Hops.Count > 0)
            {
                var hop = _config.JumpChain.Hops[0];
                _tunnelCheck.Checked = true;
                _tunnelHostBox.Text = hop.Host ?? "";
                _tunnelPortBox.Value = hop.Port > 0 ? hop.Port : 22;
                _tunnelUserBox.Text = hop.Username ?? "";
            }

            // Serial
            if (_config.Serial != null)
            {
                _serialPortCombo.Text = _config.Serial.PortName ?? "COM1";
                _serialBaudCombo.SelectedItem = _config.Serial.BaudRate.ToString();
                _serialDataBitsCombo.SelectedItem = _config.Serial.DataBits.ToString();
                _serialParityCombo.SelectedItem = _config.Serial.Parity.ToString();
            }

            // RDP options (from Metadata)
            if (_config.Metadata != null)
            {
                _rdpDriveCheck.Checked = _config.Metadata.ContainsKey("rdp_drives") && _config.Metadata["rdp_drives"] == "true";
                _rdpClipboardCheck.Checked = !_config.Metadata.ContainsKey("rdp_clipboard") || _config.Metadata["rdp_clipboard"] != "false";
                if (_config.Metadata.ContainsKey("rdp_colordepth"))
                    _rdpColorDepth.Value = int.Parse(_config.Metadata["rdp_colordepth"]);
                _rdpFullScreenCheck.Checked = _config.Metadata.ContainsKey("rdp_fullscreen") && _config.Metadata["rdp_fullscreen"] == "true";
                _rdpNlaCheck.Checked = !_config.Metadata.ContainsKey("rdp_nla") || _config.Metadata["rdp_nla"] != "false";
                _rdpForceNlaCheck.Checked = _config.Metadata.ContainsKey("rdp_force_nla") && _config.Metadata["rdp_force_nla"] == "true";
                if (_config.Metadata.ContainsKey("rdp_loadbalance"))
                    _rdpLoadBalanceBox.Text = _config.Metadata["rdp_loadbalance"];
                string eng;
                if (_config.Metadata.TryGetValue("rdp_engine", out eng))
                    _rdpEngineCombo.SelectedIndex = eng == "mstscax" ? 2 : eng == "freerdp" ? 1 : 0;
            }
            if (_rdpEngineCombo.SelectedIndex < 0) _rdpEngineCombo.SelectedIndex = 0;

            // 配置过高级选项则自动展开，避免“明明配了却看不见”
            MaybeAutoExpand();
        }

        private void SaveToConfig()
        {
            _config.Name = _nameBox.Text.Trim();
            var proto = (string)_protocolCombo.SelectedItem;
            _config.Protocol = proto == "RDP" ? ProtocolType.RDP : proto == "Serial" ? ProtocolType.Serial : ProtocolType.SSH;
            _config.Host = _hostBox.Text.Trim();
            _config.Port = (int)_portBox.Value;
            _config.Username = _usernameBox.Text.Trim();
            _config.Domain = _domainBox.Text.Trim();
            _config.GroupPath = _groupPathBox.Text.Trim();
            _config.CredentialRefId = string.IsNullOrWhiteSpace(_credentialRefBox.Text) ? null : _credentialRefBox.Text.Trim();

            // Metadata
            if (_config.Metadata == null) _config.Metadata = new System.Collections.Generic.Dictionary<string, string>();
            _config.Metadata["notes"] = _notesBox.Text ?? "";

            // RDP
            _config.Metadata["rdp_drives"] = _rdpDriveCheck.Checked.ToString().ToLower();
            _config.Metadata["rdp_clipboard"] = _rdpClipboardCheck.Checked.ToString().ToLower();
            _config.Metadata["rdp_colordepth"] = _rdpColorDepth.Value.ToString();
            _config.Metadata["rdp_fullscreen"] = _rdpFullScreenCheck.Checked.ToString().ToLower();
            _config.Metadata["rdp_nla"] = _rdpNlaCheck.Checked.ToString().ToLower();
            _config.Metadata["rdp_force_nla"] = _rdpForceNlaCheck.Checked.ToString().ToLower();
            _config.Metadata["rdp_loadbalance"] = _rdpLoadBalanceBox.Text?.Trim() ?? "";
            _config.Metadata["rdp_engine"] = _rdpEngineCombo.SelectedIndex == 2 ? "mstscax"
                                           : _rdpEngineCombo.SelectedIndex == 1 ? "freerdp" : "auto";

            // JumpChain hop (SSH jump host UI)
            if (_tunnelCheck.Checked && !string.IsNullOrWhiteSpace(_tunnelHostBox.Text))
            {
                _config.JumpChain = new JumpChainConfig
                {
                    Hops = new System.Collections.Generic.List<JumpHop>
                    {
                        new JumpHop
                        {
                            Host = _tunnelHostBox.Text.Trim(),
                            Port = (int)_tunnelPortBox.Value,
                            Username = _tunnelUserBox.Text.Trim()
                        }
                    }
                };
            }
            else
            {
                _config.JumpChain = null;
            }

            // Serial
            if (_config.Protocol == ProtocolType.Serial)
            {
                var baud = 9600; int.TryParse((string)_serialBaudCombo.SelectedItem, out baud);
                var dataBits = 8; int.TryParse((string)_serialDataBitsCombo.SelectedItem, out dataBits);
                _config.Serial = new SerialConfig
                {
                    PortName = _serialPortCombo.Text.Trim(),
                    BaudRate = baud,
                    DataBits = dataBits,
                    Parity = (string)_serialParityCombo.SelectedItem == "Odd" ? System.IO.Ports.Parity.Odd :
                             (string)_serialParityCombo.SelectedItem == "Even" ? System.IO.Ports.Parity.Even :
                             (string)_serialParityCombo.SelectedItem == "Mark" ? System.IO.Ports.Parity.Mark :
                             (string)_serialParityCombo.SelectedItem == "Space" ? System.IO.Ports.Parity.Space :
                             System.IO.Ports.Parity.None,
                    StopBits = (string)_serialStopBitsCombo.SelectedItem == "2" ? System.IO.Ports.StopBits.Two :
                               (string)_serialStopBitsCombo.SelectedItem == "1.5" ? System.IO.Ports.StopBits.OnePointFive :
                               System.IO.Ports.StopBits.One
                };
            }

            // Generate ID for new connections
            if (string.IsNullOrEmpty(_config.Id))
                _config.Id = Guid.NewGuid().ToString("N");

            Result = _config;
        }

        private void OnPickCredential(object sender, EventArgs e)
        {
            if (_keepass == null)
            {
                MessageBox.Show("密码库服务不可用。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!_keepass.IsUnlocked)
            {
                MessageBox.Show("请先解锁密码库。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var picker = new KeePassEntryPicker(_keepass))
            {
                if (picker.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(picker.SelectedEntryId))
                {
                    _credentialRefBox.Text = picker.SelectedEntryId;
                    RefreshCredentialTitle();
                }
            }
        }

        private void OnClearCredential(object sender, EventArgs e)
        {
            _credentialRefBox.Text = "";
            RefreshCredentialTitle();
        }

        /// <summary>
        /// 根据 _credentialRefBox 中的 UUID 反查条目标题展示；未选或反查失败时显示“自动匹配”提示。
        /// </summary>
        private void RefreshCredentialTitle()
        {
            var uuid = _credentialRefBox.Text;
            if (string.IsNullOrWhiteSpace(uuid))
            {
                _credentialTitleLabel.Text = "未选（按主机+用户名自动匹配）";
                _credentialTitleLabel.ForeColor = Color.FromArgb(100, 100, 100);
                return;
            }
            if (_keepass == null || !_keepass.IsUnlocked)
            {
                // 未解锁时仅显示 UUID 前 8 位，避免用户面对原始 UUID。
                _credentialTitleLabel.Text = "已选 UUID: " + (uuid.Length > 12 ? uuid.Substring(0, 12) + "…" : uuid);
                _credentialTitleLabel.ForeColor = Color.FromArgb(204, 204, 204);
                return;
            }
            try
            {
                var entry = _keepass.GetEntry(uuid);
                if (entry == null)
                {
                    _credentialTitleLabel.Text = "凭据已被删除（清除后自动匹配）";
                    _credentialTitleLabel.ForeColor = Color.FromArgb(204, 120, 60);
                    return;
                }
                var title = string.IsNullOrWhiteSpace(entry.Title) ? "(无标题)" : entry.Title;
                var user = string.IsNullOrWhiteSpace(entry.Username) ? "" : " — " + entry.Username;
                _credentialTitleLabel.Text = "已选: " + title + user;
                _credentialTitleLabel.ForeColor = Color.FromArgb(120, 200, 120);
            }
            catch
            {
                _credentialTitleLabel.Text = "已选 UUID: " + (uuid.Length > 12 ? uuid.Substring(0, 12) + "…" : uuid);
                _credentialTitleLabel.ForeColor = Color.FromArgb(204, 204, 204);
            }
        }
    }
}
