using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 命令历史面板——显示所有会话的命令执行记录，支持搜索和过滤
    /// </summary>
    public class CommandHistoryPanel : UserControl
    {
        private readonly CommandHistoryStore _store;
        private ListView _historyList;
        private AntdUI.Input _searchBox;
        private AntdUI.Select _hostFilter;
        private AntdUI.Label _statusLabel;

        /// <summary>
        /// 双击命令事件（用于重新执行）
        /// </summary>
        public event EventHandler<CommandHistoryEntry> CommandDoubleClicked;

        public CommandHistoryPanel(CommandHistoryStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Size = DpiScale.S(this, 500, 400);
            BackColor = SystemColors.Control;

            // 搜索栏
            var searchPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 35,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(3)
            };

            _searchBox = new AntdUI.Input {
                Size = DpiScale.S(this, 150, 23)
            };
            _searchBox.PlaceholderText = "搜索命令...";

            _hostFilter = new AntdUI.Select {
                Size = DpiScale.S(this, 120, 23),
            };
            _hostFilter.Items.Add("所有主机");
            _hostFilter.SelectedIndex = 0;

            var btnRefresh = new AntdUI.Button { Text = "刷新", Size = DpiScale.S(this, 50, 23)};
            var btnClear = new AntdUI.Button { Text = "清空", Size = DpiScale.S(this, 50, 23)};

            searchPanel.Controls.AddRange(new Control[] { _searchBox, _hostFilter, btnRefresh, btnClear });

            // 命令列表
            _historyList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Clickable
            };
            _historyList.Columns.Add("时间", 130);
            _historyList.Columns.Add("主机", 100);
            _historyList.Columns.Add("命令", 200);
            _historyList.Columns.Add("耗时", 60);
            _historyList.Columns.Add("广播", 40);

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                Height = 20,
                Text = "就绪",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3, 0, 0, 0),
                BackColor = SystemColors.ControlLight
            };

            Controls.Add(_historyList);
            Controls.Add(searchPanel);
            Controls.Add(_statusLabel);

            // 事件绑定
            _searchBox.TextChanged += (s, e) => RefreshHistory();
            _hostFilter.SelectedIndexChanged += (s, e) => RefreshHistory();
            btnRefresh.Click += (s, e) => RefreshHistory();
            btnClear.Click += (s, e) => ClearHistory();
            _historyList.DoubleClick += OnItemDoubleClick;
        }

        /// <summary>
        /// 刷新命令历史列表
        /// </summary>
        public void RefreshHistory()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(RefreshHistory));
                return;
            }

            var query = new CommandHistoryQuery
            {
                Limit = 500,
                NewestFirst = true
            };

            if (!string.IsNullOrEmpty(_searchBox.Text))
                query.CommandContains = _searchBox.Text;

            if (_hostFilter.SelectedIndex > 0)
                query.Hostname = _hostFilter.SelectedValue as string;

            var entries = _store.Query(query);

            _historyList.BeginUpdate();
            _historyList.Items.Clear();

            foreach (var entry in entries)
            {
                var item = new ListViewItem(entry.ExecutedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(entry.Hostname ?? "");
                item.SubItems.Add(TruncateCommand(entry.Command, 50));
                item.SubItems.Add(FormatDuration(entry.DurationMs));
                item.SubItems.Add(entry.IsBroadcast ? "●" : "");
                item.Tag = entry;
                _historyList.Items.Add(item);
            }

            _historyList.EndUpdate();
            _statusLabel.Text = $"共 {entries.Count} 条记录";
        }

        /// <summary>
        /// 更新主机过滤器下拉列表
        /// </summary>
        public void UpdateHostFilter(System.Collections.Generic.IList<CommandHistoryEntry> entries)
        {
            _hostFilter.Items.Clear();
            _hostFilter.Items.Add("所有主机");

            var hosts = new System.Collections.Generic.SortedSet<string>();
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.Hostname))
                    hosts.Add(entry.Hostname);
            }

            foreach (var host in hosts)
                _hostFilter.Items.Add(host);

            _hostFilter.SelectedIndex = 0;
        }

        private void OnItemDoubleClick(object sender, EventArgs e)
        {
            if (_historyList.SelectedItems.Count > 0)
            {
                var entry = _historyList.SelectedItems[0].Tag as CommandHistoryEntry;
                if (entry != null)
                    CommandDoubleClicked?.Invoke(this, entry);
            }
        }

        private void ClearHistory()
        {
            var result = MessageBox.Show(
                "确定要清空命令历史记录吗？此操作不可撤销。",
                "确认清空",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                // 实际实现中应该清空文件
                RefreshHistory();
            }
        }

        private static string TruncateCommand(string command, int maxLength)
        {
            if (string.IsNullOrEmpty(command)) return "";
            command = command.Replace("\n", " ").Replace("\r", "");
            return command.Length > maxLength
                ? command.Substring(0, maxLength) + "..."
                : command;
        }

        private static string FormatDuration(long ms)
        {
            if (ms < 1000) return $"{ms}ms";
            if (ms < 60000) return $"{ms / 1000.0:F1}s";
            return $"{ms / 60000:F1}m";
        }
    }
}
