using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// KeePass 密码管理器对话框
    /// 显示密码条目列表，支持增删改查、复制密码/用户名
    /// </summary>
    public class KeePassManagerForm : Form
    {
        private readonly IKeePassService _keepassService;
        private ListView _entryList;
        private ToolStrip _toolbar;
        private Label _statusLabel;

        public KeePassManagerForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            Gdterm.UI.Services.FormFontPolicy.Apply(this);
            LoadEntries();
        }

        private void InitializeComponent()
        {
            Text = "KeePass 密码管理器";
            Size = new Size(700, 500);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(30, 30, 30);

            // 工具栏
            _toolbar = new ToolStrip
            {
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.FromArgb(204, 204, 204),
                GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new DarkToolStripRenderer(),
                Font = new Font("Microsoft YaHei", 9f),
                Padding = new Padding(5, 2, 5, 2)
            };

            var btnAdd = new ToolStripButton("添加");
            btnAdd.Click += OnAddClick;
            _toolbar.Items.Add(btnAdd);

            var btnEdit = new ToolStripButton("编辑");
            btnEdit.Click += OnEditClick;
            _toolbar.Items.Add(btnEdit);

            var btnDelete = new ToolStripButton("删除");
            btnDelete.Click += OnDeleteClick;
            _toolbar.Items.Add(btnDelete);

            _toolbar.Items.Add(new ToolStripSeparator());

            var btnCopyPassword = new ToolStripButton("复制密码");
            btnCopyPassword.Click += OnCopyPasswordClick;
            _toolbar.Items.Add(btnCopyPassword);

            var btnCopyUsername = new ToolStripButton("复制用户名");
            btnCopyUsername.Click += OnCopyUsernameClick;
            _toolbar.Items.Add(btnCopyUsername);

            _toolbar.Items.Add(new ToolStripSeparator());

            var btnRefresh = new ToolStripButton("刷新");
            btnRefresh.Click += (s, e) => LoadEntries();
            _toolbar.Items.Add(btnRefresh);

            // 列表
            _entryList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None
            };

            _entryList.Columns.Add("标题", 180);
            _entryList.Columns.Add("用户名", 120);
            _entryList.Columns.Add("分组路径", 150);
            _entryList.Columns.Add("URL", 150);
            _entryList.Columns.Add("最后修改", 120);

            _entryList.DoubleClick += OnCopyPasswordClick;

            // 状态栏
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = new Font("Microsoft YaHei", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Text = "就绪"
            };

            Controls.Add(_entryList);
            Controls.Add(_toolbar);
            Controls.Add(_statusLabel);
        }

        private void LoadEntries()
        {
            _entryList.Items.Clear();
            try
            {
                var entries = _keepassService.ListEntries();
                foreach (var entry in entries)
                {
                    var title = entry.Title ?? "(无标题)";
                    if (entry.HasSshPrivateKey) title = "🔑 " + title;
                    var item = new ListViewItem(title);
                    item.SubItems.Add(entry.Username ?? "");
                    item.SubItems.Add(entry.GroupPath ?? "");
                    item.SubItems.Add(entry.Url ?? "");
                    item.SubItems.Add(entry.LastModified > DateTime.MinValue
                        ? entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                        : "");
                    item.Tag = entry.Id;
                    _entryList.Items.Add(item);
                }
                _statusLabel.Text = $"共 {entries.Count} 个条目";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载失败：{ex.Message}";
            }
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
                        try { ToastNotifier.Success("凭据已创建"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"创建失败：{ex.Message}", "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OnEditClick(object sender, EventArgs e)
        {
            if (_entryList.SelectedItems.Count == 0) return;

            var selected = _entryList.SelectedItems[0];
            var entryId = (string)selected.Tag;

            try
            {
                var full = _keepassService.GetEntry(entryId);
                if (full == null)
                {
                    MessageBox.Show(this, "无法加载条目详情（可能已删除或库已锁定）。",
                        "编辑", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                        try { ToastNotifier.Success("凭据已保存：" + full.Title); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "编辑失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDeleteClick(object sender, EventArgs e)
        {
            if (_entryList.SelectedItems.Count == 0) return;

            var selected = _entryList.SelectedItems[0];
            var entryId = (string)selected.Tag;
            var title = selected.Text;

            var confirm = MessageBox.Show(this,
                $"确定要删除条目 \"{title}\" 吗？\n此操作不可撤销。",
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
                    try { ToastNotifier.Warning("已删除：" + title); } catch { }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"删除失败：{ex.Message}", "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnCopyPasswordClick(object sender, EventArgs e)
        {
            if (_entryList.SelectedItems.Count == 0) return;

            var selected = _entryList.SelectedItems[0];
            var entryId = (string)selected.Tag;

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
            if (_entryList.SelectedItems.Count == 0) return;

            var selected = _entryList.SelectedItems[0];
            var username = selected.SubItems[1].Text;

            if (!string.IsNullOrEmpty(username))
            {
                try
                {
                    Clipboard.SetText(username);
                    _statusLabel.Text = "用户名已复制到剪贴板";
                }
                catch { }
            }
            else
            {
                _statusLabel.Text = "该条目没有用户名";
            }
        }

        /// <summary>
        /// 深色工具栏渲染器
        /// </summary>
        private class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var brush = new SolidBrush(Color.FromArgb(45, 45, 45)))
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }

            protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected || e.Item.Pressed)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(60, 60, 60)))
                        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                }
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var y = e.Item.Height / 2;
                using (var pen = new Pen(Color.FromArgb(60, 60, 60)))
                    e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Color.FromArgb(204, 204, 204);
                base.OnRenderItemText(e);
            }
        }
    }

    /// <summary>
    /// 条目编辑对话框（用于添加/编辑 KeePass 条目）
    /// </summary>
    internal class KeePassEntryEditForm : Form
    {
        private TextBox _titleBox;
        private TextBox _usernameBox;
        private TextBox _passwordBox;
        private TextBox _urlBox;
        private TextBox _notesBox;
        private TextBox _groupBox;

        private TextBox _hostBox;
        private NumericUpDown _portBox;
        private TextBox _protocolBox;
        private TextBox _autoTypeBox;

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
            Text = "编辑密码条目";
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
            Size = new Size(420, 380);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 30, 30);

            int y = 15;
            int labelW = 70;
            int boxX = 90;
            int boxW = 300;

            _titleBox = AddField("标题：", ref y, labelW, boxX, boxW);
            _usernameBox = AddField("用户名：", ref y, labelW, boxX, boxW);

            var pwdLabel = new Label
            {
                Text = "密码：",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(15, y),
                Size = new Size(labelW, 22)
            };
            _passwordBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW - 70, 22),
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = true
            };
            var btnShowPwd = new Button
            {
                Text = "显示",
                Location = new Point(boxX + boxW - 65, y - 1),
                Size = new Size(55, 24),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 8f),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.FromArgb(204, 204, 204)
            };
            btnShowPwd.Click += (s, e) =>
            {
                _passwordBox.UseSystemPasswordChar = !_passwordBox.UseSystemPasswordChar;
                btnShowPwd.Text = _passwordBox.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            Controls.Add(pwdLabel);
            Controls.Add(_passwordBox);
            Controls.Add(btnShowPwd);
            y += 30;

            _urlBox = AddField("URL：", ref y, labelW, boxX, boxW);
            _groupBox = AddField("分组：", ref y, labelW, boxX, boxW);
            _hostBox = AddField("主机：", ref y, labelW, boxX, boxW);
            _protocolBox = AddField("协议：", ref y, labelW, boxX, boxW);
            if (string.IsNullOrEmpty(_protocolBox.Text)) _protocolBox.Text = "SSH";

            var portLabel = new Label
            {
                Text = "端口：",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(15, y),
                Size = new Size(labelW, 22)
            };
            _portBox = new NumericUpDown
            {
                Location = new Point(boxX, y),
                Size = new Size(100, 22),
                Minimum = 0,
                Maximum = 65535,
                Value = 22,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204)
            };
            Controls.Add(portLabel);
            Controls.Add(_portBox);
            y += 30;

            _autoTypeBox = AddField("AutoType：", ref y, labelW, boxX, boxW);

            // 放大窗体以容纳新字段
            try { Size = new Size(420, 520); } catch { }

            var notesLabel = new Label
            {
                Text = "备注：",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(15, y),
                Size = new Size(labelW, 22)
            };
            _notesBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW, 60),
                Font = new Font("Microsoft YaHei", 9f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical
            };
            Controls.Add(notesLabel);
            Controls.Add(_notesBox);
            y += 75;

            // 按钮
            var okButton = new Button
            {
                Text = "确定",
                Size = new Size(80, 30),
                Location = new Point(boxX + boxW - 170, y),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9f),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "取消",
                Size = new Size(80, 30),
                Location = new Point(boxX + boxW - 80, y),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9f),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(okButton);
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private TextBox AddField(string labelText, ref int y, int labelW, int boxX, int boxW)
        {
            var label = new Label
            {
                Text = labelText,
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(15, y),
                Size = new Size(labelW, 22)
            };

            var textBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW, 22),
                Font = new Font("Microsoft YaHei", 9f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle
            };

            Controls.Add(label);
            Controls.Add(textBox);
            y += 30;
            return textBox;
        }
    }
}
