using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.Core.Models;
using Gdterm.Logging;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 命令历史面板——显示所有会话的命令执行记录，支持搜索和过滤
    /// </summary>
    public class CommandHistoryPanel : UserControl
    {
        private readonly CommandHistoryStore _store;
        private ListView _historyList;
        private EmptyStateView _empty;
        private AntdUI.Input _searchBox;
        private AntdUI.Select _hostFilter;
        private AntdUI.Label _statusLabel;
        /// <summary>排序/批量/导出壳（自带搜索，故壳只出排序与导出）</summary>
        private ListShell _shell;

        /// <summary>
        /// 双击命令事件（用于重新执行）
        /// </summary>
        public event EventHandler<CommandHistoryEntry> CommandDoubleClicked;

        public CommandHistoryPanel(CommandHistoryStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            InitializeComponent();
            // F16 回收站：删除的命令记录可经「工具 → 回收站」恢复
            TrashStore.Default.RegisterRestorer("commandhistory", entry =>
            {
                var en = entry.Payload as CommandHistoryEntry;
                if (en != null) _store.RecordCommand(en);
            });
        }

        private void InitializeComponent()
        {
            Size = DpiScale.S(this, 500, 400);
            BackColor = GdtermColorTable.Background;

            // 搜索栏
            var searchPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(DpiScale.V(this, 3))
            };

            _searchBox = new AntdUI.Input {
                Width = DpiScale.V(this, 150),
                AutoSize = true,
                // 工具条输入无高会被压矮 → 38 地板
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this))
            };
            _searchBox.PlaceholderText = L10n.T("搜索命令...");

            // AntdUI.Select 是自绘控件，不认基类 AutoSize（宽度只能靠 Width/MinimumSize 定）；
            // 写 AutoSize=true 曾让下拉宽失控——只显示半个字（SCALING 规则 4d / R12）。
            _hostFilter = new AntdUI.Select {
                Width = DpiScale.V(this, 120),
                MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this)),
            };
            _hostFilter.Items.Add(L10n.T("所有主机"));
            _hostFilter.SelectedIndex = 0;

            var btnRefresh = new AntdUI.Button { Text = L10n.T("刷新"), AutoSize = true, Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3))};
            var btnDeleteSel = new AntdUI.Button { Text = L10n.T("删选中"), AutoSize = true, Enabled = false, Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3))};
            var btnClear = new AntdUI.Button { Text = L10n.T("清空"), AutoSize = true, Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 3), DpiScale.V(this, 8), DpiScale.V(this, 3))};

            searchPanel.Controls.AddRange(new Control[] { _searchBox, _hostFilter, btnRefresh, btnDeleteSel, btnClear });

            // 命令列表（F6：多选——Ctrl/Shift 选多条后「删选中」批量删）
            _historyList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.Clickable
            };
            _historyList.Columns.Add(L10n.T("时间"), 130);
            _historyList.Columns.Add(L10n.T("主机"), 100);
            _historyList.Columns.Add(L10n.T("命令"), 200);
            _historyList.Columns.Add(L10n.T("耗时"), 60);
            _historyList.Columns.Add(L10n.T("广播"), 40);

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Text = L10n.T("就绪"),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 3), 0, 0, 0),
                BackColor = GdtermColorTable.Surface
            };

            // 排序 + 导出走共用壳；过滤仍由上面这个搜索框负责（它走 store 查询，能搜全量而非当前屏）
            _shell = new ListShell(_historyList, showFilter: false);
            var btnExport = _shell.AddAction(L10n.T("导出"), (s, e) => _shell.ExportCsv(FindForm(), "command-history"));
            btnExport.Name = "HistoryExportButton";
            _shell.ItemActivated += (s, it) =>
            {
                var entry = it == null ? null : it.Tag as CommandHistoryEntry;
                if (entry != null) CommandDoubleClicked?.Invoke(this, entry);
            };

            Controls.Add(_shell);
            Controls.Add(searchPanel);
            Controls.Add(_statusLabel);
            _historyList.AccessibleName = L10n.T("命令历史列表");
            _historyList.AccessibleDescription = L10n.T("时间/主机/命令/耗时/是否广播");
            // 无障碍名称回填：ListView/Table 本身没有 Text，AutoName 只补有文本的控件，
            // 列表名单独设（读屏软件念"宏列表"而不是"列表"）
            try { UiA11y.AutoName(this); } catch (Exception) { }


            // 审计 C1：空态引导（此前打开就是“一整片背景色”，分不清空/加载/坏了）
            _empty = EmptyStateView.Attach(this, L10n.T("还没有命令历史"),
                L10n.T("在终端里执行过的命令会自动记录在这里，可按命令内容或主机过滤"),
                L10n.T("打开终端"));

            // 事件绑定
            _searchBox.TextChanged += (s, e) => RefreshHistory();
            _hostFilter.SelectedIndexChanged += (s, e) => RefreshHistory();
            btnRefresh.Click += (s, e) => RefreshHistory();
            btnClear.Click += (s, e) => ClearHistory();
            btnDeleteSel.Click += (s, e) => DeleteSelected();
            _historyList.ItemSelectionChanged += (s, e) => { btnDeleteSel.Enabled = _historyList.SelectedItems.Count > 0; };
            _historyList.DoubleClick += OnItemDoubleClick;   // 旧路径保留（外部订阅者可能依赖）

            // 审计 C1 收尾（CI 0.1.348 实证）：构造后首刷一次。此前首次打开面板时
            // RefreshHistory 无人调用，EmptyStateView 初值 Visible=false 永不翻亮，
            // 空库用户看到的仍是一整片背景色（C1 要修的原症状）。
            try { RefreshHistory(); }
            catch (Exception exSwallowed) { try { DiagLog.Swallowed("CommandHistoryPanel.Ctor.Refresh", exSwallowed); } catch { } }
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

            var items = new System.Collections.Generic.List<ListViewItem>(entries.Count);
            foreach (var entry in entries)
            {
                var item = new ListViewItem(entry.ExecutedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(entry.Hostname ?? "");
                item.SubItems.Add(TruncateCommand(entry.Command, 50));
                item.SubItems.Add(FormatDuration(entry.DurationMs));
                item.SubItems.Add(entry.IsBroadcast ? "●" : "");
                item.Tag = entry;
                items.Add(item);
            }
            _shell.SetItems(items);
            _statusLabel.Text = L10n.Tf("共 {0} 条记录", entries.Count);

            // 审计 C1：空态文案区分“确实没有”与“筛选后为空”
            if (_empty != null)
            {
                bool filtered = !string.IsNullOrEmpty(_searchBox.Text) || _hostFilter.SelectedIndex > 0;
                _empty.SetEmpty(entries.Count == 0, filtered
                    ? L10n.T("没有匹配的命令")
                    : L10n.T("还没有命令历史"),
                    filtered
                        ? L10n.T("换个关键词，或把主机过滤切回“所有主机”")
                        : L10n.T("在终端里执行过的命令会自动记录在这里，可按命令内容或主机过滤"));
            }
        }

        /// <summary>
        /// 更新主机过滤器下拉列表
        /// </summary>
        public void UpdateHostFilter(System.Collections.Generic.IList<CommandHistoryEntry> entries)
        {
            _hostFilter.Items.Clear();
            _hostFilter.Items.Add(L10n.T("所有主机"));

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
                L10n.T("确定要清空命令历史记录吗？此操作不可撤销。"),
                L10n.T("确认清空"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            try
            {
                // 原来这里只有 RefreshHistory()：磁盘上一条没删，用户以为清干净了
                int removed = _store.ClearAll();
                RefreshHistory();
                _statusLabel.Text = removed > 0 ? L10n.T("已清空 ") + removed + L10n.T(" 个历史文件") : L10n.T("历史已经是空的");
                ToastNotifier.Success(removed > 0 ? L10n.T("命令历史已清空") : L10n.T("命令历史已经是空的"));
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("CommandHistory.Clear", ex); } catch (Exception) { }
                ToastNotifier.Error(L10n.T("清空失败：") + ex.Message);
            }
        }

        /// <summary>多选批量删除选中记录（F6；与「清空」不同——只删选中的，确认框带数量）。</summary>
        private void DeleteSelected()
        {
            var entries = new System.Collections.Generic.List<CommandHistoryEntry>();
            foreach (ListViewItem it in _historyList.SelectedItems)
            {
                var en = it.Tag as CommandHistoryEntry;
                if (en != null) entries.Add(en);
            }
            if (entries.Count == 0) return;

            var n = entries.Count;
            if (MessageBox.Show(
                    L10n.Tf("确定删除选中的 {0} 条命令记录吗？此操作不可撤销。", n),
                    L10n.T("确认删除"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            try
            {
                var removed = _store.DeleteByIds(entries.Select(e => e.Id));
                foreach (var en in entries) TrashStore.Default.Add("commandhistory", L10n.T("命令历史"), en.Command, en);
                RefreshHistory();
                _statusLabel.Text = L10n.Tf("已删除 {0} 条记录", removed);
                ToastNotifier.Success(L10n.Tf("已删除 {0} 条命令记录", removed));
            }
            catch (Exception ex)
            {
                try { DiagLog.Error("CommandHistory.DeleteSelected", ex); } catch (Exception) { }
                ToastNotifier.Error(L10n.T("删除失败：") + ex.Message);
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
