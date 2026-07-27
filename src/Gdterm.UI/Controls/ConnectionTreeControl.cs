using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 连接面板（TreeView + 右键菜单 + 图标）
    /// </summary>
    public class ConnectionTreeControl : UserControl
    {
        private readonly IConnectionStore _connectionStore;
        private TreeView _treeView;
        private ContextMenuStrip _contextMenu;
        private ImageList _imageList;
        // 右键点中的节点（ContextMenuStrip.Opening 读的），后后菜单设置选中
        private TreeNode _rightClickedNode;

        public event EventHandler<ConnectionConfig> ConnectionDoubleClicked;
        public event EventHandler ConnectionListChanged;

        public ConnectionTreeControl(IConnectionStore connectionStore)
        {
            _connectionStore = connectionStore;
            InitializeComponent();
            LoadConnections();
        }

        private void InitializeComponent()
        {
            BuildImageList();

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
            Controls.Add(_treeView);

            // 右键菜单——在 Opening 中按右键节点动态调整项，避免“主机上右键也弹新建”
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add("新建连接(&N)", null, OnNewConnection);
            _contextMenu.Items.Add("编辑(&E)", null, OnEditConnection);
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("删除(&D)", null, OnDeleteConnection);
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("连接(&C)", null, OnConnect);
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
            _treeView.Nodes.Clear();

            var connections = _connectionStore.LoadAll();
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
            var itemNew = _contextMenu.Items[0] as ToolStripMenuItem; // 新建连接
            var sep1 = _contextMenu.Items[1] as ToolStripSeparator;
            var itemEdit = _contextMenu.Items[2] as ToolStripMenuItem; // 编辑
            var sep2 = _contextMenu.Items[3] as ToolStripSeparator;
            var itemDel = _contextMenu.Items[4] as ToolStripMenuItem; // 删除
            var sep3 = _contextMenu.Items[5] as ToolStripSeparator;
            var itemConn = _contextMenu.Items[6] as ToolStripMenuItem; // 连接

            if (itemNew == null || itemEdit == null || sep1 == null || sep2 == null || itemDel == null || sep3 == null || itemConn == null)
                return;

            if (IsConnectionNode(node))
            {
                // 连接节点：编辑/删除/连接 为主，新建也保留（建到该连接所在分组）
                var cfg = (ConnectionConfig)node.Tag;
                var grp = cfg.GroupPath ?? "";
                itemNew.Text = string.IsNullOrEmpty(grp) ? "新建连接(&N)" : $"新建连接到本分组(&N)";
                itemEdit.Enabled = true;
                itemDel.Enabled = true;
                itemConn.Enabled = true;
                sep1.Visible = sep2.Visible = sep3.Visible = true;
            }
            else if (IsGroupNode(node))
            {
                // 分组节点：新建连接到本分组；编辑/删除分组在本意上可提（本版本暂禁到提）
                var grp = (string)node.Tag;
                itemNew.Text = $"新建连接到本分组(&N)";
                itemEdit.Enabled = false; // 未支持编辑分组（实现重构后可动）
                itemDel.Enabled = false;  // 未支持删分组（避免误删连接）
                itemConn.Enabled = false;
                sep2.Visible = false;
                sep3.Visible = false;
            }
            else
            {
                // 根节点 / 空白：只允许顶层新建连接，编辑/删除/连接禁用
                itemNew.Text = "新建连接(&N)";
                itemEdit.Enabled = false;
                sep2.Visible = false;
                itemDel.Enabled = false;
                itemConn.Enabled = false;
                sep3.Visible = false;
            }
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
                using (var dlg = new ConnectionDialog())
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
                using (var dlg = new ConnectionDialog(defaultGroup))
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
                using (var dlg = new ConnectionDialog(config))
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
