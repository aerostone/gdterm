using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 凭据选择器——在连接设置中浏览/选择/新建凭据。
    /// 一体化 change 2026-09-26：
    ///   1. 构造可选 seed（新建凭据预填当前连接的主机/用户名/协议/端口，免二次录入）；
    ///   2. 底部新增「管理…」直通密码库管理器（选择器内可编辑/删除后再回来选）；
    ///   3. 已选条目支持回车/双击确认（AcceptButton 原有行为保留）。
    /// </summary>
    public sealed class KeePassEntryPicker : AntdUI.Window
    {
        private readonly IKeePassService _keepass;
        private AntdUI.Input _searchBox;
        private AntdUI.Select _sortCombo;
        private AntdUI.Table _table;
        private EmptyStateView _emptyState;
        private AntdUI.Button _selectButton;
        private System.Collections.Generic.List<KeePassEntrySummary> _rows = new System.Collections.Generic.List<KeePassEntrySummary>();
        private IList<KeePassEntrySummary> _entries;
        private readonly KeePassEntry _seed;

        /// <summary>选中的条目 UUID，未选择返回 null</summary>
        public string SelectedEntryId { get; private set; }

        public KeePassEntryPicker(IKeePassService keepass, KeePassEntry seed = null)
        {
            _keepass = keepass ?? throw new ArgumentNullException(nameof(keepass));
            _seed = seed;
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadEntries();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private void InitializeComponent()
        {
            Text = L10n.T("选择凭据");
            Size = DpiScale.S(this, 520, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            Font = Services.FormFontPolicy.UiFont();

            // 搜索框 + F10 排序列（Dock 布局，随字体/DPI 自适应高度）
            var searchPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = DpiScale.Pad(this, 12, 10, 12, 6),
                BackColor = GdtermColorTable.Background,
                ColumnCount = 2,
                RowCount = 1
            };
            searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            _searchBox = new AntdUI.Input {
                Dock = DockStyle.Fill,
                PlaceholderText = L10n.T("搜索...")
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            // F10 排序列：标题/用户名/分组（会话级选择，不落盘——选择器是瞬态窗）
            _sortCombo = new AntdUI.Select { Dock = DockStyle.Fill };
            _sortCombo.Items.Add(L10n.T("标题"));
            _sortCombo.Items.Add(L10n.T("用户名"));
            _sortCombo.Items.Add(L10n.T("分组"));
            _sortCombo.SelectedIndex = 0;
            _sortCombo.SelectedIndexChanged += (s, e) => ApplyFilter();
            searchPanel.Controls.Add(_searchBox, 0, 0);
            searchPanel.Controls.Add(_sortCombo, 1, 0);

            // 列表
            _table = new AntdUI.Table
            {
                Name = "KeePassPickerTable",
                Dock = DockStyle.Fill,
                BorderWidth = 0,

                RowHeight = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)) // 表格阅读行：24 地板
            };
            _table.Columns.Add(new AntdUI.Column("Title", L10n.T("标题"), AntdUI.ColumnAlign.Left) { Width = "30%" });
            _table.Columns.Add(new AntdUI.Column("Username", L10n.T("用户名"), AntdUI.ColumnAlign.Left) { Width = "30%" });
            _table.Columns.Add(new AntdUI.Column("GroupPath", L10n.T("分组"), AntdUI.ColumnAlign.Left) { Width = "40%" });
            _table.CellClick += (s, e) => UpdateSelectionState();
            _table.CellDoubleClick += (s, e) => SelectEntry();

            // 统一到 EmptyStateView：原来的手工 Panel + 单行居中标签既没有标题层级，
            // 也不带“去管理密码库”的下一步动作。
            _emptyState = EmptyStateView.Attach(this, L10n.T("密码库中没有可选凭据"),
                L10n.T("在「管理…」里新建一条，或取消本次选择"), L10n.T("管理密码库…"), (s2, e2) => OpenManager());

            // 停靠子控件不会可靠撑开 Panel.AutoSize；按钮栏改为表格，取消按钮始终占据可点击空间。
            var btnPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = GdtermColorTable.Surface,
                ColumnCount = 5,
                RowCount = 1,
                Padding = DpiScale.Pad(this, 12, 7, 12, 7)
            };
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var btnNew = new AntdUI.Button {
                Text = L10n.T("新建凭据"),
                Type = AntdUI.TTypeMini.Default,
                Margin = new Padding(0)
            };
            Services.FormFontPolicy.SizeActionButton(this, btnNew, 10, 4);
            btnNew.Click += (s, e) => CreateNewEntry();
            var btnManage = new AntdUI.Button {
                Text = L10n.T("管理凭据…"),
                Type = AntdUI.TTypeMini.Default,
                Margin = new Padding(DpiScale.V(this, 8), 0, 0, 0)
            };
            Services.FormFontPolicy.SizeActionButton(this, btnManage, 10, 4);
            btnManage.Click += (s, e) => OpenManager();
            _selectButton = new AntdUI.Button {
                Text = L10n.T("选择"),
                Type = AntdUI.TTypeMini.Primary,
                Margin = DpiScale.Pad(this, 8, 0, 0, 0),
                Enabled = false
            };
            Services.FormFontPolicy.SizeActionButton(this, _selectButton, 10, 4);
            _selectButton.Click += (s, e) => SelectEntry();
            var btnCancel = new AntdUI.Button {
                Text = L10n.T("取消"),
                Type = AntdUI.TTypeMini.Default,
                Margin = DpiScale.Pad(this, 8, 0, 0, 0)
            };
            Services.FormFontPolicy.SizeActionButton(this, btnCancel, 10, 4);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnPanel.Controls.Add(btnNew, 0, 0);
            btnPanel.Controls.Add(btnManage, 1, 0);
            btnPanel.Controls.Add(btnCancel, 3, 0);
            btnPanel.Controls.Add(_selectButton, 4, 0);

            // Dock 顺序：后添加的先布局——Top 先钉住，Bottom 再钉住，Fill 吃剩余空间
            Controls.Add(_table);
            Controls.Add(_emptyState);
            Controls.Add(btnPanel);
            Controls.Add(searchPanel);
            AcceptButton = _selectButton;
            CancelButton = btnCancel;
        }

        private void LoadEntries()
        {
            try
            {
                _entries = _keepass.ListEntries() ?? new List<KeePassEntrySummary>();
            }
            catch (Exception ex)
            {
                // 密码库没解锁/文件被占用时原来直接抛到 WinForms 未处理异常
                _entries = new List<KeePassEntrySummary>();
                try { DiagLog.Error("KeePassEntryPicker.ListEntries", ex); } catch (Exception) { }
                ToastNotifier.Error(L10n.T("读取密码库失败：") + ex.Message);
                if (_emptyState != null)
                    _emptyState.SetEmpty(true, L10n.T("密码库读取失败"), ex.Message);
            }
            PopulateList(_entries);
        }

        private sealed class EntryRow
        {
            public string Title { get; set; }
            public string Username { get; set; }
            public string GroupPath { get; set; }
        }

        private void PopulateList(IList<KeePassEntrySummary> items)
        {
            _rows.Clear();
            var rows = new List<EntryRow>();
            foreach (var e in items)
            {
                _rows.Add(e);
                rows.Add(new EntryRow
                {
                    Title = e.Title ?? "",
                    Username = e.Username ?? "",
                    GroupPath = e.GroupPath ?? ""
                });
            }
            _table.DataSource = rows;
            bool isEmpty = _rows.Count == 0;
            _table.Visible = !isEmpty;
            if (_emptyState != null)
            {
                if (isEmpty && !string.IsNullOrWhiteSpace(_searchBox.Text))
                    _emptyState.SetEmpty(true, L10n.T("没有匹配的凭据"), L10n.T("调整搜索词，或清空后重试"));
                else if (isEmpty)
                    _emptyState.SetEmpty(true, L10n.T("密码库中没有可选凭据"), L10n.T("在「管理…」里新建一条，或取消本次选择"));
                else
                    _emptyState.SetEmpty(false);
            }
            UpdateSelectionState();
        }

        private void UpdateSelectionState()
        {
            if (_selectButton == null) return;
            // AntdUI Table 行号 1 开始（表头占 0），映射 _rows 须减 1
            int idx = _table.SelectedIndex - 1;
            _selectButton.Enabled = idx >= 0 && idx < _rows.Count;
        }

        private void ApplyFilter()
        {
            var filter = _searchBox.Text?.Trim() ?? "";
            var filtered = string.IsNullOrEmpty(filter)
                ? new List<KeePassEntrySummary>(_entries)
                : _entries.Where(e =>
                    (e.Title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.Username ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.GroupPath ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                ).ToList();
            // F10 排序：按所选字段（不区分大小写），同键保持原序（装饰原下标保证稳定）
            int mode = _sortCombo != null ? _sortCombo.SelectedIndex : 0;
            System.Func<KeePassEntrySummary, string> key;
            switch (mode)
            {
                case 1: key = e => e.Username ?? ""; break;
                case 2: key = e => e.GroupPath ?? ""; break;
                default: key = e => e.Title ?? ""; break;
            }
            var indexed = new List<KeyValuePair<int, KeePassEntrySummary>>(filtered.Count);
            for (int i = 0; i < filtered.Count; i++)
                indexed.Add(new KeyValuePair<int, KeePassEntrySummary>(i, filtered[i]));
            indexed.Sort((a, b) =>
            {
                int c = string.Compare(key(a.Value), key(b.Value), StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            var sorted = new List<KeePassEntrySummary>(indexed.Count);
            foreach (var kv in indexed) sorted.Add(kv.Value);
            PopulateList(sorted);
        }

        private void SelectEntry()
        {
            // AntdUI Table 行号 1 开始（表头占 0），映射 _rows 须减 1
            var idx = _table.SelectedIndex - 1;
            if (idx >= 0 && idx < _rows.Count)
            {
                SelectedEntryId = _rows[idx].Id;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                UpdateSelectionState();
            }
        }

        private void CreateNewEntry()
        {
            using (var dlg = new KeePassEntryEditForm())
            {
                // 一体化 change：有 seed 时预填（当前连接的主机/用户名/协议/端口/分组/URL），
                // 用户只需补密码——不再是空白表单二次录入。
                if (_seed != null) dlg.LoadFrom(_seed);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var entry = new KeePassEntry
                    {
                        Title = dlg.EntryTitle,
                        Username = dlg.EntryUsername,
                        Password = dlg.EntryPassword,
                        Url = dlg.EntryUrl,
                        GroupPath = dlg.EntryGroupPath,
                        Hostname = dlg.EntryHostname,
                        Port = dlg.EntryPort,
                        Protocol = dlg.EntryProtocol,
                        AutoTypeSequence = dlg.EntryAutoType,
                        Notes = dlg.EntryNotes
                    };
                    try
                    {
                        if (!KeePassPasswordWarning.ConfirmSaveIfWeak(this, _keepass, entry.Password))
                            return; // 用户取消，保持在 picker 界面
                        var created = _keepass.CreateEntry(entry);
                        if (created != null)
                        {
                            SelectedEntryId = created.Id;
                            DialogResult = DialogResult.OK;
                            Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        AntdUI.Message.error(this, L10n.T("创建凭据失败: ") + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// 直通密码库管理器（模态）：编辑/删除后回来重载列表，之前选中的条目若仍存在则保持选中。
        /// </summary>
        private void OpenManager()
        {
            try
            {
                using (var mgr = new KeePassManagerForm(_keepass))
                {
                    mgr.ShowDialog(this);
                }
                var keep = SelectedEntryId;
                LoadEntries();
                ApplyFilter();
                // 管理器里删了原选中条目时清空选中，避免“选中一个已消失的 UUID”
                if (keep != null && _entries != null && !_entries.Any(e => e.Id == keep))
                    UpdateSelectionState();
            }
            catch (Exception ex)
            {
                AntdUI.Message.error(this, L10n.T("打开密码库管理器失败: ") + ex.Message);
            }
        }
    }
}
