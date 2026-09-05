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
        private TextBox _searchBox;
        private ListView _listView;
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
            var searchHint = new Label
            {
                Text = "搜索...",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent
            };
            _searchBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.FixedSingle
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            _searchBox.Enter += (s, e) => { searchHint.Visible = false; };
            _searchBox.Leave += (s, e) => { searchHint.Visible = string.IsNullOrEmpty(_searchBox.Text); };
            // 先加的在上层：提示文字覆盖在空文本框上方
            searchPanel.Controls.Add(searchHint);
            searchPanel.Controls.Add(_searchBox);

            // 列表
            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = true,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = GdtermColorTable.Foreground,
                BorderStyle = BorderStyle.FixedSingle,
                OwnerDraw = true
            };
            _listView.Columns.Add("标题", 180);
            _listView.Columns.Add("用户名", 120);
            _listView.Columns.Add("分组", 160);
            _listView.DoubleClick += (s, e) => SelectEntry();
            _listView.DrawColumnHeader += (s, e) =>
            {
                e.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(45, 45, 45)), e.Bounds);
                // 表头字体跟随窗体当前字体（硬编码 9f 在 11pt 全局下不协调）
                using (var headerFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, e.Header.Text, headerFont,
                        e.Bounds, GdtermColorTable.Foreground,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            };
            _listView.DrawItem += (s, e) =>
            {
                e.DrawDefault = false;
                var bg = e.Item.Selected
                    ? new SolidBrush(GdtermColorTable.Accent)
                    : new SolidBrush(e.ItemIndex % 2 == 0
                        ? Color.FromArgb(37, 37, 38)
                        : Color.FromArgb(42, 42, 43));
                e.Graphics.FillRectangle(bg, e.Bounds);
                for (int i = 0; i < _listView.Columns.Count; i++)
                {
                    var bounds = e.Item.SubItems[i].Bounds;
                    var text = e.Item.SubItems[i].Text;
                    TextRenderer.DrawText(e.Graphics, text, Font,
                        bounds, e.Item.Selected ? Color.White : GdtermColorTable.Foreground,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            };

            // ===== 底部按钮（流式布局，随字体/DPI 自适应）=====
            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 45,
                BackColor = Color.FromArgb(37, 37, 38)
            };
            var btnNew = new Button
            {
                Text = "新建凭据",
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = GdtermColorTable.Hover,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(12, 7, 0, 0)
            };
            btnNew.Click += (s, e) => CreateNewEntry();
            var btnNewFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(37, 37, 38),
                AutoSize = true
            };
            btnNewFlow.Controls.Add(btnNew);

            var btnSelectFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.FromArgb(37, 37, 38),
                AutoSize = true,
                Padding = new Padding(0, 0, 12, 0)
            };
            var btnSelect = new Button
            {
                Text = "选择",
                DialogResult = DialogResult.OK,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = GdtermColorTable.Accent,
                ForeColor = Color.White,
                Margin = new Padding(8, 7, 0, 0)
            };
            btnSelect.Click += (s, e) => SelectEntry();
            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = GdtermColorTable.Hover,
                ForeColor = GdtermColorTable.Foreground,
                Margin = new Padding(0, 7, 8, 0)
            };
            btnSelectFlow.Controls.Add(btnCancel);   // RightToLeft：第一个在最右
            btnSelectFlow.Controls.Add(btnSelect);

            btnPanel.Controls.Add(btnSelectFlow);   // 后添加的先布局：右、左互不重叠
            btnPanel.Controls.Add(btnNewFlow);

            // Dock 顺序：后添加的先布局——Top 先钉住，Bottom 再钉住，Fill 吃剩余空间
            Controls.Add(_listView);
            Controls.Add(btnPanel);
            Controls.Add(searchPanel);
        }

        private void LoadEntries()
        {
            _entries = _keepass.ListEntries() ?? new List<KeePassEntrySummary>();
            PopulateList(_entries);
        }

        private void PopulateList(IList<KeePassEntrySummary> items)
        {
            _listView.BeginUpdate();
            _listView.Items.Clear();
            foreach (var e in items)
            {
                var item = new ListViewItem(e.Title ?? "");
                item.SubItems.Add(e.Username ?? "");
                item.SubItems.Add(e.GroupPath ?? "");
                item.Tag = e.Id;
                _listView.Items.Add(item);
            }
            _listView.EndUpdate();
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
            if (_listView.SelectedItems.Count > 0)
            {
                SelectedEntryId = _listView.SelectedItems[0].Tag as string;
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
                        MessageBox.Show("创建凭据失败: " + ex.Message, "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
    }
}