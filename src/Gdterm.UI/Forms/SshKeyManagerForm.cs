using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Controls;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// SSH 私钥导入并写入 KeePass 附件。
    /// </summary>
    public sealed class SshKeyManagerForm : Form
    {
        private readonly IKeePassService _keepass;
        private readonly TextBox _title;
        private readonly TextBox _user;
        private readonly TextBox _host;
        private readonly TextBox _passphrase;
        private readonly TextBox _keyPath;
        private readonly TextBox _preview;

        public SshKeyManagerForm(IKeePassService keepass)
        {
            _keepass = keepass;
            Text = "SSH 密钥管理";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = DpiScale.S(this, 560, 420);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = Services.FormFontPolicy.UiFont();

            int y = 16;
            _title = Labeled(ref y, "条目标题", "SSH Key");
            _user = Labeled(ref y, "用户名", "root");
            _host = Labeled(ref y, "主机名", "");
            _passphrase = Labeled(ref y, "密钥口令", "");
            _passphrase.UseSystemPasswordChar = true;

            var pathLbl = new Label { Text = "私钥文件", Location = DpiScale.P(this, 16, y), Size = DpiScale.S(this, 90, 22), ForeColor = GdtermColorTable.Muted, TextAlign = ContentAlignment.MiddleRight };
            _keyPath = new TextBox { Location = DpiScale.P(this, 116, y), Size = DpiScale.S(this, 320, 24), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, BorderStyle = BorderStyle.FixedSingle };
            var browse = new Button { Text = "浏览…", Location = DpiScale.P(this, 444, y), Size = DpiScale.S(this, 80, 24), FlatStyle = FlatStyle.Flat, BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground };
            browse.FlatAppearance.BorderColor = GdtermColorTable.Border;
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Title = "选择 PEM 私钥", Filter = "密钥文件|*.pem;*.key;id_rsa;id_ed25519;*.*|所有|*.*" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _keyPath.Text = dlg.FileName;
                        try
                        {
                            var raw = File.ReadAllText(dlg.FileName, Encoding.UTF8);
                            _preview.Text = raw.Length > 2000 ? raw.Substring(0, 2000) + "\n…" : raw;
                        }
                        catch (Exception ex) { _preview.Text = ex.Message; }
                    }
                }
            };
            Controls.Add(pathLbl); Controls.Add(_keyPath); Controls.Add(browse);
            y += 34;

            var prevLbl = new Label { Text = "预览", Location = DpiScale.P(this, 16, y), Size = DpiScale.S(this, 90, 22), ForeColor = GdtermColorTable.Muted, TextAlign = ContentAlignment.MiddleRight };
            _preview = new TextBox
            {
                Location = DpiScale.P(this, 116, y),
                Size = DpiScale.S(this, 408, 180),
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 8.5f),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Muted,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(prevLbl); Controls.Add(_preview);

            var ok = new Button { Text = "导入到密码库", Location = DpiScale.P(this, 300, 380), Size = DpiScale.S(this, 120, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0,120,50), ForeColor = Color.White };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += OnImport;
            var cancel = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Location = DpiScale.P(this, 430, 380), Size = DpiScale.S(this, 90, 28), FlatStyle = FlatStyle.Flat, BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground };
            cancel.FlatAppearance.BorderColor = GdtermColorTable.Border;
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            Gdterm.UI.Services.FormFontPolicy.Apply(this); 
        }

        private void OnImport(object sender, EventArgs e)
        {
            if (_keepass == null || !_keepass.IsUnlocked)
            {
                MessageBox.Show(this, "请先解锁密码库。", "SSH 密钥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(_keyPath.Text) || !File.Exists(_keyPath.Text))
            {
                MessageBox.Show(this, "请选择有效的私钥文件。", "SSH 密钥", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                var data = File.ReadAllBytes(_keyPath.Text);
                var entry = new KeePassEntry
                {
                    Title = string.IsNullOrWhiteSpace(_title.Text) ? Path.GetFileName(_keyPath.Text) : _title.Text.Trim(),
                    Username = _user.Text.Trim(),
                    Hostname = _host.Text.Trim(),
                    Protocol = "SSH",
                    SshPrivateKeyData = data,
                    SshPrivateKeyPath = _keyPath.Text,
                    SshPrivateKeyPassphrase = _passphrase.Text,
                    Notes = "Imported SSH private key " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
                if (!KeePassPasswordWarning.ConfirmSaveIfWeak(this, _keepass, entry.SshPrivateKeyPassphrase))
                    return; // 用户取消密钥短语强度警告
                var created = _keepass.CreateEntry(entry);
                ToastNotifier.Success("密钥已导入: " + entry.Title + " (" + (created != null ? created.Id : "") + ")");
                NotificationCenterPanel.Push("KEY", "导入 SSH 密钥 " + entry.Title);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导入失败: " + ex.Message, "SSH 密钥", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private TextBox Labeled(ref int y, string label, string value)
        {
            var lb = new Label { Text = label, Location = DpiScale.P(this, 16, y), Size = DpiScale.S(this, 90, 22), ForeColor = GdtermColorTable.Muted, TextAlign = ContentAlignment.MiddleRight };
            var tb = new TextBox { Location = DpiScale.P(this, 116, y), Size = DpiScale.S(this, 408, 24), Text = value ?? "", BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(lb); Controls.Add(tb);
            y += 34;
            return tb;
        }
    }
}
