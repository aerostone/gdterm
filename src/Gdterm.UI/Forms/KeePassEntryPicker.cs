using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 凭据选择器——在连接设置中浏览/选择/新建凭据
    /// </summary>
    public sealed class KeePassEntryPicker : Form
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
            LoadEntries();
        }

        private void InitializeComponent()
        {
            Text = "选择凭据";
            Size = new Size(520, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 30, 30);
            Font = new Font("Microsoft YaHei", 9f);

            // 搜索框
            _searchBox = new TextBox
            {
                Location = new Point(12, 12),
                Size = new Size(300, 22),
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            Controls.Add(_searchBox);

            var searchHint = new Label
            {
                Text = "搜索...",
                Location = new Point(16, 14),
                ForeColor = Color.FromArgb(100, 100, 100),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            _searchBox.Enter += (s, e) => { searchHint.Visible = false; };
            _searchBox.Leave += (s, e) => { searchHint.Visible = string.IsNullOrEmpty(_searchBox.Text); };
            Controls.Add(searchHint);

            // 列表
            _listView = new ListView
            {
                Location = new Point(12, 42),
                Size = new Size(480, 290),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = true,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.FromArgb(204, 204, 204),
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
                using (var headerFont = new Font("Microsoft YaHei", 9f, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, e.Header.Text, headerFont,
                        e.Bounds, Color.FromArgb(204, 204, 204),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            };
            _listView.DrawItem += (s, e) =>
            {
                e.DrawDefault = false;
                var bg = e.Item.Selected
                    ? new SolidBrush(Color.FromArgb(0, 122, 204))
                    : new SolidBrush(e.ItemIndex % 2 == 0
                        ? Color.FromArgb(37, 37, 38)
                        : Color.FromArgb(42, 42, 43));
                e.Graphics.FillRectangle(bg, e.Bounds);
                for (int i = 0; i < _listView.Columns.Count; i++)
                {
                    var bounds = e.Item.SubItems[i].Bounds;
                    var text = e.Item.SubItems[i].Text;
                    TextRenderer.DrawText(e.Graphics, text, Font,
                        bounds, e.Item.Selected ? Color.White : Color.FromArgb(204, 204, 204),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            };
            Controls.Add(_listView);

            // 按钮
            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 45,
                BackColor = Color.FromArgb(37, 37, 38)
            };

            var btnNew = new Button
            {
                Text = "新建凭据",
                Size = new Size(90, 30),
                Location = new Point(12, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204)
            };
            btnNew.Click += (s, e) => CreateNewEntry();

            var btnSelect = new Button
            {
                Text = "选择",
                Size = new Size(80, 30),
                Location = new Point(Width - 210, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            btnSelect.Click += (s, e) => SelectEntry();

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 30),
                Location = new Point(Width - 120, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204)
            };

            btnPanel.Controls.AddRange(new Control[] { btnNew, btnSelect, btnCancel });
            Controls.Add(btnPanel);
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