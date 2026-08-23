using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 连接面板（TreeView + 右键菜单 + 图标）
    /// </summary>
    public class ConnectionTreeControl : UserControl
    {
        private readonly IConnectionStore _connectionStore;
        private readonly IKeePassService _keepass;
        private TreeView _treeView;
        private ContextMenuStrip _contextMenu;
        private ImageList _imageList;
        // 右键点中的节点（ContextMenuStrip.Opening 读的），后后菜单设置选中
        private TreeNode _rightClickedNode;
        // 顶部搜索框（参考 Xshell/SecureCRT Session Manager filter bar）
        private TextBox _filterBox;
        // 所有连接的原始列表（筛选时从它重建树）
        private List<ConnectionConfig> _allConnections;
        // auto-hide 状态：true=固定展开（默认）；false=收为窄边，悬停展开。
        private bool _pinned = true;
        private int _pinnedWidth = 250;
        private const int CollapsedWidth = 18;

        public event EventHandler<ConnectionConfig> ConnectionDoubleClicked;
        public event EventHandler ConnectionListChanged;
        /// <summary>右键菜单“本地终端”被点击时触发（由 MainForm 订阅 → TabContainer.OpenLocalTerminal）。</summary>
        public event Action OpenLocalTerminalRequested;

        public ConnectionTreeControl(IConnectionStore connectionStore, IKeePassService keepass = null)
        {
            _connectionStore = connectionStore;
            _keepass = keepass;
            // collapsed 窄边上画 pin 图标作为展开提示
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            InitializeComponent();
            LoadConnections();
        }

        private void InitializeComponent()
        {
            BuildImageList();

            // 顶部筛选框（输即过滤）-- Xshell/SecureCRT filter bar 风格。
            _filterBox = new TextBox
            {
                Dock = DockStyle.Top,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Surface,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground,
                Font = ResolveDefaultFont(),
                Height = 24
            };
            try { Gdterm.UI.Diagnostics.WinFormsCompat.SetCueBanner(_filterBox, "输入主机/名称/分组过滤…"); }
            catch { }
            _filterBox.TextChanged += (s, e) => ApplyFilter(_filterBox.Text);

            _treeView = new TreeView
            {
                Dock = DockStyle.Fill,
                ImageList = _imageList,
                BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background,
                ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground,
                Font = ResolveDefaultFont(),
                BorderStyle = BorderStyle.None,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HotTracking = true,
                FullRowSelect = true
            };
            _treeView.NodeMouseDoubleClick += OnNodeMouseDoubleClick;
            _treeView.NodeMouseClick += OnNodeMouseClick;
            _treeView.MouseDown += OnMouseDownSelectRightClick;
            // 拖放：把连接节点拖到分组节点上移动 GroupPath
            _treeView.AllowDrop = true;
            _treeView.ItemDrag += OnItemDrag;
            _treeView.DragOver += OnDragOver;
            _treeView.DragDrop += OnDragDrop;
            // WinForms Dock：Fill 要先加，Top 后加才能占位
            Controls.Add(_treeView);
            Controls.Add(_filterBox);

            // 右键菜单——在 Opening 中按右键节点动态调整项，避免“主机上右键也弹新建”
            // 顺序按使用频率：连接/复制地址（主机节点专属）→ 本地终端 → 新建/编辑/删除
            _contextMenu = new ContextMenuStrip();

            var itemConnect = new ToolStripMenuItem("连接(&C)", null, OnConnect);
            itemConnect.Tag = "connect";
            _contextMenu.Items.Add(itemConnect);

            var itemCopyHost = new ToolStripMenuItem("复制主机地址(&O)", null, OnCopyHost);
            itemCopyHost.Tag = "copyhost";
            _contextMenu.Items.Add(itemCopyHost);

            _contextMenu.Items.Add(new ToolStripSeparator { Tag = "sep0" });

            var itemLocal = new ToolStripMenuItem("本地终端(&L)", null, OnOpenLocalTerminal);
            itemLocal.Tag = "local";
            _contextMenu.Items.Add(itemLocal);

            var itemNew = new ToolStripMenuItem("新建连接(&N)", null, OnNewConnection);
            itemNew.Tag = "new";
            _contextMenu.Items.Add(itemNew);

            var itemEdit = new ToolStripMenuItem("编辑(&E)", null, OnEditConnection);
            itemEdit.Tag = "edit";
            _contextMenu.Items.Add(itemEdit);

            _contextMenu.Items.Add(new ToolStripSeparator { Tag = "sep1" });

            var itemDelete = new ToolStripMenuItem("删除(&D)", null, OnDeleteConnection);
            itemDelete.Tag = "delete";
            _contextMenu.Items.Add(itemDelete);

            _contextMenu.Opening += OnContextMenuOpening;
            _treeView.ContextMenuStrip = _contextMenu;
        }

        /// <summary>
        /// 构建程序化图标列表（无需外部 .ico 文件）
        /// </summary>
        private void BuildImageList()
        {
            _imageList = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            var names = new[] { "folder", "ssh", "rdp", "serial", "server", "group" };
            foreach (var name in names)
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    DrawIcon(g, name);
                }
                _imageList.Images.Add(name, bmp);
            }
        }

        private void DrawIcon(Graphics g, string type)
        {
            switch (type)
            {
                case "folder":
                    using (var brush = new SolidBrush(Color.FromArgb(255, 193, 7)))
                    {
                        g.FillRectangle(brush, 1, 5, 14, 10);
                        g.FillRectangle(brush, 1, 3, 6, 3);
                    }
                    break;
                case "ssh":
                    // Terminal icon with >_
                    using (var bg = new SolidBrush(Color.FromArgb(40, 120, 80)))
                        g.FillRectangle(bg, 1, 1, 14, 14);
                    using (var pen = new Pen(Color.FromArgb(100, 255, 100), 1.5f))
                    {
                        g.DrawLine(pen, 4, 6, 8, 6);
                        g.DrawLine(pen, 8, 6, 6, 9);
                        g.DrawLine(pen, 6, 9, 4, 9);
                        g.DrawLine(pen, 10, 6, 12, 6);
                    }
                    break;
                case "rdp":
                    // Monitor icon
                    using (var bg = new SolidBrush(Color.FromArgb(0, 120, 215)))
                        g.FillRectangle(bg, 2, 2, 12, 9);
                    using (var pen = new Pen(Color.FromArgb(0, 120, 215), 1.5f))
                    {
                        g.DrawLine(pen, 5, 12, 11, 12);
                        g.DrawLine(pen, 8, 11, 8, 13);
                    }
                    using (var fg = new SolidBrush(Color.White))
                        g.FillRectangle(fg, 5, 4, 6, 5);
                    break;
                case "serial":
                    // Plug icon
                    using (var pen = new Pen(Color.FromArgb(180, 130, 60), 1.5f))
                    {
                        g.DrawRectangle(pen, 4, 2, 8, 12);
                        g.DrawLine(pen, 6, 5, 6, 8);
                        g.DrawLine(pen, 8, 5, 8, 8);
                        g.DrawLine(pen, 10, 5, 10, 8);
                        g.DrawLine(pen, 6, 10, 10, 10);
                    }
                    break;
                case "server":
                    using (var bg = new SolidBrush(Color.FromArgb(80, 80, 80)))
                        g.FillRectangle(bg, 2, 1, 12, 14);
                    using (var fg = new SolidBrush(Color.FromArgb(100, 200, 100)))
                    {
                        g.FillEllipse(fg, 4, 3, 2, 2);
                        g.FillEllipse(fg, 4, 7, 2, 2);
                    }
                    using (var line = new Pen(Color.FromArgb(120, 120, 120), 1))
                    {
                        g.DrawLine(line, 2, 6, 14, 6);
                        g.DrawLine(line, 2, 10, 14, 10);
                    }
                    break;
                case "group":
                    using (var bg = new SolidBrush(Color.FromArgb(60, 120, 180)))
                        g.FillEllipse(bg, 3, 3, 10, 10);
                    using (var fg = new SolidBrush(Color.White))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("G", new Font("Consolas", 7f, FontStyle.Bold), fg, new RectangleF(3, 3, 10, 10), sf);
                    }
                    break;
            }
        }

        /// <summary>
        /// 获取连接对应的图标 key
        /// </summary>
        private string GetIconKey(ConnectionConfig config)
        {
            switch (config.Protocol)
            {
                case ProtocolType.SSH: return "ssh";
                case ProtocolType.RDP: return "rdp";
                case ProtocolType.Serial: return "serial";
                default: return "server";
            }
        }

        /// <summary>
        /// 应用界面字体（非等宽）——供 MainForm 在启动时从 GlobalAppearance 应用。
        /// </summary>
        public void ApplyUIFont(string name, int size)
        {
            if (string.IsNullOrEmpty(name) || size < 8 || size > 24) return;
            try { _treeView.Font = new Font(name, size, FontStyle.Regular); }
            catch { _treeView.Font = new Font("Microsoft YaHei UI", 9f); }
        }

        /// <summary>
        /// 从 GlobalAppearance 取 UI 字体 — 若尚未初始化则回到安全默认值。
        /// 重冷启动时 AppearanceSettings 还未加载，退到 Microsoft YaHei UI 9。
        /// </summary>
        private static Font ResolveDefaultFont()
        {
            try
            {
                var ga = Gdterm.UI.Program.GlobalAppearance;
                if (ga != null && !string.IsNullOrEmpty(ga.UIFontName) && ga.UIFontSize >= 8 && ga.UIFontSize <= 24)
                    return new Font(ga.UIFontName, ga.UIFontSize, FontStyle.Regular);
            }
            catch { }
            return new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        }

        /// <summary>当前内部 TreeView（供 MainForm 更改字体）。</summary>
        internal TreeView InternalTree => _treeView;

        public void LoadConnections()
        {
            _allConnections = new List<ConnectionConfig>(_connectionStore.LoadAll());
            RebuildTree(_allConnections);
        }

        /// <summary>
        /// 从给定连接列表重建树。筛选时传过滤后的子集，全量加载传 _allConnections。
        /// </summary>
        private void RebuildTree(List<ConnectionConfig> connections)
        {
            _treeView.Nodes.Clear();
            var groupNodes = new Dictionary<string, TreeNode>();

            var rootNode = new TreeNode("所有连接") { ImageKey = "folder", SelectedImageKey = "folder" };
            _treeView.Nodes.Add(rootNode);

            foreach (var config in connections)
            {
                var iconKey = GetIconKey(config);
                var connNode = new TreeNode(config.Name)
                {
                    Tag = config,
                    ImageKey = iconKey,
                    SelectedImageKey = iconKey,
                    ToolTipText = $"{config.Protocol} | {config.Host}:{config.Port}"
                };

                // 按 GroupPath 分组
                if (!string.IsNullOrEmpty(config.GroupPath))
                {
                    var groupNode = GetOrCreateGroupNode(rootNode, groupNodes, config.GroupPath);
                    groupNode.Nodes.Add(connNode);
                }
                else
                {
                    rootNode.Nodes.Add(connNode);
                }
            }

            rootNode.Expand();
        }

        /// <summary>
        /// 输入过滤文本：名称/主机/分组名任一包含即保留，分组路径上 未命中也保留其空父节点。
        /// 空文本重建全量。
        /// </summary>
        private void ApplyFilter(string text)
        {
            if (IsDisposed) return;
            if (_allConnections == null) return;

            text = (text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                RebuildTree(_allConnections);
                return;
            }

            // 大小写不敏感 + 平台友好
            var q = text.ToLowerInvariant();
            var filtered = new List<ConnectionConfig>();
            foreach (var c in _allConnections)
            {
                if (c == null) continue;
                var name = (c.Name ?? string.Empty).ToLowerInvariant();
                var host = (c.Host ?? string.Empty).ToLowerInvariant();
                var grp  = (c.GroupPath ?? string.Empty).ToLowerInvariant();
                if (name.Contains(q) || host.Contains(q) || grp.Contains(q))
                    filtered.Add(c);
            }
            RebuildTree(filtered);
        }

        /// <summary>
        /// 固定/取消固定切换（Auto-hide 模式）。
        /// 取消固定后收为 CollapsedWidth 窄边，鼠标进入则临时展开，离开则收回。
        /// </summary>
        public void TogglePin()
        {
            if (IsDisposed) return;
            _pinned = !_pinned;
            ApplyPinState();
        }

        private void ApplyPinState()
        {
            if (IsDisposed) return;
            if (_pinned)
            {
                Width = _pinnedWidth;
                if (_filterBox != null) _filterBox.Visible = true;
                if (_treeView != null) _treeView.Visible = true;
            }
            else
            {
                if (Width >= _pinnedWidth) _pinnedWidth = Width;
                Width = CollapsedWidth;
                if (_filterBox != null) _filterBox.Visible = false;
                if (_treeView != null) _treeView.Visible = false;
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_pinned && Width == CollapsedWidth)
            {
                // 临时展开（不置位 _pinned）
                Width = _pinnedWidth;
                if (_filterBox != null) _filterBox.Visible = true;
                if (_treeView != null) _treeView.Visible = true;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // WinForms 会把鼠标进入子控件当成父控件的 MouseLeave 事件，所以这里必须用全局光标位置判一下
            // 否则用户从窄边移动到刚展开的 tree 时会立刻收回去，根本点不到 tree
            if (!_pinned)
            {
                var clientPos = PointToClient(Cursor.Position);
                if (!ClientRectangle.Contains(clientPos))
                {
                    Width = CollapsedWidth;
                    if (_filterBox != null) _filterBox.Visible = false;
                    if (_treeView != null) _treeView.Visible = false;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 折叠窄边时画一个 pin 箭头，提示用户悬停可以展开
            if (!_pinned && Width <= CollapsedWidth + 2)
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Gdterm.UI.Diagnostics.GdtermColorTable.Muted))
                {
                    float cx = Width / 2f;
                    // chevron » 指向右（展开方向）
                    var p1 = new PointF(cx - 3, 6);
                    var p2 = new PointF(cx + 3, 12);
                    var p3 = new PointF(cx - 3, 18);
                    using (var pen = new Pen(Gdterm.UI.Diagnostics.GdtermColorTable.Muted, 1.5f))
                    {
                        g.DrawLine(pen, p1, p2);
                        g.DrawLine(pen, p2, p3);
                    }
                }
            }
        }

        private TreeNode GetOrCreateGroupNode(TreeNode root, Dictionary<string, TreeNode> dict, string groupPath)
        {
            if (dict.ContainsKey(groupPath))
                return dict[groupPath];

            // 支持多级分组 "Web/生产"
            var parts = groupPath.Split('/');
            TreeNode current = root;
            var currentPath = "";

            foreach (var part in parts)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? part : currentPath + "/" + part;
                if (!dict.ContainsKey(currentPath))
                {
                    var groupNode = new TreeNode(part)
                    {
                        ImageKey = "folder",
                        SelectedImageKey = "folder",
                        Tag = currentPath // Tag 存储完整路径
                    };
                    current.Nodes.Add(groupNode);
                    dict[currentPath] = groupNode;
                }
                current = dict[currentPath];
            }

            return dict[groupPath];
        }

        private void OnNodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Tag is ConnectionConfig config)
            {
                ConnectionDoubleClicked?.Invoke(this, config);
            }
        }

        // ===== 拖放：连接节点 -> 分组节点（修改 GroupPath） =====

        private void OnItemDrag(object sender, ItemDragEventArgs e)
        {
            if (e.Item is TreeNode node && IsConnectionNode(node))
            {
                try { _treeView.DoDragDrop(node, DragDropEffects.Move); } catch { }
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data == null) { e.Effect = DragDropEffects.None; return; }
            var node = NodeAtDragPoint(e);
            bool ok = node != null && (IsGroupNode(node) || IsRootNode(node));
            e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data == null || e.Effect != DragDropEffects.Move) return;
            var dragged = e.Data.GetData(typeof(TreeNode)) as TreeNode;
            if (dragged == null || !IsConnectionNode(dragged)) return;
            var target = NodeAtDragPoint(e);
            if (target == null) return;

            // 目标分组路径：根节点 -> ""，分组节点 -> 其 GroupPath
            string newGroup;
            if (IsRootNode(target)) newGroup = "";
            else if (IsGroupNode(target)) newGroup = (string)target.Tag;
            else return;

            var cfg = (ConnectionConfig)dragged.Tag;
            if (string.Equals(cfg.GroupPath ?? "", newGroup ?? "", StringComparison.Ordinal)) return; // 无变化

            try
            {
                cfg.GroupPath = newGroup;
                _connectionStore.Update(cfg);
                LoadConnections();
                ConnectionListChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Gdterm.UI.Diagnostics.DiagLog.Swallowed("ConnectionTree.DragDrop", ex);
                Gdterm.UI.Diagnostics.ToastNotifier.Error("移动失败: " + ex.Message);
            }
        }

        private TreeNode NodeAtDragPoint(DragEventArgs e)
        {
            try
            {
                var pt = _treeView.PointToClient(new System.Drawing.Point(e.X, e.Y));
                return _treeView.GetNodeAt(pt);
            }
            catch { return null; }
        }

        /// <summary>右键选中节点——确保右键点中的节点成为选中节点，以便后续菜单项处理。</summary>
        private void OnMouseDownSelectRightClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var hit = _treeView.GetNodeAt(e.Location);
                _rightClickedNode = hit;
                if (hit != null)
                    _treeView.SelectedNode = hit;
            }
        }

        // 右键 BaseNode 检测。
        private bool IsConnectionNode(TreeNode n) => n != null && (n.Tag is ConnectionConfig);
        private bool IsGroupNode(TreeNode n) => n != null && (n.Tag is string) && !string.IsNullOrEmpty((string)n.Tag);
        private bool IsRootNode(TreeNode n) => n != null && (n.Tag == null) && (n.Nodes.Count > 0) && ReferenceEquals(n, _treeView.Nodes[0]);

        /// <summary>
        /// 动态调整右键菜单——区分连接/分组/根节点。
        /// 旧实现问题：右键弹的是同一组项，在主机节点上也出现“新建连接”，反直觉。
        /// </summary>
        private void OnContextMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var node = _rightClickedNode;
            var itemLocal = FindItemByTag("local") as ToolStripMenuItem;
            var itemNew = FindItemByTag("new") as ToolStripMenuItem;
            var itemEdit = FindItemByTag("edit") as ToolStripMenuItem;
            var sep1 = FindItemByTag("sep1") as ToolStripSeparator;
            var sep0 = FindItemByTag("sep0") as ToolStripSeparator;
            var itemDel = FindItemByTag("delete") as ToolStripMenuItem;
            var itemConn = FindItemByTag("connect") as ToolStripMenuItem;
            var itemCopyHost = FindItemByTag("copyhost") as ToolStripMenuItem;

            if (itemLocal == null || itemNew == null || itemEdit == null || sep1 == null || sep0 == null || itemDel == null || itemConn == null || itemCopyHost == null)
                return;

            // “本地终端”始终可用——根节点/空白/分组/连接都可开本地终端（独立动作，不依赖节点语义）。
            itemLocal.Enabled = true;

            if (IsConnectionNode(node))
            {
                // 连接节点：编辑/删除/连接 为主，新建也保留（建到该连接所在分组）
                var cfg = (ConnectionConfig)node.Tag;
                var grp = cfg.GroupPath ?? "";
                itemNew.Text = string.IsNullOrEmpty(grp) ? "新建连接(&N)" : $"新建连接到本分组(&N)";
                itemEdit.Enabled = true;
                itemDel.Enabled = true;
                itemConn.Enabled = true;
                itemCopyHost.Enabled = !string.IsNullOrEmpty(cfg.Host);
                sep0.Visible = sep1.Visible = true;
            }
            else if (IsGroupNode(node))
            {
                // 分组节点：新建连接到本分组；编辑/删除分组在本意上可提（本版本暂禁到提）
                var grp = (string)node.Tag;
                itemNew.Text = $"新建连接到本分组(&N)";
                itemEdit.Enabled = false; // 未支持编辑分组（实现重构后可动）
                itemDel.Enabled = false;  // 未支持删分组（避免误删连接）
                itemConn.Enabled = false;
                itemCopyHost.Enabled = false;
                sep0.Visible = false;
            }
            else
            {
                // 根节点 / 空白：只允许顶层新建连接，编辑/删除/连接禁用
                itemNew.Text = "新建连接(&N)";
                itemEdit.Enabled = false;
                itemDel.Enabled = false;
                itemConn.Enabled = false;
                itemCopyHost.Enabled = false;
                sep0.Visible = false;
                sep1.Visible = false;
            }
        }

        /// <summary>复制主机地址到剪贴板（仅连接节点可用）。</summary>
        private void OnCopyHost(object sender, EventArgs e)
        {
            if (!IsConnectionNode(_rightClickedNode)) return;
            var cfg = _rightClickedNode.Tag as ConnectionConfig;
            if (cfg == null || string.IsNullOrEmpty(cfg.Host)) return;
            try { Clipboard.SetText(cfg.Host); } catch { }
        }

        /// <summary>按 Tag 查找上下文菜单项，不依赖硬编码索引。</summary>
        private ToolStripItem FindItemByTag(string tag)
        {
            foreach (ToolStripItem item in _contextMenu.Items)
            {
                if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                    return item;
            }
            return null;
        }

        private void OnNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _rightClickedNode = e.Node;
                _treeView.SelectedNode = e.Node;
            }
        }

        private string ResolveDefaultGroupFromRightClick()
        {
            var node = _rightClickedNode;
            if (node == null) return string.Empty;
            if (IsGroupNode(node)) return (string)node.Tag;
            if (IsConnectionNode(node))
            {
                var cfg = (ConnectionConfig)node.Tag;
                return cfg.GroupPath ?? string.Empty;
            }
            return string.Empty;
        }

        private void OnNewConnection(object sender, EventArgs e)
        {
            var defaultGroup = ResolveDefaultGroupFromRightClick();
            if (string.IsNullOrEmpty(defaultGroup))
            {
                using (var dlg = new ConnectionDialog(keepass: _keepass))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                    {
                        _connectionStore.Add(dlg.Result);
                        LoadConnections();
                        ConnectionListChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            else
            {
                using (var dlg = new ConnectionDialog(defaultGroup, keepass: _keepass))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                    {
                        _connectionStore.Add(dlg.Result);
                        LoadConnections();
                        ConnectionListChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        private void OnEditConnection(object sender, EventArgs e)
        {
            var node = _rightClickedNode ?? _treeView.SelectedNode;
            if (node?.Tag is ConnectionConfig config)
            {
                using (var dlg = new ConnectionDialog(config, keepass: _keepass))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                    {
                        _connectionStore.Update(dlg.Result);
                        LoadConnections();
                        ConnectionListChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        private void OnDeleteConnection(object sender, EventArgs e)
        {
            var node = _rightClickedNode ?? _treeView.SelectedNode;
            if (node?.Tag is ConnectionConfig config)
            {
                var result = MessageBox.Show(
                    $"确定删除连接 '{config.Name}'？\n\n此操作不可撤销。",
                    "确认删除",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    _connectionStore.Delete(config.Id);
                    LoadConnections();
                    ConnectionListChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void OnConnect(object sender, EventArgs e)
        {
            var node = _rightClickedNode ?? _treeView.SelectedNode;
            if (node?.Tag is ConnectionConfig config)
            {
                ConnectionDoubleClicked?.Invoke(this, config);
            }
        }

        private void OnOpenLocalTerminal(object sender, EventArgs e)
        {
            OpenLocalTerminalRequested?.Invoke();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _imageList?.Dispose();
                _contextMenu?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
