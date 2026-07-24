using System;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 连接面板（TreeView + 右键菜单）
    /// </summary>
    public class ConnectionTreeControl : UserControl
    {
        private readonly IConnectionStore _connectionStore;
        private TreeView _treeView;
        private ContextMenuStrip _contextMenu;

        public event EventHandler<ConnectionConfig> ConnectionDoubleClicked;

        public ConnectionTreeControl(IConnectionStore connectionStore)
        {
            _connectionStore = connectionStore;
            InitializeComponent();
            LoadConnections();
        }

        private void InitializeComponent()
        {
            _treeView = new TreeView
            {
                Dock = DockStyle.Fill,
                ImageList = null // TODO: 添加图标
            };
            _treeView.NodeMouseDoubleClick += OnNodeMouseDoubleClick;
            Controls.Add(_treeView);

            // 右键菜单
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Items.Add("新建连接", null, OnNewConnection);
            _contextMenu.Items.Add("编辑", null, OnEditConnection);
            _contextMenu.Items.Add("删除", null, OnDeleteConnection);
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("连接", null, OnConnect);
            _treeView.ContextMenuStrip = _contextMenu;
        }

        private void LoadConnections()
        {
            _treeView.Nodes.Clear();

            var connections = _connectionStore.LoadAll();
            var rootNode = new TreeNode("所有连接");

            foreach (var config in connections)
            {
                var node = new TreeNode(config.Name)
                {
                    Tag = config
                };
                rootNode.Nodes.Add(node);
            }

            _treeView.Nodes.Add(rootNode);
            rootNode.Expand();
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
            // TODO: 打开新建连接对话框
            MessageBox.Show("新建连接功能待实现", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnEditConnection(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is ConnectionConfig config)
            {
                // TODO: 打开编辑连接对话框
                MessageBox.Show($"编辑连接: {config.Name}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OnDeleteConnection(object sender, EventArgs e)
        {
            if (_treeView.SelectedNode?.Tag is ConnectionConfig config)
            {
                var result = MessageBox.Show(
                    $"确定删除连接 '{config.Name}'？",
                    "确认删除",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    _connectionStore.Delete(config.Id);
                    LoadConnections();
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
    }
}
