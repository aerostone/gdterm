using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Tools.Scanning;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 扫描中心——插件化扫描体系主界面。
    /// 左侧插件清单（内置+用户自建，热更新自动刷新），一键运行；
    /// 右上发现列表按严重级别着色；右下原始输出。
    /// 目标：本机 / 当前已连接的 SSH 远程主机（linux=bash、windows=同源 ps1 经 EncodedCommand）。
    /// </summary>
    public class ScannerCenterForm : Form
    {
        private readonly ScanPluginStore _store;
        private readonly Func<Gdterm.Tools.ISshRemoteSession> _remoteSessionFactory;

        private ComboBox _targetCombo;
        private Label _hotStateLabel;
        private Button _reloadButton;
        private Button _openFolderButton;
        private Button _runButton;
        private ListView _pluginList;
        private ListView _findingList;
        private Label _findingHeader;
        private TextBox _rawOutput;
        private SplitContainer _split;

        // WMI 免 SSH 通道的连接参数行（仅该目标可见）
        private Panel _wmiPanel;
        private TextBox _wmiHost;
        private TextBox _wmiUser;
        private TextBox _wmiPass;

        private readonly ScanRunner _runner = new ScanRunner();
        private bool _running;

        public ScannerCenterForm(ScanPluginStore store, Func<Gdterm.Tools.ISshRemoteSession> remoteSessionFactory)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _remoteSessionFactory = remoteSessionFactory;

            Text = "扫描中心（插件）";
            StartPosition = FormStartPosition.CenterParent;
            Size = DpiScale.S(960, 640);
            MinimumSize = DpiScale.S(780, 520);

            BuildUi();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);

            _store.PluginsReloaded += OnStoreReloaded;
            RefreshPluginList();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _store.PluginsReloaded -= OnStoreReloaded;
            base.OnFormClosed(e);
        }

        // ===== UI 构建 =====

        private void BuildUi()
        {
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 8, 8, 0),
                WrapContents = false
            };
            top.Controls.Add(new Label
            {
                Text = "目标:",
                AutoSize = true,
                Margin = new Padding(3, 8, 4, 0)
            });
            _targetCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 250
            };
            _targetCombo.Items.Add("本机（Windows）");
            _targetCombo.Items.Add("当前远程主机（SSH 已连）");
            _targetCombo.Items.Add("远程 Windows（WMI·免SSH）");
            _targetCombo.SelectedIndex = 0;
            _targetCombo.SelectedIndexChanged += (s, ev) => { UpdateWmiPanelVisibility(); UpdateRunButtonState(); };
            top.Controls.Add(_targetCombo);

            _runButton = new Button { Text = "运行选中", Width = 96 };
            _runButton.Click += OnRunClicked;
            top.Controls.Add(_runButton);

            _reloadButton = new Button { Text = "重新加载插件", Width = 110 };
            _reloadButton.Click += (s, ev) => _store.Reload();
            top.Controls.Add(_reloadButton);

            _openFolderButton = new Button { Text = "打开插件目录", Width = 110 };
            _openFolderButton.Click += OnOpenPluginsFolder;
            top.Controls.Add(_openFolderButton);

            _hotStateLabel = new Label
            {
                Text = "",
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 200, 120),
                Margin = new Padding(6, 10, 3, 0)
            };
            top.Controls.Add(_hotStateLabel);
            Controls.Add(top);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 300
            };

            // 左：插件清单
            var pluginPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            _pluginList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false
            };
            _pluginList.Columns.Add("插件", 190);
            _pluginList.Columns.Add("目标", 90);
            _pluginList.Columns.Add("分类", 70);
            _pluginList.Columns.Add("来源", 66);
            _pluginList.Columns.Add("签名", 74);
            _pluginList.Columns.Add("版本", 50);
            _pluginList.SelectedIndexChanged += (s, ev) => UpdateRunButtonState();
            _pluginList.DoubleClick += (s, ev) => { if (!_running && SelectedRunnablePlugins().Count > 0) OnRunClicked(null, null); };
            pluginPanel.Controls.Add(_pluginList);
            var pluginHint = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Text = "提示：双击运行；在 插件目录 增删改脚本即热更新（无需重启）",
                ForeColor = SystemColors.GrayText
            };
            pluginPanel.Controls.Add(pluginHint);
            _split.Panel1.Controls.Add(pluginPanel);

            // 右：发现 + 原始输出
            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };

            var findingPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 8, 0) };
            _findingList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false
            };
            _findingList.Columns.Add("级别", 70);
            _findingList.Columns.Add("标题", 210);
            _findingList.Columns.Add("详情", 330);
            findingPanel.Controls.Add(_findingList);
            var findingHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = "发现（0）",
                Font = new Font(Font, FontStyle.Bold)
            };
            _findingHeader = findingHeader;
            findingPanel.Controls.Add(findingHeader);
            findingHeader.BringToFront();
            rightSplit.Panel1.Controls.Add(findingPanel);

            var rawPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 8, 8) };
            _rawOutput = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 8.5f)
            };
            rawPanel.Controls.Add(_rawOutput);
            var rawHeader = new Label { Dock = DockStyle.Top, Height = 22, Text = "原始输出", Font = new Font(Font, FontStyle.Bold) };
            rawPanel.Controls.Add(rawHeader);
            rawHeader.BringToFront();
            rightSplit.Panel2.Controls.Add(rawPanel);
            _split.Panel2.Controls.Add(rightSplit);
            // Dock 按添加逆序布局：先加 Fill，再加两个 Top，视觉自上而下 = top / wmi / split
            Controls.Add(_split);
            Controls.Add(BuildWmiPanel());
            UpdateWmiPanelVisibility();

            // 必须在停靠生效（获得真实尺寸）后再设，否则构造期默认尺寸过小会拋参数异常；
            // 布局在句柄创建后才完成，故延到 OnShown

            AcceptButton = _runButton;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateHotStateLabel();
            ApplySplitters();
        }

        private bool _splittersApplied;

        private void ApplySplitters()
        {
            if (_splittersApplied || _split == null || !_split.IsHandleCreated) return;
            _splittersApplied = true;
            try { if (_split.Width > 640) _split.SplitterDistance = 300; }
            catch (Exception ex) { DiagLog.Swallowed("Scanner.ApplySplitters", ex); }
            var inner = _split.Panel2.Controls.OfType<SplitContainer>().FirstOrDefault();
            if (inner != null)
            {
                try { if (inner.Height > 200) inner.SplitterDistance = Math.Max(120, inner.Height / 2); }
                catch (Exception ex) { DiagLog.Swallowed("Scanner.ApplySplitters.inner", ex); }
            }
        }

        // ===== 数据与事件 =====

        private void OnStoreReloaded(object sender, EventArgs e)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(() => { RefreshPluginList(); })); return; }
                RefreshPluginList();
            }
            catch (Exception ex) { DiagLog.Swallowed("Scanner.OnStoreReloaded", ex); }
        }

        private void RefreshPluginList()
        {
            _pluginList.BeginUpdate();
            _pluginList.Items.Clear();
            foreach (var p in _store.Plugins)
            {
                var item = new ListViewItem(p.LoadError != null ? p.DisplayName + "（加载失败）" : p.DisplayName)
                {
                    Tag = p,
                    ForeColor = p.LoadError != null ? Color.Firebrick : (p.Source == "builtin" ? SystemColors.ControlText : Color.FromArgb(110, 170, 230))
                };
                if (!p.IsRunnable && p.LoadError == null)
                    item.ForeColor = SystemColors.GrayText; // 已停用
                item.SubItems.Add(p.TargetSummary);
                item.SubItems.Add(p.Manifest != null ? (p.Manifest.Category ?? "-") : "-");
                item.SubItems.Add(p.Source == "builtin" ? "内置" : "用户");
                item.SubItems.Add(TrustBadge(p));
                item.SubItems.Add(p.Manifest != null ? (p.Manifest.Version ?? "-") : "-");
                if (p.LoadError != null)
                    item.ToolTipText = p.LoadError;
                else if (p.Manifest != null && !string.IsNullOrEmpty(p.Manifest.Description))
                    item.ToolTipText = p.Manifest.Description + "\r\n脚本: " + p.ScriptPath;
                _pluginList.Items.Add(item);
            }
            _pluginList.EndUpdate();
            UpdateHotStateLabel();
            UpdateRunButtonState();
        }

        private void UpdateHotStateLabel()
        {
            var total = _store.Plugins.Count;
            var runnable = _store.Plugins.Count(x => x.IsRunnable);
            _hotStateLabel.Text = string.Format("热更新监控中 · {0} 个插件（可运行 {1}）", total, runnable);
        }

        /// <summary>WMI 目标的主机/凭据行；仅选中 WMI 目标时显示。</summary>
        private Panel BuildWmiPanel()
        {
            _wmiPanel = new Panel { Dock = DockStyle.Top, Height = 34, Visible = false };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(8, 4, 8, 0) };
            flow.Controls.Add(new Label { Text = "主机:", AutoSize = true, Margin = new Padding(3, 9, 4, 0) });
            _wmiHost = new TextBox { Width = 160 };
            flow.Controls.Add(_wmiHost);
            flow.Controls.Add(new Label { Text = "用户名:", AutoSize = true, Margin = new Padding(10, 9, 4, 0) });
            _wmiUser = new TextBox { Width = 140 };
            flow.Controls.Add(_wmiUser);
            flow.Controls.Add(new Label { Text = "密码:", AutoSize = true, Margin = new Padding(10, 9, 4, 0) });
            _wmiPass = new TextBox { Width = 140, UseSystemPasswordChar = true };
            flow.Controls.Add(_wmiPass);
            flow.Controls.Add(new Label
            {
                Text = "留空凭据=用当前身份；域账号格式 DOMAIN\\user；需目标管理员权限 + ADMIN$ 共享",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(10, 9, 3, 0)
            });
            _wmiPanel.Controls.Add(flow);
            return _wmiPanel;
        }

        private void UpdateWmiPanelVisibility()
        {
            if (_wmiPanel != null) _wmiPanel.Visible = IsWmiTarget;
        }

        private bool IsRemoteTarget
        {
            get { return _targetCombo != null && _targetCombo.SelectedIndex == 1; }
        }

        private bool IsWmiTarget
        {
            get { return _targetCombo != null && _targetCombo.SelectedIndex == 2; }
        }

        /// <summary>签名徽标：官方签名/未签名/签名无效，颜色区分。</summary>
        private static string TrustBadge(ScanPlugin p)
        {
            if (p.LoadError != null) return "-";
            switch (p.Trust)
            {
                case ScanTrust.Trusted: return "官方签名";
                case ScanTrust.Invalid: return "签名无效!";
                default: return "未签名";
            }
        }

        private List<ScanPlugin> SelectedRunnablePlugins()
        {
            return _pluginList.SelectedItems.Cast<ListViewItem>()
                .Select(x => x.Tag as ScanPlugin)
                .Where(x => x != null && x.IsRunnable)
                .ToList();
        }

        private void UpdateRunButtonState()
        {
            if (_runButton == null) return;
            if (IsRemoteTarget)
                _runButton.Enabled = !_running && SelectedRunnablePlugins().Count > 0 && RemoteSessionAvailable();
            else if (IsWmiTarget)
                _runButton.Enabled = !_running && SelectedRunnablePlugins().Count > 0;
            else
                _runButton.Enabled = !_running && SelectedRunnablePlugins().Count > 0;
        }

        private bool RemoteSessionAvailable()
        {
            try
            {
                var s = _remoteSessionFactory != null ? _remoteSessionFactory() : null;
                return s != null && s.IsConnected;
            }
            catch { return false; }
        }

        /// <summary>构造对应目标的执行通道。</summary>
        private IScanChannel BuildChannel(out string validationError)
        {
            validationError = null;
            if (IsWmiTarget)
            {
                var host = (_wmiHost != null ? _wmiHost.Text : "").Trim();
                if (host.Length == 0)
                {
                    validationError = "请填写远程主机地址（IP 或机器名）";
                    return null;
                }
                return new Gdterm.Tools.Scanning.WmiScanChannel(host,
                    _wmiUser != null ? _wmiUser.Text.Trim() : "",
                    _wmiPass != null ? _wmiPass.Text : "");
            }
            if (IsRemoteTarget)
            {
                var session = _remoteSessionFactory != null ? _remoteSessionFactory() : null;
                if (session == null || !session.IsConnected)
                {
                    validationError = "没有已连接的 SSH 远程主机。若目标无法安装 OpenSSH Server，可改选「远程 Windows（WMI·免SSH）」。";
                    return null;
                }
                return new SshScanChannel(session);
            }
            return new LocalScanChannel();
        }

        private void OnOpenPluginsFolder(object sender, EventArgs e)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "plugins", "scanner");
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开目录失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void OnRunClicked(object sender, EventArgs e)
        {
            if (_running) return;
            var plugins = SelectedRunnablePlugins();
            if (plugins.Count == 0) return;

            // 签名门禁：Invalid 拒跑；Unsigned 首次逐个确认并记台账（内容变更后重新问）
            foreach (var p in plugins)
            {
                if (p.Trust == ScanTrust.Invalid)
                {
                    MessageBox.Show(this,
                        "插件「" + p.DisplayName + "」的官方签名校验失败：\r\n\r\n" + p.ScriptPath +
                        "\r\n\r\n内容可能与发布时不一致（疑似被篡改），已拒绝运行。",
                        "签名无效", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (p.Trust == ScanTrust.Unsigned && !_store.IsApproved(p))
                {
                    var r = MessageBox.Show(this,
                        "插件「" + p.DisplayName + "」未经官方签名，无法验证来源。\r\n\r\n" +
                        "脚本: " + p.ScriptPath + "\r\n\r\n是否信任并运行？\r\n（批准按内容哈希记账；之后修改该插件会再次询问）",
                        "运行未签名插件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes) return;
                    _store.Approve(p);
                }
            }

            IScanChannel channel;
            string validationError;
            channel = BuildChannel(out validationError);
            if (channel == null)
            {
                MessageBox.Show(this, validationError, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _running = true;
            UpdateRunButtonState();
            _findingList.Items.Clear();
            SetFindingCount(0);
            _rawOutput.Text = "";

            // finding-03：try/finally 保证任何路径下 _running 都能复位；
            // 渲染前检查句柄存活性，避免批量运行中窗体被关闭后打在已释放控件上。
            try
            {
                var results = new List<ScanRunResult>();
                var runner = _runner;
                await Task.Run(() =>
                {
                    foreach (var p in plugins)
                    {
                        var r = runner.RunOne(p, channel);
                        results.Add(r);
                    }
                });

                if (IsDisposed || Disposing) return;
                foreach (var r in results) RenderResult(r);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    MessageBox.Show(this, "扫描执行失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _running = false;
                if (!IsDisposed && !Disposing) UpdateRunButtonState();
            }
        }

        private void RenderResult(ScanRunResult r)
        {
            var header = "== " + r.PluginName + " @ " + r.TargetName
                + "  (" + (int)r.Duration.TotalMilliseconds + "ms, exit=" + r.ExitCode + ") ==";
            AppendRawLine(header);

            if (!string.IsNullOrEmpty(r.RuntimeError))
            {
                AppendRawLine("[运行错误] " + r.RuntimeError);
            }

            foreach (var f in r.Findings)
            {
                var item = new ListViewItem(SeverityLabel(f.Severity))
                {
                    ForeColor = SeverityColor(f.Severity),
                    Tag = f
                };
                item.SubItems.Add(f.Title);
                item.SubItems.Add(f.Detail);
                _findingList.Items.Add(item);
            }
            SetFindingCount(_findingList.Items.Count);

            if (!string.IsNullOrEmpty(r.RawOutput)) _rawOutput.AppendText(r.RawOutput + Environment.NewLine);
            // finding-16：改用 AppendRawLine，删除下方与私有方法重复的扩展类
            if (!string.IsNullOrEmpty(r.ErrorOutput)) AppendRawLine("[stderr] " + r.ErrorOutput.TrimEnd());
        }

        private void AppendRawLine(string line)
        {
            _rawOutput.AppendText(line + Environment.NewLine);
        }

        private void SetFindingCount(int n)
        {
            if (_findingHeader != null) _findingHeader.Text = "发现（" + n + "）";
        }

        // ===== 展示辅助 =====

        private static string SeverityLabel(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "critical": return "严重";
                case "high": return "高";
                case "medium": return "中";
                case "low": return "低";
                default: return "信息";
            }
        }

        private static Color SeverityColor(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "critical": return Color.FromArgb(200, 40, 40);
                case "high": return Color.Firebrick;
                case "medium": return Color.FromArgb(200, 130, 30);
                case "low": return Color.FromArgb(160, 140, 40);
                default: return SystemColors.GrayText;
            }
        }
    }
}
