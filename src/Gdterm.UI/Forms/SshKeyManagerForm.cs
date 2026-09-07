using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Gdterm.KeePass;
using Gdterm.KeePass.Models;
using Gdterm.UI.Controls;
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
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = DpiScale.S(this, 600, 520);
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导
            BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
            ForeColor = Gdterm.UI.Diagnostics.GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定坐标下预览框与底部按钮重叠）
            int clientW = DpiScale.V(this, 600);
            int pad = DpiScale.V(this, 20);
            int boxX = DpiScale.V(this, 120);
            int boxW = clientW - boxX - pad;
            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            int rowH = fieldH + DpiScale.V(this, 12);
            int y = DpiScale.V(this, 20);
            _title = Labeled(ref y, "条目标题", "SSH Key", boxX, boxW, fieldH, rowH);
            _user = Labeled(ref y, "用户名", "root", boxX, boxW, fieldH, rowH);
            _host = Labeled(ref y, "主机名", "", boxX, boxW, fieldH, rowH);
            _passphrase = Labeled(ref y, "密钥口令", "", boxX, boxW, fieldH, rowH);
            _passphrase.UseSystemPasswordChar = true;

            Controls.Add(MakeLabel("私钥文件", pad, y, fieldH));
            _keyPath = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW - DpiScale.V(this, 100), fieldH)
            };
            Controls.Add(_keyPath);
            var browse = new AntdUI.Button {
                Text = "浏览…",
                Location = new Point(boxX + boxW - DpiScale.V(this, 90), y),
                Size = new Size(DpiScale.V(this, 90), fieldH)
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
            y += rowH;

            Controls.Add(MakeLabel("预览", pad, y, fieldH));
            int previewH = DpiScale.V(this, 190);
            _preview = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, previewH),
                Multiline = true,
                ReadOnly = true,
                Font = new Font("Consolas", 8.5f)
            };
            Controls.Add(_preview);
            y += previewH + DpiScale.V(this, 16);

            // 底部按钮：主按钮最右，行位置由 y 流式推导（修复：原先固定 y=440 与预览框重叠）
            int btnW = DpiScale.V(this, 130);
            int btnW2 = DpiScale.V(this, 88);
            int btnGap = DpiScale.V(this, 8);
            var cancel = new AntdUI.Button {
                Text = "关闭",
                Location = new Point(clientW - pad - btnW2, y),
                Size = new Size(btnW2, fieldH)
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            var ok = new AntdUI.Button {
                Text = "导入到密码库",
                Type = AntdUI.TTypeMini.Primary,
                Location = new Point(clientW - pad - btnW - btnGap - btnW2, y),
                Size = new Size(btnW, fieldH)
            };
            ok.Click += OnImport;
            Controls.Add(ok);

            ClientSize = new Size(clientW, y + fieldH + DpiScale.V(this, 16));
            CancelButton = cancel;
        }

        private static AntdUI.Label MakeLabel(string text, int x, int y, int fieldH)
        {
            int offset = Math.Max(4, (fieldH - 17) / 2);
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + offset) };
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

        private AntdUI.Input Labeled(ref int y, string label, string value, int boxX, int boxW, int fieldH, int rowH)
        {
            Controls.Add(MakeLabel(label, DpiScale.V(this, 20), y, fieldH));
            var tb = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, fieldH),
                Text = value ?? ""
            };
            Controls.Add(tb);
            y += rowH;
            return tb;
        }
    }
}
