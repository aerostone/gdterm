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
    public class KeePassManagerForm : AntdUI.Window
    {
        private readonly IKeePassService _keepassService;
        private AntdUI.Table _entryTable;
        private AntdUI.Label _statusLabel;
        private System.Collections.Generic.List<KeePassEntrySummary> _entries = new System.Collections.Generic.List<KeePassEntrySummary>();

        public KeePassManagerForm(IKeePassService keepassService)
        {
            _keepassService = keepassService;
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

            // 工具行（AntdUI.Button 流式靠右）
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Height = 46,
                Padding = new Padding(8, 5, 8, 5)
            };

            toolbar.Controls.Add(MakeToolBtn("添加", OnAddClick));
            toolbar.Controls.Add(MakeToolBtn("编辑", OnEditClick));
            toolbar.Controls.Add(MakeToolBtn("删除", OnDeleteClick));
            toolbar.Controls.Add(new AntdUI.Divider { Orientation = AntdUI.TOrientation.Left, Thickness = 1f, Margin = new Padding(4) });
            toolbar.Controls.Add(MakeToolBtn("复制密码", OnCopyPasswordClick));
            toolbar.Controls.Add(MakeToolBtn("复制用户名", OnCopyUsernameClick));
            toolbar.Controls.Add(new AntdUI.Divider { Orientation = AntdUI.TOrientation.Left, Thickness = 1f, Margin = new Padding(4) });
            toolbar.Controls.Add(MakeToolBtn("刷新", (s, e) => LoadEntries()));

            // 条目表（AntdUI.Table）
            _entryTable = new AntdUI.Table
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BorderWidth = 0,

                RowHeight = 30
            };
            _entryTable.Columns.Add(new AntdUI.Column("Title", "标题", AntdUI.ColumnAlign.Left));
            _entryTable.Columns.Add(new AntdUI.Column("Username", "用户名", AntdUI.ColumnAlign.Left));
            _entryTable.Columns.Add(new AntdUI.Column("GroupPath", "分组路径", AntdUI.ColumnAlign.Left));
            _entryTable.Columns.Add(new AntdUI.Column("Url", "URL", AntdUI.ColumnAlign.Left));
            _entryTable.Columns.Add(new AntdUI.Column("Modified", "最后修改", AntdUI.ColumnAlign.Left));
            _entryTable.CellClick += OnEntryCellClick;
            _entryTable.CellDoubleClick += OnEntryCellClick;   // 双击=复制密码

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                Height = 28,
                Text = "就绪"
            };

            Controls.Add(_entryTable);
            Controls.Add(toolbar);
            Controls.Add(_statusLabel);
        }

        private static AntdUI.Button MakeToolBtn(string text, EventHandler onClick)
        {
            var btn = new AntdUI.Button { Text = text, Type = AntdUI.TTypeMini.Default, Ghost = true, Size = new Size(88, 34) };
            btn.Click += onClick;
            return btn;
        }

        /// <summary>AntdUI.Table 单元格点击：取行数据条目 Id 执行复制密码。</summary>
        private void OnEntryCellClick(object sender, AntdUI.TableClickEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _entries.Count) return;
            CopyEntryPassword(_entries[e.RowIndex].Id);
        }

        private sealed class EntryRow
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Username { get; set; }
            public string GroupPath { get; set; }
            public string Url { get; set; }
            public string Modified { get; set; }
        }

        private void LoadEntries()
        {
            try
            {
                var entries = _keepassService.ListEntries() ?? new System.Collections.Generic.List<KeePassEntrySummary>();
                _entries.Clear();
                var rows = new AntdUI.AntList<EntryRow>();
                foreach (var entry in entries)
                {
                    var title = entry.Title ?? "(无标题)";
                    if (entry.HasSshPrivateKey) title = "🔑 " + title;
                    _entries.Add(entry);
                    rows.Add(new EntryRow
                    {
                        Id = entry.Id,
                        Title = title,
                        Username = entry.Username ?? "",
                        GroupPath = entry.GroupPath ?? "",
                        Url = entry.Url ?? "",
                        Modified = entry.LastModified > DateTime.MinValue
                            ? entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                            : ""
                    });
                }
                _entryTable.DataSource = rows;
                _statusLabel.Text = $"共 {rows.Count} 个条目";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载失败：{ex.Message}";
            }
        }

        /// <summary>取当前选中（或最后点击）的条目 Id；无选中返回 null。</summary>
        private string SelectedEntryId()
        {
            if (_entryTable.SelectedIndex >= 0 && _entryTable.SelectedIndex < _entries.Count)
                return _entries[_entryTable.SelectedIndex].Id;
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
                        try { ToastNotifier.Success("凭据已创建"); } catch { }
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
                        try { ToastNotifier.Success("凭据已保存：" + full.Title); } catch { }
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
            var row = _entries.Find(x => x.Id == entryId);
            var title = row != null ? (row.Title ?? "(无标题)") : entryId;

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
            var row = _entries.Find(x => x.Id == entryId);
            var username = row != null ? (row.Username ?? "") : "";

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
            ClientSize = DpiScale.S(this, 420, 470);
            // 跟随字体/DPI 自动整体缩放（绝对定位在 11pt@144dpi 下会重叠/溢出）,
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;

            // ===== 底部按钮（流式靠右，随字体缩放）=====
            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = GdtermColorTable.Background };
            var btnFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, 7, 15, 0)
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
                Margin = new Padding(0, 0, 8, 0)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnFlow.Controls.Add(okButton);       // RightToLeft：第一个在最右
            btnFlow.Controls.Add(cancelButton);
            btnPanel.Controls.Add(btnFlow);

            // ===== 字段表单 =====
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(12, 12, 12, 4)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 标签列按文字宽度自适应
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            _titleBox = AddField(grid, ref row, "标题：", new AntdUI.Input());
            _usernameBox = AddField(grid, ref row, "用户名：", new AntdUI.Input());

            _passwordBox = new AntdUI.Input {
                Font = new Font("Consolas", 9.5f),   // 等宽语义,
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
                Margin = new Padding(6, 1, 0, 1)
            };
            btnShowPwd.Click += (s, e) =>
            {
                _passwordBox.UseSystemPasswordChar = !_passwordBox.UseSystemPasswordChar;
                btnShowPwd.Text = _passwordBox.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            pwdCell.Controls.Add(btnShowPwd, 1, 0);
            AddField(grid, ref row, "密码：", pwdCell);

            _urlBox = AddField(grid, ref row, "URL：", new AntdUI.Input());
            _groupBox = AddField(grid, ref row, "分组：", new AntdUI.Input());
            _hostBox = AddField(grid, ref row, "主机：", new AntdUI.Input());
            _protocolBox = AddField(grid, ref row, "协议：", new AntdUI.Input());
            if (string.IsNullOrEmpty(_protocolBox.Text)) _protocolBox.Text = "SSH";
            _portBox = AddField(grid, ref row, "端口：", new AntdUI.InputNumber {
                Minimum = 0,
                Maximum = 65535,
                Value = 22
            });
            _autoTypeBox = AddField(grid, ref row, "AutoType：", new AntdUI.Input());
            _notesBox = new AntdUI.Input {
                Multiline = true,
                Height = 64,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 0, 4)
            };
            AddLabel(grid, row, "备注：");
            grid.Controls.Add(_notesBox, 1, row);
            row++;

            Controls.Add(grid);
            Controls.Add(btnPanel);   // 后添加的先布局：Bottom 先钉住，Fill 吃剩余空间

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static void AddLabel(TableLayoutPanel grid, int row, string text)
        {
            grid.Controls.Add(new AntdUI.Label {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 8, 0)
            }, 0, row);
        }

        private T AddField<T>(TableLayoutPanel grid, ref int row, string labelText, T control) where T : Control
        {
            AddLabel(grid, row, labelText);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(control, 1, row);
            row++;
            return control;
        }
    }
}
