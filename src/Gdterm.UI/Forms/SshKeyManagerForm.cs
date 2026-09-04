using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// SSH 私钥导入并写入 KeePass 附件（AntdUI 版）。
    /// </summary>
    public sealed class SshKeyManagerForm : AntdUI.Window
    {
        private readonly IKeePassService _keepass;
        private readonly AntdUI.Input _title;
        private readonly AntdUI.Input _user;
        private readonly AntdUI.Input _host;
        private readonly AntdUI.Input _passphrase;
        private readonly AntdUI.Input _keyPath;
        private readonly AntdUI.Input _preview;

        public SshKeyManagerForm(IKeePassService keepass)
        {
            _keepass = keepass;
            Text = "SSH 密钥管理";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(600, 520);

            int y = 20;
            _title = Labeled(ref y, "条目标题", "SSH Key");
            _user = Labeled(ref y, "用户名", "root");
            _host = Labeled(ref y, "主机名", "");
            _passphrase = Labeled(ref y, "密钥口令", "");
            _passphrase.UseSystemPasswordChar = true;

            Controls.Add(MakeLabel("私钥文件", 20, y));
            _keyPath = new AntdUI.Input
            {
                Location = new Point(120, y),
                Size = new Size(330, 38)
            };
            Controls.Add(_keyPath);
            var browse = new AntdUI.Button
            {
                Text = "浏览…",
                Location = new Point(460, y),
                Size = new Size(90, 38)
            };
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
            Controls.Add(browse);
            y += 50;

            Controls.Add(MakeLabel("预览", 20, y));
            _preview = new AntdUI.Input
            {
                Location = new Point(120, y),
                Size = new Size(430, 190),
                Multiline = true,
                ReadOnly = true,
                Font = new Font("Consolas", 8.5f)
            };
            Controls.Add(_preview);

            var ok = new AntdUI.Button
            {
                Text = "导入到密码库",
                Type = AntdUI.TTypeMini.Primary,
                Location = new Point(300, 440),
                Size = new Size(130, 38)
            };
            ok.Click += OnImport;
            Controls.Add(ok);

            var cancel = new AntdUI.Button
            {
                Text = "关闭",
                Location = new Point(442, 440),
                Size = new Size(88, 38)
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        private static AntdUI.Label MakeLabel(string text, int x, int y)
        {
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + 10) };
        }

        private void OnImport(object sender, EventArgs e)
        {
            if (_keepass == null || !_keepass.IsUnlocked)
            {
                AntdUI.Message.warn(this, "请先解锁密码库。");
                return;
            }
            if (string.IsNullOrWhiteSpace(_keyPath.Text) || !File.Exists(_keyPath.Text))
            {
                AntdUI.Message.warn(this, "请选择有效的私钥文件。");
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
                AntdUI.Message.error(this, "导入失败: " + ex.Message);
            }
        }

        private AntdUI.Input Labeled(ref int y, string label, string value)
        {
            Controls.Add(MakeLabel(label, 20, y));
            var tb = new AntdUI.Input
            {
                Location = new Point(120, y),
                Size = new Size(430, 38),
                Text = value ?? ""
            };
            Controls.Add(tb);
            y += 50;
            return tb;
        }
    }
}
