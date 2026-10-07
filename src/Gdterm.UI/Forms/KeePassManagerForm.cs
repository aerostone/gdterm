using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 密码管理器对话框
    /// 显示密码条目列表，支持增删改查、复制密码/用户名。
    /// 一体化 change 2026-09-26：与连接管理打通——
    ///   1. 顶部搜索框（同选择器，标题/用户名/分组/URL 过滤）；
    ///   2. 新增「关联连接」列：直接看到每条凭据被哪些连接引用；
    ///   3. 工具栏「新建连接」：从选中凭据一键建连接（预填主机/用户名/协议/端口并绑定 CredentialRefId），
    ///      那些从未建连接的凭据不再需要手动抄主机名到连接对话框。
    /// </summary>
    public class KeePassManagerForm : AntdUI.Window
    {
        private readonly IKeePassService _keepassService;
        /// <summary>连接存储（可选）：注入后启用「新建连接」与「关联连接」列。</summary>
        private readonly IConnectionStore _connectionStore;
        private readonly Action _connectionsChanged;
        private AntdUI.Input _searchBox;
        private AntdUI.Table _entryTable;
        private AntdUI.Label _statusLabel;
        private AntdUI.Button _btnNewConnection;
        /// <summary>空库引导层：覆盖表区，仅在“加载成功且 0 条目”时可见（UX change 2026-09-25）。</summary>
        private Panel _emptyGuide;
        private System.Collections.Generic.List<KeePassEntrySummary> _entries = new System.Collections.Generic.List<KeePassEntrySummary>();
        /// <summary>搜索过滤后的条目视图（与 _entries 同序子集）。</summary>
        private IList<KeePassEntrySummary> _view = new System.Collections.Generic.List<KeePassEntrySummary>();
        /// <summary>entryId → 引用它的连接名列表（LoadEntries 时重建）。</summary>
        private Dictionary<string, List<string>> _usageByEntry = new Dictionary<string, List<string>>();

        public KeePassManagerForm(IKeePassService keepassService, IConnectionStore connectionStore = null, Action connectionsChanged = null)
        {
            _keepassService = keepassService;
            _connectionStore = connectionStore;
            _connectionsChanged = connectionsChanged;
            Font = Gdterm.UI.Services.FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            InitializeComponent();
            LoadEntries();
        }

        private void InitializeComponent()
        {
            Text = "KeePass 密码管理器";
            Size = DpiScale.S(this, 700, 500);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;

            // 字体驱动 + DPI 缩放：所有尺寸从 fieldH/rowH/pad 派生，避免固定像素在大字号/高 DPI 下挤压
            int pad = DpiScale.V(this, 8);
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)); // 表格阅读行：24 地板，不复用输入框 38
            int btnPad = DpiScale.V(this, 10);
            int btnMargin = DpiScale.V(this, 4);
            var btnPadding = new Padding(btnPad, DpiScale.V(this, 4), btnPad, DpiScale.V(this, 4));
            var btnMarginR = new Padding(0, 0, DpiScale.V(this, 6), 0);

            // 工具行（左：操作按钮；右：搜索框。与凭据选择器同构，消除“选择器能搜、管理器不能搜”的割裂）
            var toolbarHost = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0)
            };
            toolbarHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbarHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(pad, DpiScale.V(this, 5), 0, DpiScale.V(this, 5)),
                BackColor = GdtermColorTable.Background
            };

            toolbar.Controls.Add(MakeToolBtn("添加", OnAddClick, btnPadding, btnMarginR));
            toolbar.Controls.Add(MakeToolBtn("编辑", OnEditClick, btnPadding, btnMarginR));
            toolbar.Controls.Add(MakeToolBtn("删除", OnDeleteClick, btnPadding, btnMarginR));
            toolbar.Controls.Add(new AntdUI.Divider { Orientation = AntdUI.TOrientation.Left, Thickness = 1f, Margin = new Padding(btnMargin) });
            toolbar.Controls.Add(MakeToolBtn("复制密码", OnCopyPasswordClick, btnPadding, btnMarginR));
            toolbar.Controls.Add(MakeToolBtn("复制用户名", OnCopyUsernameClick, btnPadding, btnMarginR));
            toolbar.Controls.Add(new AntdUI.Divider { Orientation = AntdUI.TOrientation.Left, Thickness = 1f, Margin = new Padding(btnMargin) });
            _btnNewConnection = MakeToolBtn("新建连接", OnNewConnectionFromEntry, btnPadding, btnMarginR);
            _btnNewConnection.Enabled = _connectionStore != null; // 无连接库时禁用（冒烟/内嵌场景）
            toolbar.Controls.Add(_btnNewConnection);
            toolbar.Controls.Add(MakeToolBtn("刷新", (s, e) => LoadEntries(), btnPadding, btnMarginR));

            // 搜索框：标题/用户名/分组/URL 即时过滤（同选择器体验）
            _searchBox = new AntdUI.Input
            {
                Name = "KeePassSearchBox",
                Dock = DockStyle.Fill,
                Margin = new Padding(pad, DpiScale.V(this, 5), pad, DpiScale.V(this, 5)),
                PlaceholderText = "搜索标题 / 用户名 / 分组 / URL…",
                MinimumSize = new Size(DpiScale.V(this, 200), fieldH)
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();

            toolbarHost.Controls.Add(toolbar, 0, 0);
            toolbarHost.Controls.Add(_searchBox, 1, 0);

            // 条目表（AntdUI.Table）
            _entryTable = new AntdUI.Table
            {
                Name = "KeePassEntryTable",
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9.5f),
                BorderWidth = 0,
                RowHeight = rowH
            };
            if (_connectionStore != null)
            {
                _entryTable.Columns.Add(new AntdUI.Column("Title", "标题", AntdUI.ColumnAlign.Left) { Width = "18%" });
                _entryTable.Columns.Add(new AntdUI.Column("Username", "用户名", AntdUI.ColumnAlign.Left) { Width = "16%" });
                _entryTable.Columns.Add(new AntdUI.Column("UsedBy", "关联连接", AntdUI.ColumnAlign.Left) { Width = "22%" });
                _entryTable.Columns.Add(new AntdUI.Column("GroupPath", "分组路径", AntdUI.ColumnAlign.Left) { Width = "16%" });
                _entryTable.Columns.Add(new AntdUI.Column("Url", "URL", AntdUI.ColumnAlign.Left) { Width = "16%" });
                _entryTable.Columns.Add(new AntdUI.Column("Modified", "最后修改", AntdUI.ColumnAlign.Left) { Width = "12%" });
            }
            else
            {
                _entryTable.Columns.Add(new AntdUI.Column("Title", "标题", AntdUI.ColumnAlign.Left) { Width = "20%" });
                _entryTable.Columns.Add(new AntdUI.Column("Username", "用户名", AntdUI.ColumnAlign.Left) { Width = "20%" });
                _entryTable.Columns.Add(new AntdUI.Column("GroupPath", "分组路径", AntdUI.ColumnAlign.Left) { Width = "25%" });
                _entryTable.Columns.Add(new AntdUI.Column("Url", "URL", AntdUI.ColumnAlign.Left) { Width = "20%" });
                _entryTable.Columns.Add(new AntdUI.Column("Modified", "最后修改", AntdUI.ColumnAlign.Left) { Width = "15%" });
            }
            _entryTable.CellClick += OnEntryCellClicked;         // 单击=选中（高亮自动跟随，状态栏回显）
            _entryTable.CellDoubleClick += OnEntryCellDoubleClicked; // 双击=复制密码

            // 状态栏（等宽字体行高，底部左对齐内边距）
            _statusLabel = new AntdUI.Label {
                Name = "KeePassStatusLabel",
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Text = "就绪",
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = GdtermColorTable.Muted,
                Padding = new Padding(pad, DpiScale.V(this, 5), pad, DpiScale.V(this, 5))
            };

            // 底部关闭条（RightToLeft：关闭在右，ESC 快捷关闭）
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, DpiScale.V(this, 4), pad, DpiScale.V(this, 4))
            };
            var closeButton = new AntdUI.Button
            {
                Name = "KeePassCloseButton",
                Text = "关闭",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Default,
                Padding = btnPadding,
                Margin = new Padding(0)
            };
            closeButton.Click += (s, e) => Close();
            bottomPanel.Controls.Add(closeButton);

            // 空库引导层：与表同格（同 bounds），BringToFront 后覆盖表区；空/有行两态由 LoadEntries 切换。
            // 引导只指向已有动作（添加/刷新），不新增“连接库”能力（change 2026-09-25 D1）。
            _emptyGuide = new Panel
            {
                Name = "KeePassEmptyGuide",
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                Visible = false
            };
            var emptyGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = GdtermColorTable.Background
            };
            emptyGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            emptyGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            emptyGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            // 显式给列 100%：不写列样式时 TLP 列按内容 AutoSize，内容会偏左而非居中
            emptyGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var emptyTitle = new AntdUI.Label
            {
                Name = "KeePassEmptyGuideTitle",
                Text = "还没有条目",
                AutoSize = true,
                Font = FormFontPolicy.UiFont(+2f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Foreground,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 6))
            };
            var emptyHint = new AntdUI.Label
            {
                Name = "KeePassEmptyGuideHint",
                Text = "点『添加』创建第一条；读取现有 KeePass 库需先在主界面解锁。",
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 0, 0, DpiScale.V(this, 10))
            };
            var emptyButtons = new FlowLayoutPanel
            {
                Name = "KeePassEmptyGuideButtons",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Anchor = AnchorStyles.None,
                BackColor = GdtermColorTable.Background,
                Margin = new Padding(0)
            };
            var emptyAddButton = new AntdUI.Button
            {
                Name = "KeePassEmptyGuideAddButton",
                Text = "添加第一条",
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                Padding = btnPadding,
                Margin = btnMarginR
            };
            emptyAddButton.Click += OnAddClick;
            var emptyRefreshButton = new AntdUI.Button
            {
                Name = "KeePassEmptyGuideRefreshButton",
                Text = "刷新",
                Type = AntdUI.TTypeMini.Default,
                Ghost = true,
                AutoSize = true,
                Padding = btnPadding,
                Margin = new Padding(DpiScale.V(this, 4), 0, 0, 0)
            };
            emptyRefreshButton.Click += (s, e) => LoadEntries();
            emptyButtons.Controls.Add(emptyAddButton);
            emptyButtons.Controls.Add(emptyRefreshButton);

            emptyGrid.Controls.Add(emptyTitle, 0, 0);
            emptyGrid.Controls.Add(emptyHint, 0, 1);
            emptyGrid.Controls.Add(emptyButtons, 0, 2);
            _emptyGuide.Controls.Add(emptyGrid);

            // 表 + 引导层同格：引导后加并置前，Dock=Fill 覆盖表区（两矩形全等，树检包含关系豁免重叠）
            var tableHost = new Panel { Dock = DockStyle.Fill, BackColor = GdtermColorTable.Background };
            _entryTable.Dock = DockStyle.Fill;
            tableHost.Controls.Add(_entryTable);
            tableHost.Controls.Add(_emptyGuide);
            _emptyGuide.BringToFront();

            // Dock 顺序：后添加的先布局——Top 先钉住，Bottom 再钉住，Fill 吃剩余空间
            Controls.Add(tableHost);
            Controls.Add(_statusLabel);
            Controls.Add(bottomPanel);
            Controls.Add(toolbarHost);

            CancelButton = closeButton;
        }

        private static AntdUI.Button MakeToolBtn(string text, EventHandler onClick, Padding padding, Padding margin)
        {
            var btn = new AntdUI.Button { Text = text, Type = AntdUI.TTypeMini.Default, Ghost = true, AutoSize = true, Padding = padding, Margin = margin };
            btn.Click += onClick;
            return btn;
        }

        /// <summary>AntdUI.Table 单元格单击：仅回显选中行，不复制（避免误触）。
        /// 行号语义：表头行占 INDEX 0（Table.Layout AddRowsHeader rows.Insert(0)），
        /// 数据行从 1 开始（Layout row.INDEX = row_i），SelectedIndex/RowIndex 均为 1 开始，
        /// 故映射到 _view（过滤视图）必须减 1；RowIndex 0（点表头）直接忽略。</summary>
        private void OnEntryCellClicked(object sender, AntdUI.TableClickEventArgs e)
        {
            int idx = e.RowIndex - 1;
            if (idx < 0 || idx >= _view.Count) return;
            var row = _view[idx];
            List<string> users;
            var usage = _usageByEntry.TryGetValue(row.Id, out users) && users.Count > 0
                ? "｜关联 " + users.Count + " 个连接"
                : "";
            _statusLabel.Text = "已选中：" + (row.Title ?? "(无标题)") + usage;
        }

        /// <summary>AntdUI.Table 单元格双击：复制该行密码（行号同上减 1，映射 _view）。</summary>
        private void OnEntryCellDoubleClicked(object sender, AntdUI.TableClickEventArgs e)
        {
            int idx = e.RowIndex - 1;
            if (idx < 0 || idx >= _view.Count) return;
            CopyEntryPassword(_view[idx].Id);
        }

        private sealed class EntryRow
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Username { get; set; }
            public string UsedBy { get; set; }
            public string GroupPath { get; set; }
            public string Url { get; set; }
            public string Modified { get; set; }
        }

        /// <summary>重建 entryId → 连接名列表 映射（连接的 CredentialRefId 直接引用）。</summary>
        private Dictionary<string, List<string>> BuildUsageMap()
        {
            var map = new Dictionary<string, List<string>>();
            if (_connectionStore == null) return map;
            try
            {
                var conns = _connectionStore.LoadAll() ?? new List<ConnectionConfig>();
                foreach (var c in conns)
                {
                    if (c == null || string.IsNullOrEmpty(c.CredentialRefId)) continue;
                    List<string> names;
                    if (!map.TryGetValue(c.CredentialRefId, out names))
                    {
                        names = new List<string>();
                        map[c.CredentialRefId] = names;
                    }
                    var label = string.IsNullOrEmpty(c.Name) ? (c.Host ?? "(未命名)") : c.Name;
                    names.Add(label);
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm.Usage", exSwallowed); } catch { } }
            return map;
        }

        private void LoadEntries()
        {
            try
            {
                var entries = _keepassService.ListEntries() ?? new System.Collections.Generic.List<KeePassEntrySummary>();
                _entries.Clear();
                foreach (var entry in entries)
                    _entries.Add(entry);
                _usageByEntry = BuildUsageMap();
                ApplyFilter();
                // 空库时才给引导（加载失败走 catch 分支，不显示引导以免误导）
                if (_emptyGuide != null) _emptyGuide.Visible = _entries.Count == 0;
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载失败：{ex.Message}";
                if (_emptyGuide != null) _emptyGuide.Visible = false;
            }
        }

        /// <summary>按搜索框即时过滤并重绑表数据（空过滤=全部；视图与 _entries 同序）。</summary>
        private void ApplyFilter()
        {
            var filter = _searchBox == null ? "" : (_searchBox.Text ?? "").Trim();
            IEnumerable<KeePassEntrySummary> src = _entries;
            if (filter.Length > 0)
            {
                src = src.Where(e =>
                    (e.Title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.Username ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.GroupPath ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.Url ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            _view = src.ToList();
            var rows = new List<EntryRow>();
            foreach (var entry in _view)
            {
                var title = entry.Title ?? "(无标题)";
                if (entry.HasSshPrivateKey) title = "🔑 " + title;
                string usedBy = "";
                List<string> users;
                if (_usageByEntry.TryGetValue(entry.Id, out users) && users.Count > 0)
                    usedBy = users.Count == 1 ? users[0] : users[0] + " 等" + users.Count + " 项";
                rows.Add(new EntryRow
                {
                    Id = entry.Id,
                    Title = title,
                    Username = entry.Username ?? "",
                    UsedBy = usedBy,
                    GroupPath = entry.GroupPath ?? "",
                    Url = entry.Url ?? "",
                    Modified = entry.LastModified > DateTime.MinValue
                        ? entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                        : ""
                });
            }
            _entryTable.DataSource = rows;
            int total = _entries.Count;
            _statusLabel.Text = filter.Length == 0
                ? $"共 {total} 个条目"
                : $"匹配 {_view.Count}/{total} 个条目";
        }

        /// <summary>取当前选中（或最后点击）的条目 Id；无选中返回 null。
        /// AntdUI Table 行号 1 开始（表头占 0），映射 _view 须减 1。</summary>
        private string SelectedEntryId()
        {
            int idx = _entryTable.SelectedIndex - 1;
            if (idx >= 0 && idx < _view.Count)
                return _view[idx].Id;
            return null;
        }

        private void OnAddClick(object sender, EventArgs e)
        {
            using (var dlg = new KeePassEntryEditForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        var entry = new KeePassEntry
                        {
                            Title = dlg.EntryTitle,
                            Username = dlg.EntryUsername,
                            Password = dlg.EntryPassword,
                            Url = dlg.EntryUrl,
                            Notes = dlg.EntryNotes,
                            GroupPath = dlg.EntryGroupPath
                        };
                        if (!KeePassPasswordWarning.ConfirmSaveIfWeak(this, _keepassService, entry.Password))
                        {
                            _statusLabel.Text = "已取消：密码强度警告未确认";
                            return;
                        }
                        _keepassService.CreateEntry(entry);
                        LoadEntries();
                        _statusLabel.Text = "条目已创建";
                        try { ToastNotifier.Success("凭据已创建"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm", exSwallowed); } catch { } }
                    }
                    catch (Exception ex)
                    {
                        AntdUI.Message.error(this, $"创建失败：{ex.Message}");
                    }
                }
            }
        }

        private void OnEditClick(object sender, EventArgs e)
        {
            var entryId = SelectedEntryId();
            if (entryId == null) return;

            try
            {
                var full = _keepassService.GetEntry(entryId);
                if (full == null)
                {
                    AntdUI.Message.warn(this, "无法加载条目详情（可能已删除或库已锁定）。");
                    return;
                }

                using (var dlg = new KeePassEntryEditForm())
                {
                    dlg.LoadFrom(full);
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        full.Title = dlg.EntryTitle;
                        full.Username = dlg.EntryUsername;
                        full.Password = dlg.EntryPassword;
                        full.Url = dlg.EntryUrl;
                        full.Notes = dlg.EntryNotes;
                        full.GroupPath = dlg.EntryGroupPath;
                        full.Hostname = dlg.EntryHostname;
                        full.Port = dlg.EntryPort;
                        full.Protocol = dlg.EntryProtocol;
                        full.AutoTypeSequence = dlg.EntryAutoType;
                        if (!KeePassPasswordWarning.ConfirmSaveIfWeak(this, _keepassService, full.Password))
                        {
                            _statusLabel.Text = "已取消：密码强度警告未确认";
                            return;
                        }
                        _keepassService.UpdateEntry(full);
                        LoadEntries();
                        _statusLabel.Text = "条目已更新：" + full.Title;
                        try { ToastNotifier.Success("凭据已保存：" + full.Title); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm", exSwallowed); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                AntdUI.Message.error(this, "编辑失败：" + ex.Message);
            }
        }

        private void OnDeleteClick(object sender, EventArgs e)
        {
            var entryId = SelectedEntryId();
            if (entryId == null) return;
            var row = _view.FirstOrDefault(x => x.Id == entryId); // IList<T> 无 Find，用 LINQ（.NET 4.6.2 兼容）
            var title = row != null ? (row.Title ?? "(无标题)") : entryId;

            // 一体化 change：被连接引用的凭据删除前提醒（否则连接静默回退自动匹配，用户无感）
            List<string> users;
            int usedCount = _usageByEntry.TryGetValue(entryId, out users) ? users.Count : 0;
            var msg = $"确定要删除条目 \"{title}\" 吗？\n此操作不可撤销。";
            if (usedCount > 0)
                msg += $"\n\n⚠ 有 {usedCount} 个连接正在引用该凭据，删除后这些连接将回退为自动匹配。";

            var confirm = MessageBox.Show(this,
                msg,
                "确认删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm == DialogResult.Yes)
            {
                try
                {
                    _keepassService.DeleteEntry(entryId);
                    LoadEntries();
                    _statusLabel.Text = $"已删除：{title}";
                    try { ToastNotifier.Warning("已删除：" + title); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm", exSwallowed); } catch { } }
                }
                catch (Exception ex)
                {
                    AntdUI.Message.error(this, $"删除失败：{ex.Message}");
                }
            }
        }

        private void OnCopyPasswordClick(object sender, EventArgs e)
        {
            var entryId = SelectedEntryId();
            if (entryId == null) return;
            CopyEntryPassword(entryId);
        }

        private void CopyEntryPassword(string entryId)
        {
            try
            {
                var credential = _keepassService.GetCredential(entryId);
                if (credential != null && !string.IsNullOrEmpty(credential.Password))
                {
                    ClipboardProtector.SetTextWithTtl(credential.Password);
                    // 状态栏提示 TTL
                    _statusLabel.Text = "密码已复制（约 30 秒后自动清空）";
                }
                else
                {
                    _statusLabel.Text = "该条目没有密码";
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"复制失败：{ex.Message}";
            }
        }

        private void OnCopyUsernameClick(object sender, EventArgs e)
        {
            var entryId = SelectedEntryId();
            if (entryId == null) return;
            var row = _view.FirstOrDefault(x => x.Id == entryId); // IList<T> 无 Find，用 LINQ（.NET 4.6.2 兼容）
            var username = row != null ? (row.Username ?? "") : "";

            if (!string.IsNullOrEmpty(username))
            {
                try
                {
                    Clipboard.SetText(username);
                    _statusLabel.Text = "用户名已复制到剪贴板";
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm", exSwallowed); } catch { } }
            }
            else
            {
                _statusLabel.Text = "该条目没有用户名";
            }
        }

        /// <summary>
        /// 从选中凭据一键新建连接（一体化 change 2026-09-26）：
        /// 预填主机/用户名/协议/端口/分组并绑定 CredentialRefId，保存后回连接库并通知刷新。
        /// </summary>
        private void OnNewConnectionFromEntry(object sender, EventArgs e)
        {
            if (_connectionStore == null)
            {
                _statusLabel.Text = "连接库不可用（本入口需从主窗体打开）";
                return;
            }
            var entryId = SelectedEntryId();
            if (entryId == null)
            {
                _statusLabel.Text = "请先选中一条凭据";
                return;
            }
            Gdterm.KeePass.Models.KeePassEntry full;
            try { full = _keepassService.GetEntry(entryId); }
            catch (Exception ex)
            {
                AntdUI.Message.error(this, "读取条目失败：" + ex.Message);
                return;
            }
            if (full == null)
            {
                AntdUI.Message.warn(this, "无法加载条目详情（可能已删除或库已锁定）。");
                return;
            }
            using (var dlg = new ConnectionDialog(full, _keepassService))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    try
                    {
                        _connectionStore.Add(dlg.Result);
                        LoadEntries(); // 刷新“关联连接”列
                        var created = string.IsNullOrEmpty(dlg.Result.Name) ? dlg.Result.Host : dlg.Result.Name;
                        _statusLabel.Text = "已创建连接：" + created + "（已绑定本条凭据）";
                        try { ToastNotifier.Success("连接已创建并绑定该凭据"); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm", exSwallowed); } catch { } }
                        try { _connectionsChanged?.Invoke(); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("KeePassManagerForm.ConnsChanged", exSwallowed); } catch { } }
                    }
                    catch (Exception ex)
                    {
                        AntdUI.Message.error(this, "创建连接失败：" + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// 深色工具栏渲染器
        /// </summary>
    }

    /// <summary>
    /// 条目编辑对话框（用于添加/编辑 KeePass 条目）
    /// </summary>
    internal class KeePassEntryEditForm : AntdUI.Window
    {
        private AntdUI.Input _titleBox;
        private AntdUI.Input _usernameBox;
        private AntdUI.Input _passwordBox;
        private AntdUI.Input _urlBox;
        private AntdUI.Input _notesBox;
        private AntdUI.Input _groupBox;

        private AntdUI.Input _hostBox;
        private AntdUI.InputNumber _portBox;
        private AntdUI.Input _protocolBox;
        private AntdUI.Input _autoTypeBox;

        public string EntryTitle { get { return _titleBox.Text; } }
        public string EntryUsername { get { return _usernameBox.Text; } }
        public string EntryPassword { get { return _passwordBox.Text; } }
        public string EntryUrl { get { return _urlBox.Text; } }
        public string EntryNotes { get { return _notesBox.Text; } }
        public string EntryGroupPath { get { return _groupBox.Text; } }
        public string EntryHostname { get { return _hostBox != null ? _hostBox.Text : ""; } }
        public int EntryPort { get { return _portBox != null ? (int)_portBox.Value : 0; } }
        public string EntryProtocol { get { return _protocolBox != null ? _protocolBox.Text : ""; } }
        public string EntryAutoType { get { return _autoTypeBox != null ? _autoTypeBox.Text : ""; } }

        public KeePassEntryEditForm()
        {
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
        }

        public void LoadFrom(KeePassEntry entry)
        {
            if (entry == null) return;
            // 种子（无 Id）不切换为“编辑”标题——连接对话框预填新建凭据时仍是新建语义
            if (!string.IsNullOrEmpty(entry.Id)) Text = "编辑密码条目";
            _titleBox.Text = entry.Title ?? "";
            _usernameBox.Text = entry.Username ?? "";
            _passwordBox.Text = entry.Password ?? "";
            _urlBox.Text = entry.Url ?? "";
            _notesBox.Text = entry.Notes ?? "";
            _groupBox.Text = string.IsNullOrEmpty(entry.GroupPath) ? "/" : entry.GroupPath;
            if (_hostBox != null) _hostBox.Text = entry.Hostname ?? "";
            if (_portBox != null) _portBox.Value = entry.Port > 0 && entry.Port <= 65535 ? entry.Port : 22;
            if (_protocolBox != null) _protocolBox.Text = string.IsNullOrEmpty(entry.Protocol) ? "SSH" : entry.Protocol;
            if (_autoTypeBox != null) _autoTypeBox.Text = entry.AutoTypeSequence ?? "";
        }

        private void InitializeComponent()
        {
            Text = "添加密码条目";
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距
            ClientSize = DpiScale.S(this, 420, 470);
            // 跟随字体/DPI 自动整体缩放（绝对定位在 11pt@144dpi 下会重叠/溢出）
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            // 字体驱动布局常量
            int pad = DpiScale.V(this, 12);
            int fieldH = FormFontPolicy.FieldHeight(this);
            int notesH = fieldH + DpiScale.V(this, 26); // 备注多行框比单行输入高两行空间
            int labelPadL = DpiScale.V(this, 3);
            int labelPadTop = DpiScale.V(this, 6);
            int labelPadR = DpiScale.V(this, 8);
            int ctrlPad = DpiScale.V(this, 4);
            int btnPadH = DpiScale.V(this, 7);
            int btnPadR = DpiScale.V(this, 15);
            int btnGap = DpiScale.V(this, 8);
            int btnShowPad = DpiScale.V(this, 6);
            int btnShowTop = DpiScale.V(this, 1);

            // ===== 底部按钮（流式靠右，随字体缩放）=====
            var btnPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, btnPadH, btnPadR, btnPadH)
            };
            var okButton = new AntdUI.Button {
                Text = "确定",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Primary,
                Margin = new Padding(0)
            };
            okButton.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            var cancelButton = new AntdUI.Button {
                Text = "取消",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Default,
                Margin = new Padding(0, 0, btnGap, 0)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnPanel.Controls.Add(okButton);       // RightToLeft：第一个在最右
            btnPanel.Controls.Add(cancelButton);

            // ===== 字段表单 =====
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(pad, pad, pad, DpiScale.V(this, 4))
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 标签列按文字宽度自适应
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            _titleBox = AddField(grid, ref row, "标题：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            _usernameBox = AddField(grid, ref row, "用户名：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);

            _passwordBox = new AntdUI.Input {
                // 等宽语义（终端/密码字符对齐），字号跟随全局 UI 字号
                Font = new Font("Consolas", Gdterm.UI.Program.GlobalAppearance != null ? Gdterm.UI.Program.GlobalAppearance.UIFontSize : 9.5f),
                UseSystemPasswordChar = true
            };
            var pwdCell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
            pwdCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pwdCell.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pwdCell.Controls.Add(_passwordBox, 0, 0);
            var btnShowPwd = new AntdUI.Button {
                Text = "显示",
                AutoSize = true,
                Type = AntdUI.TTypeMini.Default,
                Padding = new Padding(btnShowPad, DpiScale.V(this, 3), btnShowPad, DpiScale.V(this, 3)),
                Margin = new Padding(btnShowPad, btnShowTop, 0, btnShowTop)
            };
            btnShowPwd.Click += (s, e) =>
            {
                _passwordBox.UseSystemPasswordChar = !_passwordBox.UseSystemPasswordChar;
                btnShowPwd.Text = _passwordBox.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            pwdCell.Controls.Add(btnShowPwd, 1, 0);
            AddField(grid, ref row, "密码：", pwdCell, fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);

            _urlBox = AddField(grid, ref row, "URL：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            _groupBox = AddField(grid, ref row, "分组：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            _hostBox = AddField(grid, ref row, "主机：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            _protocolBox = AddField(grid, ref row, "协议：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            if (string.IsNullOrEmpty(_protocolBox.Text)) _protocolBox.Text = "SSH";
            _portBox = AddField(grid, ref row, "端口：", new AntdUI.InputNumber {
                Minimum = 0,
                Maximum = 65535,
                Value = 22
            }, fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            _autoTypeBox = AddField(grid, ref row, "AutoType：", new AntdUI.Input(), fieldH, ctrlPad, labelPadL, labelPadTop, labelPadR);
            _notesBox = new AntdUI.Input {
                Multiline = true,
                // 字驱动的多行高度，随 UI 字号增长而不裁剪文本
                MinimumSize = new Size(0, notesH),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, ctrlPad, 0, ctrlPad)
            };
            AddLabel(grid, row, "备注：", labelPadL, labelPadTop, labelPadR);
            grid.Controls.Add(_notesBox, 1, row);
            row++;

            Controls.Add(grid);
            Controls.Add(btnPanel);   // 后添加的先布局：Bottom 先钉住，Fill 吃剩余空间

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static void AddLabel(TableLayoutPanel grid, int row, string text, int padL, int padTop, int padR)
        {
            grid.Controls.Add(new AntdUI.Label {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(padL, padTop, padR, 0)
            }, 0, row);
        }

        private T AddField<T>(TableLayoutPanel grid, ref int row, string labelText, T control, int fieldH, int pad, int padL, int padTop, int padR) where T : Control
        {
            AddLabel(grid, row, labelText, padL, padTop, padR);
            control.Dock = DockStyle.Fill;
            // 最小高度按 38px 地板（与其余对话框一致），使输入框不被压矮
            control.MinimumSize = new Size(0, fieldH);
            control.Margin = new Padding(0, pad, 0, pad);
            grid.Controls.Add(control, 1, row);
            row++;
            return control;
        }
    }
}
