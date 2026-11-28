using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Forms;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Controls
{
    /// <summary>有序跳板链的编辑与凭据草稿；网络认证由 CredentialResolver/TunnelManager 负责。</summary>
    public sealed class JumpChainEditorControl : UserControl
    {
        private sealed class Draft { public JumpHop Hop; public KeePassEntry Pending; }
        private sealed class Row { public string Order { get; set; } public string Host { get; set; }
            public string User { get; set; } public string Credential { get; set; } }
        private readonly IKeePassService _vault;
        private readonly List<Draft> _drafts = new List<Draft>();
        private readonly AntdUI.Checkbox _enabledCheck;
        private readonly AntdUI.Table _table;
        private readonly Panel _listHost;
        private readonly EmptyStateView _emptyState;
        private readonly Label _hint;
        private readonly AntdUI.Button _addButton, _editButton, _removeButton, _upButton, _downButton;
        private readonly FlowLayoutPanel _actions;
        public event EventHandler Changed;
        public bool UseJumpChain { get { return _enabledCheck.Checked; } set { _enabledCheck.Checked = value; } }

        public JumpChainEditorControl(IKeePassService vault)
        {
            _vault = vault;
            Font = FormFontPolicy.UiFont();
            BackColor = GdtermColorTable.Background;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Name = "JumpChainEditor";
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _enabledCheck = new AntdUI.Checkbox { Text = L10n.T("使用 SSH 隧道（跳板机）"), Name = "EnableJumpChain",
                AutoSize = true, Margin = DpiScale.Pad(this, 0, 4, 0, 4) };
            _table = new AntdUI.Table { Name = "JumpChainTable", Dock = DockStyle.Top,
                Height = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)) * 3,
                RowHeight = Math.Max(DpiScale.V(this, 24), FormFontPolicy.RowStep(this)), BorderWidth = 0,
                Margin = DpiScale.Pad(this, 0, 4, 0, 4) };
            _table.Columns.Add(new AntdUI.Column("Order", L10n.T("顺序")) { Width = "12%" });
            _table.Columns.Add(new AntdUI.Column("Host", L10n.T("跳板主机")) { Width = "34%" });
            _table.Columns.Add(new AntdUI.Column("User", L10n.T("用户名")) { Width = "20%" });
            _table.Columns.Add(new AntdUI.Column("Credential", L10n.T("认证凭据")) { Width = "34%" });
            _table.CellClick += (s, e) => UpdateActions();
            _table.CellDoubleClick += (s, e) => EditSelected();
            _actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            _addButton = Button(L10n.T("添加跳板"), "AddJump", (s, e) => Edit(-1));
            _editButton = Button(L10n.T("编辑"), "EditJump", (s, e) => EditSelected());
            _removeButton = Button(L10n.T("删除"), "RemoveJump", (s, e) => RemoveSelected());
            _upButton = Button(L10n.T("上移"), "MoveJumpUp", (s, e) => MoveSelected(-1));
            _downButton = Button(L10n.T("下移"), "MoveJumpDown", (s, e) => MoveSelected(1));
            _actions.Controls.AddRange(new Control[] { _addButton, _editButton, _removeButton, _upButton, _downButton });
            _hint = new Label { AutoSize = true, Dock = DockStyle.Top, ForeColor = GdtermColorTable.Muted,
                Margin = DpiScale.Pad(this, 0, 4, 0, 4), Name = "JumpChainHint" };
            layout.Controls.Add(_enabledCheck, 0, 0);
            _listHost = new Panel { Dock = DockStyle.Top, Height = _table.Height, Margin = _table.Margin };
            _table.Dock = DockStyle.Fill;
            _listHost.Controls.Add(_table);
            _emptyState = EmptyStateView.Attach(_listHost, L10n.T("尚未添加跳板，请点击“添加跳板”。"));
            layout.Controls.Add(_actions, 0, 1);
            layout.Controls.Add(_listHost, 0, 2);
            layout.Controls.Add(_hint, 0, 3);
            _hint.MaximumSize = DpiScale.S(this, 480, 0);
            layout.SizeChanged += (s, e) => _hint.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - 8), 0);
            Controls.Add(layout);
            _enabledCheck.CheckedChanged += (s, e) => { RefreshRows(); Changed?.Invoke(this, EventArgs.Empty); };
            RefreshRows();
        }

        private AntdUI.Button Button(string text, string name, EventHandler click)
        {
            var b = new AntdUI.Button { Text = text, Name = name, Type = AntdUI.TTypeMini.Default,
                Margin = DpiScale.Pad(this, 4, 3, 4, 3) };
            FormFontPolicy.SizeActionButton(this, b, 10, 4);
            b.Click += click;
            return b;
        }
        private int SelectedIndex => _table.SelectedIndex - 1;
        private static JumpHop Copy(JumpHop h) => new JumpHop { Host = h?.Host, Port = h?.Port ?? 22,
            Username = h?.Username, CredentialRefId = h?.CredentialRefId };

        public void LoadChain(JumpChainConfig chain)
        {
            foreach (var draft in _drafts) JumpHopEditForm.ClearEntry(draft.Pending);
            _drafts.Clear();
            if (chain?.Hops != null)
                foreach (var hop in chain.Hops) _drafts.Add(new Draft { Hop = Copy(hop) });
            UseJumpChain = chain != null;
            RefreshRows();
        }

        public JumpChainConfig GetChain()
        {
            if (!UseJumpChain) return null;
            var hops = new List<JumpHop>();
            foreach (var draft in _drafts) hops.Add(Copy(draft.Hop));
            return new JumpChainConfig { Hops = hops };
        }

        public string ValidationError()
        {
            if (!UseJumpChain) return null;
            if (_drafts.Count == 0) return L10n.T("已启用跳板，请至少添加一台跳板机。");
            for (int i = 0; i < _drafts.Count; i++)
            {
                var d = _drafts[i];
                if (string.IsNullOrWhiteSpace(d.Hop.Host) || d.Hop.Host.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0 || d.Hop.Port < 1 || d.Hop.Port > 65535)
                    return L10n.Tf("第 {0} 台跳板的主机或端口无效。", i + 1);
                if (d.Pending == null && string.IsNullOrEmpty(d.Hop.CredentialRefId))
                    return L10n.Tf("请为第 {0} 台跳板选择密码或 SSH 私钥凭据。", i + 1);
            }
            return null;
        }

        /// <summary>仅由外层连接的保存操作调用。失败回滚本批创建，草稿保留以便重试。</summary>
        public void CommitPendingCredentials()
        {
            if (!UseJumpChain) return;
            var created = new List<KeyValuePair<Draft, string>>();
            try
            {
                foreach (var draft in _drafts)
                {
                    if (draft.Pending == null) continue;
                    if (_vault?.IsUnlocked != true) throw new InvalidOperationException(L10n.T("请先解锁密码库。"));
                    var write = JumpHopEditForm.CloneEntry(draft.Pending);
                    try
                    {
                        var saved = _vault.CreateEntry(write);
                        if (string.IsNullOrEmpty(saved?.Id)) throw new InvalidOperationException(L10n.T("凭据保存失败，请检查密码库后重试。"));
                        created.Add(new KeyValuePair<Draft, string>(draft, saved.Id));
                    }
                    finally { JumpHopEditForm.ClearEntry(write); }
                }
            }
            catch
            {
                foreach (var entry in created)
                {
                    try { _vault.DeleteEntry(entry.Value); }
                    catch (Exception ex)
                    {
                        // The entry remains saved: retain its binding so retry cannot duplicate it.
                        entry.Key.Hop.CredentialRefId = entry.Value;
                        JumpHopEditForm.ClearEntry(entry.Key.Pending);
                        entry.Key.Pending = null;
                        DiagLog.Swallowed("JumpChain.RollbackCredential", ex);
                    }
                }
                throw;
            }
            foreach (var entry in created)
            {
                entry.Key.Hop.CredentialRefId = entry.Value;
                JumpHopEditForm.ClearEntry(entry.Key.Pending);
                entry.Key.Pending = null;
            }
            RefreshRows();
        }

        private void EditSelected() { if (SelectedIndex >= 0 && SelectedIndex < _drafts.Count) Edit(SelectedIndex); }
        private void Edit(int index)
        {
            if (!UseJumpChain) return;
            var old = index >= 0 ? _drafts[index] : null;
            using (var form = new JumpHopEditForm(_vault, old?.Hop, old?.Pending))
            {
                if (form.ShowDialog(this) != DialogResult.OK || form.Result == null) return;
                var draft = new Draft { Hop = form.Result, Pending = form.TakePendingCredential() };
                if (old == null) { _drafts.Add(draft); index = _drafts.Count - 1; }
                else { JumpHopEditForm.ClearEntry(old.Pending); _drafts[index] = draft; }
                RefreshRows(index); Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        private void RemoveSelected()
        {
            int i = SelectedIndex;
            if (i < 0 || i >= _drafts.Count) return;
            JumpHopEditForm.ClearEntry(_drafts[i].Pending);
            _drafts.RemoveAt(i);
            RefreshRows(Math.Min(i, _drafts.Count - 1)); Changed?.Invoke(this, EventArgs.Empty);
        }
        private void MoveSelected(int delta)
        {
            int i = SelectedIndex, next = i + delta;
            if (i < 0 || i >= _drafts.Count || next < 0 || next >= _drafts.Count) return;
            var row = _drafts[i]; _drafts.RemoveAt(i); _drafts.Insert(next, row);
            RefreshRows(next); Changed?.Invoke(this, EventArgs.Empty);
        }
        private void RefreshRows(int selected = -1)
        {
            var rows = new List<Row>();
            for (int i = 0; i < _drafts.Count; i++)
            {
                var d = _drafts[i];
                string title = L10n.T("未选择跳板凭据");
                KeePassEntry entry = d.Pending;
                try
                {
                    if (entry == null && !string.IsNullOrEmpty(d.Hop.CredentialRefId))
                    {
                        title = L10n.T("已绑定凭据");
                        if (_vault?.IsUnlocked == true) entry = _vault.GetEntry(d.Hop.CredentialRefId);
                        if (_vault?.IsUnlocked == true && entry == null) title = L10n.T("凭据已失效");
                    }
                    if (entry != null) title = (d.Pending != null ? L10n.T("待保存: ") : "") + entry.Title
                        + (entry.SshPrivateKeyData?.Length > 0 ? L10n.T("（SSH 私钥）") : L10n.T("（密码）"));
                    rows.Add(new Row { Order = (i + 1).ToString(), Host = d.Hop.Host + ":" + d.Hop.Port,
                        User = string.IsNullOrWhiteSpace(d.Hop.Username) ? (entry?.Username ?? L10n.T("使用凭据用户名")) : d.Hop.Username,
                        Credential = title });
                }
                catch (Exception ex) { DiagLog.Swallowed("JumpChain.Display", ex); rows.Add(new Row { Order = (i + 1).ToString(), Host = d.Hop.Host, Credential = L10n.T("凭据已失效") }); }
                finally { if (!ReferenceEquals(entry, d.Pending)) JumpHopEditForm.ClearEntry(entry); }
            }
            _table.DataSource = rows;
            _listHost.Height = (_table.RowHeight ?? FormFontPolicy.RowStep(this)) * Math.Max(2, Math.Min(5, rows.Count + 1));
            _table.SelectedIndex = selected >= 0 ? selected + 1 : -1;
            _listHost.Visible = _actions.Visible = UseJumpChain;
            _table.Visible = UseJumpChain && _drafts.Count > 0;
            _emptyState.SetEmpty(UseJumpChain && _drafts.Count == 0);
            _hint.Visible = UseJumpChain && _drafts.Count > 0;
            _hint.Text = _drafts.Count == 0 ? L10n.T("尚未添加跳板，请点击“添加跳板”。") : L10n.T("按列表顺序连接，最后一台跳板转发到目标服务器。");
            UpdateActions();
        }
        private void UpdateActions()
        {
            int i = SelectedIndex;
            bool selected = UseJumpChain && i >= 0 && i < _drafts.Count;
            _editButton.Enabled = _removeButton.Enabled = selected;
            _upButton.Enabled = selected && i > 0;
            _downButton.Enabled = selected && i < _drafts.Count - 1;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var draft in _drafts) JumpHopEditForm.ClearEntry(draft.Pending);
            base.Dispose(disposing);
        }
    }
}
