using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Models;
using Gdterm.Terminal;
using TerminalControl = Gdterm.UI.Controls.TerminalControl;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 命令模板库面板——CommandTemplateStore 的 UI：分类筛选 + 搜索 + 变量填充 + 发送 + 存为模板。
    /// 与 SnippetSearchPanel（快捷命令搜索）并列：模板库是带 {host}/{user}/{date}/{prompt} 解析的运维手册，
    /// 快捷命令是底栏一键发送；两者数据源不同、入口不同（模板库走终端菜单）。
    /// 发送链路同样走 TerminalControl.TrySendInput（带危险命令闸门）。
    /// </summary>
    public class CommandTemplatePanel : UserControl
    {
        private readonly CommandTemplateStore _store;
        private readonly Func<TerminalControl> _getActiveTerminal;

        private AntdUI.Input _txtSearch;
        private AntdUI.Select _categoryBox;
        private ListView _lvResults;
        private AntdUI.Label _statusLabel;
        private List<CommandTemplate> _filtered = new List<CommandTemplate>();

        public CommandTemplatePanel(CommandTemplateStore store, Func<TerminalControl> getActiveTerminal)
        {
            _store = store ?? throw new ArgumentNullException("store");
            _getActiveTerminal = getActiveTerminal;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            BuildUI();
            RefreshCategories();
            FilterResults();
        }

        private void BuildUI()
        {
            Font = FormFontPolicy.UiFont();
            int fieldH = FormFontPolicy.FieldHeight(this);

            var topRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(0, 0, 0, DpiScale.V(this, 4))
            };
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiScale.V(this, 130)));

            _txtSearch = new AntdUI.Input
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, fieldH),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                PlaceholderText = "搜索模板：名称 / 命令 / 标签"
            };
            _txtSearch.TextChanged += (s, e) => FilterResults();
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { ExecuteSelected(); e.SuppressKeyPress = true; }
            };
            topRow.Controls.Add(_txtSearch, 0, 0);

            _categoryBox = new AntdUI.Select
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, fieldH),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _categoryBox.SelectedIndexChanged += (s, e) => FilterResults();
            topRow.Controls.Add(_categoryBox, 1, 0);
            Controls.Add(topRow);

            _lvResults = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont()
            };
            _lvResults.Columns.Add("模板", DpiScale.V(this, 150));
            _lvResults.Columns.Add("命令", DpiScale.V(this, 320));
            _lvResults.DoubleClick += (s, e) => ExecuteSelected();
            Controls.Add(_lvResults);

            var toolRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = GdtermColorTable.Surface,
                Padding = new Padding(DpiScale.V(this, 4))
            };
            var btnSend = MkBtn("发送到终端", "变量填充后发往当前终端（过危险命令确认）");
            btnSend.Click += (s, e) => ExecuteSelected();
            var btnSave = MkBtn("存为模板", "把当前输入框内容存成新模板");
            btnSave.Click += (s, e) => SaveCurrentAsTemplate();
            var btnDel = MkBtn("删除", "删除选中模板");
            btnDel.Click += (s, e) => DeleteSelected();
            toolRow.Controls.AddRange(new Control[] { btnSend, btnSave, btnDel });
            Controls.Add(toolRow);

            _statusLabel = new AntdUI.Label
            {
                Text = "Enter 发送 | 双击发送 | {host}/{user}/{date}/{prompt} 自动填充",
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Font = FormFontPolicy.UiFont(-1f)
            };
            Controls.Add(_statusLabel);
        }

        private AntdUI.Button MkBtn(string text, string tip)
        {
            var b = new AntdUI.Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 4), DpiScale.V(this, 10), DpiScale.V(this, 4)),
                Margin = new Padding(0, 0, DpiScale.V(this, 4), 0)
            };
            if (!string.IsNullOrEmpty(tip)) b.ToolTipText2(tip);
            return b;
        }

        private void RefreshCategories()
        {
            try
            {
                _categoryBox.Items.Clear();
                _categoryBox.Items.Add("全部");
                foreach (var c in _store.GetCategories()) _categoryBox.Items.Add(c);
                _categoryBox.SelectedIndex = 0;
            }
            catch { }
        }

        private void FilterResults()
        {
            _lvResults.Items.Clear();
            _filtered.Clear();
            string q = (_txtSearch.Text ?? "").Trim();
            string cat = null;
            try { cat = _categoryBox.SelectedIndex > 0 ? (string)_categoryBox.SelectedValue : null; }
            catch { cat = null; }
            IEnumerable<CommandTemplate> all;
            try { all = _store.GetAll() ?? new List<CommandTemplate>(); }
            catch { all = new List<CommandTemplate>(); }
            foreach (var t in all)
            {
                if (t == null) continue;
                if (!string.IsNullOrEmpty(cat) && !string.Equals(t.Category, cat, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(q))
                {
                    string tags = t.Tags != null ? string.Join(" ", t.Tags.ToArray()) : "";
                    if ((t.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                        && (t.Command ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                        && tags.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                _filtered.Add(t);
                var item = new ListViewItem(t.Name ?? "");
                item.SubItems.Add(t.Command ?? "");
                item.Tag = t;
                _lvResults.Items.Add(item);
            }
            if (_lvResults.Items.Count > 0) _lvResults.Items[0].Selected = true;
            _statusLabel.Text = "共 " + _filtered.Count + " 个模板";
        }

        private void ExecuteSelected()
        {
            if (_lvResults.SelectedItems.Count == 0) return;
            var t = _lvResults.SelectedItems[0].Tag as CommandTemplate;
            if (t == null) return;
            TerminalControl tc = null;
            try { tc = _getActiveTerminal != null ? _getActiveTerminal() : null; } catch { }
            if (tc == null || tc.Session == null || !tc.Session.IsConnected)
            {
                _statusLabel.Text = "无已连接终端";
                return;
            }
            var sess = tc.Session;
            var ctx = new CommandTemplateContext
            {
                HostName = sess.Hostname,
                UserName = "",
                OsType = sess.OsType,
                Prompt = ""
            };
            string resolved;
            try { resolved = t.ResolveCommand(ctx); }
            catch { resolved = t.Command; }
            // {prompt} 仍是占位意图：弹窗让用户填
            if (resolved != null && resolved.Contains("{prompt}"))
            {
                string v = PromptFor("{prompt}", t.Name);
                if (v == null) return;
                resolved = resolved.Replace("{prompt}", v);
            }
            try
            {
                t.UseCount++;
                _store.Update(t);
            }
            catch { }
            try { tc.TrySendInput(resolved + "\r", isCommandLine: true); _statusLabel.Text = "已发送：" + t.Name; }
            catch (System.Exception exSwallowed) { try { Gdterm.UI.Diagnostics.DiagLog.Swallowed("CmdTemplatePanel", exSwallowed); } catch { } }
        }

        private string PromptFor(string placeholder, string title)
        {
            var form = new Form
            {
                Text = "填写变量 — " + (title ?? ""),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            int fieldH = FormFontPolicy.FieldHeight(form);
            var lbl = new AntdUI.Label
            {
                Text = placeholder + ":",
                Location = new Point(DpiScale.V(form, 15), DpiScale.V(form, 18)),
                AutoSize = true,
                Font = FormFontPolicy.UiFont(),
                ForeColor = GdtermColorTable.Foreground
            };
            var txt = new AntdUI.Input
            {
                Location = DpiScale.P(form, 100, 12),
                Size = new Size(DpiScale.V(form, 260), fieldH),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            var btnOk = new AntdUI.Button { Text = "执行", Type = AntdUI.TTypeMini.Primary, Size = new Size(DpiScale.V(form, 80), fieldH), Location = DpiScale.P(form, 190, 56), DialogResult = DialogResult.OK };
            var btnCancel = new AntdUI.Button { Text = "取消", Size = new Size(DpiScale.V(form, 80), fieldH), Location = DpiScale.P(form, 280, 56), DialogResult = DialogResult.Cancel };
            form.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            form.AcceptButton = btnOk;
            form.CancelButton = btnCancel;
            form.ClientSize = new Size(DpiScale.V(form, 400), DpiScale.V(form, 56) + fieldH + DpiScale.V(form, 16));
            if (form.ShowDialog(this) != DialogResult.OK) return null;
            return txt.Text ?? "";
        }

        private void SaveCurrentAsTemplate()
        {
            string cmd = (_txtSearch.Text ?? "").Trim();
            if (cmd.Length == 0) { _statusLabel.Text = "搜索框为空：先在搜索框输入命令内容"; return; }
            try
            {
                var t = _store.CreateFromHistory(cmd);
                t.Category = "从历史创建";
                _store.Add(t);
                RefreshCategories();
                FilterResults();
                _statusLabel.Text = "已存为模板：" + t.Name;
            }
            catch (System.Exception ex) { _statusLabel.Text = "保存失败：" + ex.Message; }
        }

        private void DeleteSelected()
        {
            if (_lvResults.SelectedItems.Count == 0) return;
            var t = _lvResults.SelectedItems[0].Tag as CommandTemplate;
            if (t == null) return;
            try
            {
                _store.Delete(t.Id);
                FilterResults();
                _statusLabel.Text = "已删除：" + t.Name;
            }
            catch (System.Exception ex) { _statusLabel.Text = "删除失败：" + ex.Message; }
        }
    }
}
