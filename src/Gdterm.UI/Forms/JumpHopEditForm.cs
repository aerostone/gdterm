using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Services;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Forms
{
    /// <summary>编辑单个跳板。新凭据只产生草稿，由连接保存入口写入 KeePass。</summary>
    public sealed class JumpHopEditForm : AntdUI.Window
    {
        private readonly IKeePassService _vault;
        private readonly AntdUI.Input _hostBox;
        private readonly AntdUI.InputNumber _portBox;
        private readonly AntdUI.Input _userBox;
        private readonly AntdUI.Input _credentialText;
        private readonly Label _error;
        private readonly AntdUI.Button _pickButton;
        private readonly AntdUI.Button _newButton;
        private string _credentialRef;
        private KeePassEntry _pending;
        public JumpHop Result { get; private set; }

        public JumpHopEditForm(IKeePassService vault, JumpHop hop = null, KeePassEntry pending = null)
        {
            _vault = vault;
            _credentialRef = hop?.CredentialRefId;
            _pending = CloneEntry(pending);
            Text = hop == null ? L10n.T("添加跳板") : L10n.T("编辑跳板");
            Font = FormFontPolicy.UiFont();
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            ClientSize = DpiScale.S(this, 560, 430);
            AutoHandDpi = false;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;

            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 2, Padding = DpiScale.Pad(this, 12, 12, 12, 8) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _hostBox = AddField(content, 0, L10n.T("跳板主机"), new AntdUI.Input { Text = hop?.Host ?? "", Name = "JumpHost" });
            _portBox = AddField(content, 1, L10n.T("跳板端口"), new AntdUI.InputNumber { Minimum = 1, Maximum = 65535,
                Value = hop != null && hop.Port > 0 && hop.Port <= 65535 ? hop.Port : 22, Name = "JumpPort" });
            _userBox = AddField(content, 2, L10n.T("跳板用户"), new AntdUI.Input { Text = hop?.Username ?? "",
                PlaceholderText = L10n.T("留空使用所选凭据的用户名"), Name = "JumpUsername" });
            _credentialText = AddField(content, 3, L10n.T("认证凭据"), new AntdUI.Input { ReadOnly = true, Name = "JumpCredential" });
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            _pickButton = MakeButton(L10n.T("选择凭据…"), "PickJumpCredential", (s, e) => PickCredential());
            _newButton = MakeButton(L10n.T("新建凭据…"), "NewJumpCredential", (s, e) => NewCredential());
            actions.Controls.Add(_pickButton);
            actions.Controls.Add(_newButton);
            content.Controls.Add(actions, 1, 4);
            var hint = new Label { Text = L10n.T("每台跳板分别选择密码或 SSH 私钥。新凭据随连接保存，取消连接不会保存草稿。"),
                AutoSize = true, Dock = DockStyle.Top, ForeColor = GdtermColorTable.Muted,
                Margin = DpiScale.Pad(this, 0, 8, 0, 8) };
            content.Controls.Add(hint, 0, 5); content.SetColumnSpan(hint, 2);
            _error = new Label { Name = "JumpValidationError", AutoSize = true, Dock = DockStyle.Top,
                ForeColor = GdtermColorTable.Danger, Visible = false, Margin = DpiScale.Pad(this, 0, 4, 0, 4) };
            content.Controls.Add(_error, 0, 6); content.SetColumnSpan(_error, 2);
            content.SizeChanged += (s, e) => {
                int width = Math.Max(100, content.ClientSize.Width - content.Padding.Horizontal - 8);
                hint.MaximumSize = _error.MaximumSize = new Size(width, 0);
            };
            hint.MaximumSize = _error.MaximumSize = DpiScale.S(this, 500, 0);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scroll.Controls.Add(content);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var save = MakeButton(L10n.T("确定"), "SaveJump", (s, e) => Commit());
            save.Type = AntdUI.TTypeMini.Primary;
            var cancel = MakeButton(L10n.T("取消"), "CancelJump", (s, e) => { DialogResult = DialogResult.Cancel; Close(); });
            footer.Controls.Add(save); footer.Controls.Add(cancel);
            FormFontPolicy.StyleDialogActions(this, footer);
            Controls.Add(scroll); Controls.Add(footer);
            AcceptButton = save; CancelButton = cancel;
            RefreshCredential();
            UiA11y.AutoName(this);
        }

        private T AddField<T>(TableLayoutPanel grid, int row, string text, T control) where T : Control
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = DpiScale.Pad(this, 0, 4, 10, 4) };
            control.Dock = DockStyle.Fill;
            control.MinimumSize = new Size(0, FormFontPolicy.FieldHeight(this));
            control.Margin = DpiScale.Pad(this, 0, 4, 0, 4);
            grid.Controls.Add(label, 0, row); grid.Controls.Add(control, 1, row);
            return control;
        }

        private AntdUI.Button MakeButton(string text, string name, EventHandler click)
        {
            var b = new AntdUI.Button { Text = text, Name = name, Type = AntdUI.TTypeMini.Default,
                Margin = DpiScale.Pad(this, 4, 4, 4, 4) };
            FormFontPolicy.SizeActionButton(this, b, 10, 4);
            b.Click += click;
            return b;
        }

        private bool Unlock()
        {
            if (_vault == null) { Error(L10n.T("密码库服务不可用。")); return false; }
            if (_vault.IsUnlocked) return true;
            using (var dialog = new KeePassUnlockForm(_vault))
                return dialog.ShowDialog(this) == DialogResult.OK && _vault.IsUnlocked;
        }

        private void PickCredential()
        {
            if (!Unlock()) return;
            using (var picker = new KeePassEntryPicker(_vault, selectionOnly: true))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                ClearEntry(_pending); _pending = null;
                _credentialRef = picker.SelectedEntryId;
                RefreshCredential();
            }
        }

        private void NewCredential()
        {
            if (!Unlock()) return;
            using (var editor = new KeePassEntryEditForm())
            {
                editor.LoadFrom(_pending ?? new KeePassEntry { Title = _hostBox.Text.Trim(), Hostname = _hostBox.Text.Trim(),
                    Username = _userBox.Text.Trim(), Port = (int)_portBox.Value, Protocol = "SSH",
                    Url = "ssh://" + _hostBox.Text.Trim() + ":" + (int)_portBox.Value });
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                if (!KeePassPasswordWarning.ConfirmSaveIfWeak(this, _vault, editor.SecretForStrengthCheck)) return;
                var entry = editor.CreateEntry();
                ClearEntry(_pending); _pending = entry;
                _credentialRef = null;
                RefreshCredential();
            }
        }

        private void RefreshCredential()
        {
            KeePassEntry entry = _pending;
            try
            {
                if (entry == null && !string.IsNullOrEmpty(_credentialRef) && _vault?.IsUnlocked == true)
                    entry = _vault.GetEntry(_credentialRef);
                _credentialText.Text = entry == null
                    ? (string.IsNullOrEmpty(_credentialRef) ? L10n.T("未选择跳板凭据") : L10n.T("凭据不可用，请解锁密码库或重新选择"))
                    : (_pending != null ? L10n.T("待保存: ") : L10n.T("已选: ")) + entry.Title + " — " + entry.Username
                        + (entry.SshPrivateKeyData?.Length > 0 ? L10n.T("（SSH 私钥）") : L10n.T("（密码）"));
            }
            catch (Exception ex) { DiagLog.Swallowed("JumpHop.Credential", ex); _credentialText.Text = L10n.T("凭据不可用，请解锁密码库或重新选择"); }
            finally { if (!ReferenceEquals(entry, _pending)) ClearEntry(entry); }
        }

        private void Commit()
        {
            if (string.IsNullOrWhiteSpace(_hostBox.Text) || _hostBox.Text.Trim().IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
            { Error(L10n.T("跳板主机不能为空或包含空白字符。")); _hostBox.Focus(); return; }
            if (_pending == null && string.IsNullOrEmpty(_credentialRef))
            { Error(L10n.T("请为跳板选择密码或 SSH 私钥凭据。")); _pickButton.Focus(); return; }
            Result = new JumpHop { Host = _hostBox.Text.Trim(), Port = (int)_portBox.Value,
                Username = _userBox.Text.Trim(), CredentialRefId = _credentialRef };
            DialogResult = DialogResult.OK; Close();
        }

        private void Error(string text) { _error.Text = text; _error.Visible = true; }
        internal KeePassEntry TakePendingCredential() { var entry = _pending; _pending = null; return entry; }
        internal static KeePassEntry CloneEntry(KeePassEntry e)
        {
            if (e == null) return null;
            return new KeePassEntry { Title = e.Title, Username = e.Username, Password = e.Password, Url = e.Url,
                Notes = e.Notes, GroupPath = e.GroupPath, Hostname = e.Hostname, Port = e.Port, Protocol = e.Protocol,
                AutoTypeSequence = e.AutoTypeSequence, SshPrivateKeyData = (byte[])e.SshPrivateKeyData?.Clone(),
                SshPrivateKeyPassphrase = e.SshPrivateKeyPassphrase };
        }
        internal static void ClearEntry(KeePassEntry e)
        {
            if (e == null) return;
            if (e.SshPrivateKeyData != null) Array.Clear(e.SshPrivateKeyData, 0, e.SshPrivateKeyData.Length);
            e.SshPrivateKeyData = null; e.SshPrivateKeyPassphrase = null; e.Password = null;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { ClearEntry(_pending); _pending = null; }
            base.Dispose(disposing);
        }
    }
}
