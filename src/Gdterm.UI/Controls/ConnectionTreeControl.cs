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
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Microsoft YaHei", 9f),
                BorderStyle = BorderStyle.None,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HotTracking = true,
                FullRowSelect = true
            };
            _treeView.NodeMouseDoubleClick += OnNodeMouseDoubleClick;
            Controls.Add(_treeView);

            // 右键菜单
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add("新建连接(&N)", null, OnNewConnection);
            _contextMenu.Items.Add("编辑(&E)", null, OnEditConnection);
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("删除(&D)", null, OnDeleteConnection);
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("连接(&C)", null, OnConnect);
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

        private void OnNewConnection(object sender, EventArgs e)
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

        private void OnEditConnection(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is ConnectionConfig config)
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
            if (_treeView.SelectedNode?.Tag is ConnectionConfig config)
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
            if (_treeView.SelectedNode?.Tag is ConnectionConfig config)
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
