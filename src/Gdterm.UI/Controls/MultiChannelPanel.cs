using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Terminal;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 多通道输入面板——WindTerm 风格，显示所有会话列表，支持勾选/全选/广播输入
    /// </summary>
    public class MultiChannelPanel : UserControl
    {
        private readonly MultiChannelManager _manager;
        private ListView _sessionList;
        private Button _btnSelectAll;
        private Button _btnDeselectAll;
        private Button _btnBroadcast;
        private Label _statusLabel;
        private TextBox _commandInput;

        /// <summary>
        /// 广播命令事件（用户在输入框输入命令后触发）
        /// </summary>
        public event EventHandler<string> BroadcastCommandRequested;

        public MultiChannelPanel(MultiChannelManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            InitializeComponent();
            WireEvents();
        }

        private void InitializeComponent()
        {
            Size = new Size(300, 400);
            BackColor = SystemColors.Control;

            // 工具栏
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 35,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(3)
            };

            _btnSelectAll = new Button { Text = "全选", Size = new Size(50, 25), FlatStyle = FlatStyle.Flat };
            _btnDeselectAll = new Button { Text = "取消", Size = new Size(50, 25), FlatStyle = FlatStyle.Flat };
            _btnBroadcast = new Button { Text = "广播", Size = new Size(60, 25), FlatStyle = FlatStyle.Flat, Enabled = false };

            toolbar.Controls.AddRange(new Control[] { _btnSelectAll, _btnDeselectAll, _btnBroadcast });

            // 会话列表
            _sessionList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _sessionList.Columns.Add("主机", 120);
            _sessionList.Columns.Add("分组", 60);
            _sessionList.Columns.Add("状态", 50);
            _sessionList.Columns.Add("命令数", 50);

            // 命令输入框
            var inputPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60
            };

            _commandInput = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 25,
                Font = new Font("Consolas", 9f)
            };

            var inputHint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "输入命令后按 Enter 广播到所有选中会话",
                ForeColor = SystemColors.GrayText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            inputPanel.Controls.Add(inputHint);
            inputPanel.Controls.Add(_commandInput);

            // 状态栏
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Text = "就绪",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3, 0, 0, 0),
                BackColor = SystemColors.ControlLight
            };

            Controls.Add(_sessionList);
            Controls.Add(toolbar);
            Controls.Add(inputPanel);
            Controls.Add(_statusLabel);
        }

        private void WireEvents()
        {
            _btnSelectAll.Click += (s, e) =>
            {
                _manager.SelectAll();
                RefreshList();
            };

            _btnDeselectAll.Click += (s, e) =>
            {
                _manager.DeselectAll();
                RefreshList();
            };

            _btnBroadcast.Click += (s, e) => ExecuteBroadcast();

            _sessionList.ItemCheck += OnSessionItemCheck;

            _commandInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ExecuteBroadcast();
                    e.SuppressKeyPress = true;
                }
            };

            _manager.SessionRegistered += (s, e) => RefreshList();
            _manager.SessionUnregistered += (s, e) => RefreshList();
            _manager.BroadcastStateChanged += (s, e) =>
            {
                _btnBroadcast.Enabled = e.IsBroadcasting;
                UpdateStatus();
            };
        }

        private void OnSessionItemCheck(object sender, ItemCheckEventArgs e)
        {
            // 延迟处理，因为事件在状态改变前触发
            BeginInvoke(new Action(() =>
            {
                var item = _sessionList.Items[e.Index];
                var sessionId = item.Tag as string;
                if (sessionId != null)
                {
                    if (e.NewValue == CheckState.Checked)
                        _manager.Select(sessionId);
                    else
                        _manager.Deselect(sessionId);
                    UpdateStatus();
                }
            }));
        }

        private void ExecuteBroadcast()
        {
            var command = _commandInput.Text;
            if (string.IsNullOrWhiteSpace(command)) return;

            BroadcastCommandRequested?.Invoke(this, command);
            _commandInput.Clear();
        }

        /// <summary>
        /// 刷新会话列表
        /// </summary>
        public void RefreshList()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(RefreshList));
                return;
            }

            _sessionList.BeginUpdate();
            _sessionList.Items.Clear();

            var sessions = _manager.GetAllSessions();
            foreach (var session in sessions)
            {
                var item = new ListViewItem(session.DisplayName);
                item.SubItems.Add(session.Group);
                item.SubItems.Add(session.IsConnected ? "●" : "○");
                item.SubItems.Add(session.CommandCount.ToString());
                item.Tag = session.SessionId;
                item.Checked = session.IsSelected;

                // 未连接的会话灰色显示
                if (!session.IsConnected)
                    item.ForeColor = SystemColors.GrayText;

                _sessionList.Items.Add(item);
            }

            _sessionList.EndUpdate();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var total = _manager.GetAllSessions().Count;
            var selected = _manager.SelectedCount;
            _statusLabel.Text = selected > 0
                ? $"已选中 {selected}/{total} 个会话，广播模式已激活"
                : $"共 {total} 个会话，未选择广播目标";
        }
    }
}
