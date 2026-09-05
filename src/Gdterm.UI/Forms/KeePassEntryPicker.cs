using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 凭据选择器——在连接设置中浏览/选择/新建凭据
    /// </summary>
    public sealed class KeePassEntryPicker : AntdUI.Window
    {
        private readonly IKeePassService _keepass;
        private AntdUI.Input _searchBox;
        private AntdUI.Table _table;
        private System.Collections.Generic.List<KeePassEntrySummary> _rows = new System.Collections.Generic.List<KeePassEntrySummary>();
        private IList<KeePassEntrySummary> _entries;

        /// <summary>选中的条目 UUID，未选择返回 null</summary>
        public string SelectedEntryId { get; private set; }

        public KeePassEntryPicker(IKeePassService keepass)
        {
            _keepass = keepass ?? throw new ArgumentNullException(nameof(keepass));
            InitializeComponent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadEntries();
        }

        private void InitializeComponent()
        {
            Text = "选择凭据";
            Size = DpiScale.S(this, 520, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            Font = Services.FormFontPolicy.UiFont();

            // 搜索框（Dock 布局，随字体/DPI 自适应高度）
            var searchPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(12, 10, 12, 6),
                BackColor = GdtermColorTable.Background
            };
            _searchBox = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                PlaceholderText = "搜索..."
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            searchPanel.Controls.Add(_searchBox);

            // 列表
            _table = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                BorderWidth = 0,
                GridLines = true,
                RowHeight = 28
            };
            _table.Columns.Add(new AntdUI.Column("Title", "标题", AntdUI.ColumnAlign.Left));
            _table.Columns.Add(new AntdUI.Column("Username", "用户名", AntdUI.ColumnAlign.Left));
            _table.Columns.Add(new AntdUI.Column("GroupPath", "分组", AntdUI.ColumnAlign.Left));
            _table.CellDoubleClick += (s, e) => SelectEntry();

            // ===== 底部按钮（流式布局，随字体/DPI 自适应）=====
            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 45,
                BackColor = GdtermColorTable.Surface
            };
            var btnNew = new AntdUI.Button
            {
                Text = "新建凭据",
                Type = AntdUI.TTypeMini.Default,
                AutoSize = true,
                Margin = new Padding(12, 7, 0, 0)
            };
            btnNew.Click += (s, e) => CreateNewEntry();
            var btnNewFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                AutoSize = true
            };
            btnNewFlow.Controls.Add(btnNew);

            var btnSelectFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                AutoSize = true,
                Padding = new Padding(0, 0, 12, 0)
            };
            var btnSelect = new AntdUI.Button
            {
                Text = "选择",
                Type = AntdUI.TTypeMini.Primary,
                AutoSize = true,
                Margin = new Padding(8, 7, 0, 0)
            };
            btnSelect.Click += (s, e) => SelectEntry();
            var btnCancel = new AntdUI.Button
            {
                Text = "取消",
                Type = AntdUI.TTypeMini.Default,
                AutoSize = true,
                Margin = new Padding(0, 7, 8, 0)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnSelectFlow.Controls.Add(btnCancel);   // RightToLeft：第一个在最右
            btnSelectFlow.Controls.Add(btnSelect);

            btnPanel.Controls.Add(btnSelectFlow);   // 后添加的先布局：右、左互不重叠
            btnPanel.Controls.Add(btnNewFlow);

            // Dock 顺序：后添加的先布局——Top 先钉住，Bottom 再钉住，Fill 吃剩余空间
            Controls.Add(_table);
            Controls.Add(btnPanel);
            Controls.Add(searchPanel);
        }

        private void LoadEntries()
        {
            _entries = _keepass.ListEntries() ?? new List<KeePassEntrySummary>();
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
            var rows = new AntdUI.AntList<EntryRow>();
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
        }

        private void ApplyFilter()
        {
            var filter = _searchBox.Text?.Trim() ?? "";
            var filtered = string.IsNullOrEmpty(filter)
                ? _entries
                : _entries.Where(e =>
                    (e.Title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.Username ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.GroupPath ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                ).ToList();
            PopulateList(filtered);
        }

        private void SelectEntry()
        {
            var idx = _table.SelectedIndex;
            if (idx >= 0 && idx < _rows.Count)
            {
                SelectedEntryId = _rows[idx].Id;
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void CreateNewEntry()
        {
            using (var dlg = new KeePassEntryEditForm())
            {
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
                        AntdUI.Message.error(this, "创建凭据失败: " + ex.Message);
                    }
                }
            }
        }
    }
}