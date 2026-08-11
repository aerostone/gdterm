using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 新建/编辑连接对话框——支持 SSH、RDP、Serial 三种协议
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
        private TextBox _domainBox;
        private TextBox _groupPathBox;
        private TextBox _notesBox;

        // SSH 高级
        private CheckBox _tunnelCheck;
        private TextBox _tunnelHostBox;
        private NumericUpDown _tunnelPortBox;
        private TextBox _tunnelUserBox;

        // RDP
        private CheckBox _rdpDriveCheck;
        private CheckBox _rdpClipboardCheck;
        private CheckBox _rdpPrinterCheck;
        private NumericUpDown _rdpColorDepth;
        private CheckBox _rdpFullScreenCheck;
        private CheckBox _rdpNlaCheck;

        // Serial
        private ComboBox _serialPortCombo;
        private ComboBox _serialBaudCombo;
        private ComboBox _serialDataBitsCombo;
        private ComboBox _serialStopBitsCombo;
        private ComboBox _serialParityCombo;

        // KeePass
        private TextBox _credentialRefBox;

        // Panels for protocol-specific settings
        private Panel _sshPanel;
        private Panel _rdpPanel;
        private Panel _serialPanel;

        private readonly IKeePassService _keepass;

        public ConnectionConfig Result { get; private set; }

        public ConnectionDialog(ConnectionConfig existing = null, IKeePassService keepass = null)
        {
            _config = existing ?? new ConnectionConfig();
            _keepass = keepass;
            _isNew = existing == null;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            LoadFromConfig();
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
            LoadFromConfig();
        }

        private void InitializeComponent()
        {
            Text = _isNew ? "新建连接" : $"编辑连接 — {_config.Name}";
            Size = new Size(560, 620);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(30, 30, 30);
            Font = new Font("Microsoft YaHei", 9f);

            var tabControl = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };

            // === Tab 1: 基本信息 ===
            var basicTab = new TabPage("基本信息");
            basicTab.BackColor = Color.FromArgb(30, 30, 30);
            basicTab.Padding = new Padding(12);
            var basicLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7,
                AutoSize = true
            };
            // 标签列加宽，避免中文「用户名/分组」被裁切错位
            basicLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            basicLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int r = 0; r < 7; r++)
                basicLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            _nameBox = AddRow(basicLayout, 0, "名称", new TextBox());
            _protocolCombo = AddRow(basicLayout, 1, "协议", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _protocolCombo.Items.AddRange(new object[] { "SSH", "RDP", "Serial" });
            _protocolCombo.SelectedIndexChanged += OnProtocolChanged;
            _hostBox = AddRow(basicLayout, 2, "主机", new TextBox());
            WinFormsCompat.SetCueBanner(_hostBox, "IP 或主机名，如 192.168.1.10");
            _portBox = AddRow(basicLayout, 3, "端口", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 });
            _usernameBox = AddRow(basicLayout, 4, "用户名", new TextBox());
            // 域名仅 RDP 域账户（DOMAIN\user）；SSH 不需要
            _domainBox = AddRow(basicLayout, 5, "RDP域名", new TextBox());
            WinFormsCompat.SetCueBanner(_domainBox, "仅 RDP 域账户，如 CONTOSO（SSH 可留空）");
            _groupPathBox = AddRow(basicLayout, 6, "分组", new TextBox());
            WinFormsCompat.SetCueBanner(_groupPathBox, "如: Web/生产");
            basicTab.Controls.Add(basicLayout);

            // === Tab 2: SSH ===
            _sshPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(12) };
            var sshLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, AutoSize = true };
            sshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            sshLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _tunnelCheck = new CheckBox { Text = "使用 SSH 隧道", ForeColor = Color.FromArgb(204, 204, 204) };
            sshLayout.Controls.Add(_tunnelCheck, 0, 0); sshLayout.SetColumnSpan(_tunnelCheck, 2);
            _tunnelHostBox = AddRow(sshLayout, 1, "隧道主机:", new TextBox());
            _tunnelPortBox = AddRow(sshLayout, 2, "隧道端口:", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 22 });
            _tunnelUserBox = AddRow(sshLayout, 3, "隧道用户:", new TextBox());
            _sshPanel.Controls.Add(sshLayout);

            // === Tab 3: RDP ===
            _rdpPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(12) };
            var rdpLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            _rdpDriveCheck = new CheckBox { Text = "重定向本地磁盘", ForeColor = Color.FromArgb(204, 204, 204) };
            _rdpClipboardCheck = new CheckBox { Text = "共享剪贴板", ForeColor = Color.FromArgb(204, 204, 204), Checked = true };
            _rdpPrinterCheck = new CheckBox { Text = "重定向打印机", ForeColor = Color.FromArgb(204, 204, 204) };
            var depthPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            depthPanel.Controls.Add(new Label { Text = "色深:", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true });
            _rdpColorDepth = new NumericUpDown { Minimum = 8, Maximum = 32, Value = 32, Increment = 8, Width = 60 };
            depthPanel.Controls.Add(_rdpColorDepth);
            _rdpFullScreenCheck = new CheckBox { Text = "全屏模式", ForeColor = Color.FromArgb(204, 204, 204) };
            _rdpNlaCheck = new CheckBox { Text = "NLA (网络级别认证)", ForeColor = Color.FromArgb(204, 204, 204), Checked = true };
            rdpLayout.Controls.AddRange(new Control[] { _rdpDriveCheck, _rdpClipboardCheck, _rdpPrinterCheck, depthPanel, _rdpFullScreenCheck, _rdpNlaCheck });
            _rdpPanel.Controls.Add(rdpLayout);

            // === Tab 4: Serial ===
            _serialPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), Padding = new Padding(12) };
            var serialLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, AutoSize = true };
            serialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            serialLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _serialPortCombo = AddRow(serialLayout, 0, "端口:", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown });
            _serialPortCombo.Items.AddRange(new object[] { "COM1", "COM2", "COM3", "COM4", "/dev/ttyS0", "/dev/ttyUSB0" });
            _serialBaudCombo = AddRow(serialLayout, 1, "波特率:", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialBaudCombo.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200" });
            _serialBaudCombo.SelectedItem = "9600";
            _serialDataBitsCombo = AddRow(serialLayout, 2, "数据位:", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialDataBitsCombo.Items.AddRange(new object[] { "5", "6", "7", "8" });
            _serialDataBitsCombo.SelectedItem = "8";
            _serialStopBitsCombo = AddRow(serialLayout, 3, "停止位:", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialStopBitsCombo.Items.AddRange(new object[] { "1", "1.5", "2" });
            _serialStopBitsCombo.SelectedItem = "1";
            _serialParityCombo = AddRow(serialLayout, 4, "校验位:", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            _serialParityCombo.Items.AddRange(new object[] { "None", "Odd", "Even", "Mark", "Space" });
            _serialParityCombo.SelectedItem = "None";
            _serialPanel.Controls.Add(serialLayout);

            // === Tab 5: 备注 ===
            var notesTab = new TabPage("备注");
            notesTab.BackColor = Color.FromArgb(30, 30, 30);
            _notesBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Consolas", 10f)
            };
            WinFormsCompat.SetCueBanner(_notesBox, "服务器用途、特殊配置、注意事项...");
            notesTab.Controls.Add(_notesBox);

            // === Tab 6: 凭据 ===
            var credTab = new TabPage("凭据");
            credTab.BackColor = Color.FromArgb(30, 30, 30);
            var credLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(12) };
            credLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            credLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            credLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            credLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            credLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            credLayout.Controls.Add(new Label { Text = "凭据 ID:", ForeColor = Color.FromArgb(204, 204, 204), AutoSize = true }, 0, 0);
            _credentialRefBox = new TextBox { Dock = DockStyle.Fill };
            credLayout.Controls.Add(_credentialRefBox, 1, 0);
            var btnPickCred = new Button
            {
                Text = "选择...",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                Margin = new Padding(6, 0, 0, 0)
            };
            btnPickCred.Click += OnPickCredential;
            credLayout.Controls.Add(btnPickCred, 2, 0);
            credLayout.Controls.Add(new Label
            {
                Text = "提示: 留空则自动匹配密码库条目。\n可点击「选择」浏览 KeePass 条目，或手动指定 UUID。",
                ForeColor = Color.FromArgb(100, 100, 100),
                AutoSize = true,
                Dock = DockStyle.Fill
            }, 0, 1);
            credLayout.SetColumnSpan(credLayout.GetControlFromPosition(0, 1), 3);
            credTab.Controls.Add(credLayout);

            tabControl.TabPages.AddRange(new[] { basicTab, notesTab, credTab });

            // 按钮
            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = Color.FromArgb(37, 37, 38) };
            var okBtn = new Button
            {
                Text = _isNew ? "创建" : "保存",
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(80, 30),
                Location = new Point(Width - 190, 8)
            };
            var cancelBtn = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(80, 30),
                Location = new Point(Width - 100, 8)
            };
            okBtn.Click += (s, e) => { SaveToConfig(); };
            btnPanel.Controls.AddRange(new Control[] { okBtn, cancelBtn });

            Controls.Add(tabControl);
            Controls.Add(btnPanel);

            // 初始化面板（协议切换在 LoadFromConfig 中处理）
        }

        private T AddRow<T>(TableLayoutPanel layout, int row, string label, T control) where T : Control
        {
            // 去掉冒号，统一右对齐，避免中文标签宽窄不一导致错位
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
                Padding = new Padding(0, 0, 8, 0)
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
            // 域名只对 RDP 域账户有意义；SSH/Serial 禁用并提示
            bool rdp = proto == "RDP";
            _domainBox.Enabled = rdp;
            if (!rdp && string.IsNullOrWhiteSpace(_domainBox.Text))
                _domainBox.BackColor = Color.FromArgb(45, 45, 45);
            else
                _domainBox.BackColor = Color.FromArgb(37, 37, 38);
        }

        private void LoadFromConfig()
        {
            _nameBox.Text = _config.Name ?? "";
            _protocolCombo.SelectedItem = _config.Protocol == ProtocolType.RDP ? "RDP" :
                                          _config.Protocol == ProtocolType.Serial ? "Serial" : "SSH";
            _hostBox.Text = _config.Host ?? "";
            _portBox.Value = _config.Port > 0 ? _config.Port : 22;
            _usernameBox.Text = _config.Username ?? "";
            _domainBox.Text = _config.Domain ?? "";
            _groupPathBox.Text = _config.GroupPath ?? "";
            _credentialRefBox.Text = _config.CredentialRefId ?? "";

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
            }
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
                }
            }
        }
    }
}
