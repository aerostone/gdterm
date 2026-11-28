using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Forms;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    public class PortForwardPanel : UserControl
    {
        private readonly PortForwardWorkspace _workspace;
        private readonly bool _ownsWorkspace;
        private ListView _lvRules;
        private ListShell _shell;
        private EmptyStateView _empty;
        private AntdUI.Button _edit, _start, _stop, _delete;
        private AntdUI.Label _result;
        private bool _refreshing;
        private List<ListViewItem> _rows = new List<ListViewItem>();

        // Isolated preview/test constructor. Production always supplies the application workspace.
        public PortForwardPanel(PortForwardManager manager)
            : this(new PortForwardWorkspace(null, manager, null, null, null)) { _ownsWorkspace = true; }
        public PortForwardPanel(PortForwardWorkspace workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            Dock = DockStyle.Fill; BackColor = GdtermColorTable.Background;
            BuildUI();
            _workspace.Changed += OnWorkspaceChanged;
            RefreshList();
        }
        private void BuildUI()
        {
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 6), DpiScale.V(this, 8), 0)
            };
            var add = Button(L10n.T("添加规则"), "ForwardAdd");
            _edit = Button(L10n.T("编辑"), "ForwardEdit");
            _start = Button(L10n.T("启动"), "ForwardStart");
            _stop = Button(L10n.T("停止"), "ForwardStop");
            _delete = Button(L10n.T("删除"), "ForwardDelete");
            add.Type = AntdUI.TTypeMini.Primary;
            add.Click += (s, e) => AddRule();
            _edit.Click += (s, e) => EditSelected();
            _start.Click += async (s, e) => await RunSelected("start");
            _stop.Click += async (s, e) => await RunSelected("stop");
            _delete.Click += async (s, e) => await DeleteSelected();
            toolbar.Controls.AddRange(new Control[] { add, _edit, _start, _stop, _delete });
            _lvRules = new ListView
            {
                Name = "ForwardRules", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                CheckBoxes = true, HideSelection = false, MultiSelect = true, ShowItemToolTips = true,
                BackColor = GdtermColorTable.Background, ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont(), AccessibleName = L10n.T("端口转发规则列表")
            };
            foreach (var col in new[] { L10n.T("名称"), L10n.T("状态"), L10n.T("所属 SSH 连接"), L10n.T("类型"), L10n.T("本地地址"), L10n.T("远程地址"), L10n.T("说明"), L10n.T("错误原因") })
                _lvRules.Columns.Add(col, DpiScale.V(this, col == L10n.T("名称") ? 130 : 150));
            _lvRules.SelectedIndexChanged += (s, e) => UpdateActions();
            _lvRules.ItemChecked += (s, e) => UpdateActions();
            _lvRules.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; await DeleteSelected(); }
                else if (e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; EditSelected(); }
            };
            _shell = new ListShell(_lvRules, L10n.T("过滤名称/地址/说明…"));
            _shell.AddAction(L10n.T("导出"), (s, e) => _shell.ExportCsv(FindForm(), "port-forward")).Name = "ForwardExportButton";
            // ListShell owns Enter/DoubleClick dispatch; one activation opens one editor.
            _shell.ItemActivated += (s, item) => EditSelected();
            _lvRules.Layout += (s, e) => UpdateActions();
            _result = new AntdUI.Label
            {
                Name = "ForwardResult", Dock = DockStyle.Bottom,
                Height = FormFontPolicy.LineBox(this) + DpiScale.V(this, 12),
                Padding = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 6), DpiScale.V(this, 8), DpiScale.V(this, 6)),
                Font = FormFontPolicy.UiFont(), ForeColor = GdtermColorTable.Muted, AutoEllipsis = true,
                Text = L10n.T("勾选优先；双击编辑。关闭面板不会停止转发。")
            };
            Controls.Add(_shell); Controls.Add(_result); Controls.Add(toolbar);
            _empty = EmptyStateView.Attach(this, L10n.T("还没有端口转发规则"),
                L10n.T("创建本地、远程或 SOCKS5 转发，保存后手动启动。"), L10n.T("新建规则"), (s, e) => AddRule());
            UiA11y.AutoName(this);
        }
        private AntdUI.Button Button(string text, string name)
        {
            var button = new AntdUI.Button { Text = text, Name = name,
                Margin = new Padding(0, 0, DpiScale.V(this, 8), DpiScale.V(this, 6)),
                BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, Font = FormFontPolicy.UiFont(-0.5f) };
            FormFontPolicy.SizeActionButton(this, button, 10, 4); return button;
        }
        private void OnWorkspaceChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) RefreshList(); })); }
                catch (InvalidOperationException) { }
            }
            else RefreshList();
        }
        protected override void OnLoad(EventArgs e) { base.OnLoad(e); RefreshList(); }
        private void RefreshList()
        {
            if (_refreshing || IsDisposed) return;
            _refreshing = true;
            try
            {
                var selected = new HashSet<string>(_rows.Where(i => i.Selected).Select(i => ((PortForwardRule)i.Tag).Id));
                var checks = new HashSet<string>(_rows.Where(i => i.Checked).Select(i => ((PortForwardRule)i.Tag).Id));
                string top = (_lvRules.TopItem?.Tag as PortForwardRule)?.Id;
                var rules = _workspace.Rules;
                _rows = new List<ListViewItem>();
                foreach (var rule in rules)
                {
                    var state = _workspace.Manager.GetStatus(rule.Id);
                    var error = _workspace.Error(rule.Id) ?? "";
                    var item = new ListViewItem(rule.Name ?? "") { Tag = rule, Checked = checks.Contains(rule.Id) };
                    item.SubItems.Add(StateText(state.State));
                    item.SubItems.Add(rule.ConnectionName ?? rule.ConnectionId ?? L10n.T("未选择连接"));
                    item.SubItems.Add(rule.Type == PortForwardType.Dynamic ? "SOCKS5" : rule.Type.ToString());
                    item.SubItems.Add(Endpoint(rule.LocalHost, rule.LocalPort));
                    item.SubItems.Add(rule.Type == PortForwardType.Dynamic ? "—" : Endpoint(rule.RemoteHost, rule.RemotePort));
                    item.SubItems.Add(rule.Description ?? ""); item.SubItems.Add(error);
                    item.ToolTipText = error.Length > 0 ? error : rule.Description;
                    if (error.Length > 0) item.ForeColor = GdtermColorTable.Danger;
                    else if (state.State == PortForwardState.Running) item.ForeColor = GdtermColorTable.Success;
                    _rows.Add(item);
                }
                _shell.SetItems(_rows);
                foreach (var item in _rows)
                    if (item.ListView != null) item.Selected = selected.Contains(((PortForwardRule)item.Tag).Id);
                var topItem = _rows.Find(i => ((PortForwardRule)i.Tag).Id == top && i.ListView != null);
                if (topItem != null) _lvRules.TopItem = topItem;
                _empty?.SetEmpty(rules.Count == 0);
            }
            finally { _refreshing = false; }
            UpdateActions();
        }
        private static string Endpoint(string host, int port) => (host != null && host.Contains(":") ? "[" + host + "]" : host) + ":" + port;
        private static string StateText(PortForwardState state)
        {
            switch (state)
            {
                case PortForwardState.Starting: return L10n.T("启动中…");
                case PortForwardState.Running: return L10n.T("● 运行");
                case PortForwardState.Stopping: return L10n.T("停止中…");
                case PortForwardState.Failed: return L10n.T("失败（可重试）");
                default: return L10n.T("○ 停止");
            }
        }
        private List<PortForwardRule> Targets()
        {
            var visible = _lvRules.Items.Cast<ListViewItem>().ToList();
            var checkedRows = visible.Where(i => i.Checked).ToList();
            return (checkedRows.Count > 0 ? checkedRows : visible.Where(i => i.Selected)).Select(i => (PortForwardRule)i.Tag).ToList();
        }
        private void UpdateActions()
        {
            if (_refreshing || _edit == null || _lvRules == null) return;
            var targets = Targets();
            _edit.Enabled = targets.Count == 1 && _workspace.CanEdit(targets[0].Id);
            _start.Enabled = targets.Any(r => _workspace.CanEdit(r.Id));
            _stop.Enabled = targets.Any(r => !_workspace.IsDeleting(r.Id) &&
                (_workspace.Manager.GetStatus(r.Id).State == PortForwardState.Starting || _workspace.Manager.IsActive(r.Id)));
            _delete.Enabled = targets.Any(r => !_workspace.IsDeleting(r.Id));
            if (_result != null && targets.Count == 1 && !string.IsNullOrEmpty(_workspace.Error(targets[0].Id)))
                _result.Text = _workspace.Error(targets[0].Id);
        }
        private void AddRule() { ShowEditor(null); }
        private void EditSelected()
        {
            var targets = Targets();
            if (targets.Count == 1 && _workspace.CanEdit(targets[0].Id)) ShowEditor(targets[0]);
        }
        private void ShowEditor(PortForwardRule rule)
        {
            try
            {
                using (var form = new PortForwardRuleForm(_workspace.Connections, rule, _workspace.Save))
                    form.ShowDialog(this);
            }
            catch (Exception ex) { if (!IsDisposed) _result.Text = ex.Message; }
        }
        private async Task DeleteSelected()
        {
            var targets = Targets().Where(r => !_workspace.IsDeleting(r.Id)).ToList();
            if (targets.Count == 0) return;
            if (MessageBox.Show(FindForm(), L10n.Tf("删除 {0} 条转发规则？运行中的转发会先停止。", targets.Count),
                L10n.T("删除规则"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await RunTargets(targets, "delete");
        }
        private Task RunSelected(string action)
        {
            var targets = Targets().Where(r => action != "start" || _workspace.CanEdit(r.Id)).ToList();
            return RunTargets(targets, action);
        }
        private async Task RunTargets(List<PortForwardRule> targets, string action)
        {
            if (targets.Count == 0) return;
            try
            {
                var tasks = targets.Select(r => action == "start" ? _workspace.StartAsync(r.Id) :
                    action == "stop" ? _workspace.StopAsync(r.Id) : _workspace.DeleteAsync(r.Id)).ToArray();
                UpdateActions();
                var results = await Task.WhenAll(tasks);
                if (IsDisposed || Disposing) return;
                _result.Text = L10n.Tf("完成 {0} 条，失败 {1} 条；原因见列表。", results.Count(x => x), results.Count(x => !x));
                RefreshList();
            }
            catch (Exception ex) { if (!IsDisposed && !Disposing) _result.Text = ex.Message; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _workspace.Changed -= OnWorkspaceChanged;
                if (_ownsWorkspace) _workspace.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
