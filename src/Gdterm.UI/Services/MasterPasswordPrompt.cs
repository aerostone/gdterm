using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 敏感操作前主密码再验证对话框（不改变锁状态）。
    /// </summary>
    public static class MasterPasswordPrompt
    {
        /// <summary>
        /// 弹出暗色主密码验证框。应用已锁定时直接失败。
        /// </summary>
        public static bool Confirm(IWin32Window owner, ISecurityManager securityManager, string action)
        {
            if (securityManager == null) return false;

            if (securityManager.IsLocked)
            {
                MessageBox.Show(owner, "应用已锁定，请先解锁", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            using (var dialog = new Form())
            {
                dialog.Text = "安全验证";
                dialog.Size = new Size(380, 180);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.BackColor = Color.FromArgb(35, 35, 35);

                var label = new Label
                {
                    Text = (action ?? "操作") + "需要验证主密码：",
                    Font = Services.FormFontPolicy.UiFont(+1f),
                    ForeColor = Color.FromArgb(200, 200, 200),
                    Location = new Point(15, 15),
                    Size = new Size(340, 25)
                };
                var pwdBox = new TextBox
                {
                    Location = new Point(15, 45),
                    Size = new Size(335, 28),
                    Font = new Font("Consolas", 11f),
                    UseSystemPasswordChar = true,
                    BackColor = Color.FromArgb(50, 50, 50),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                var errorLabel = new Label
                {
                    Text = "",
                    Font = Services.FormFontPolicy.UiFont(),
                    ForeColor = Color.FromArgb(255, 100, 100),
                    Location = new Point(15, 78),
                    Size = new Size(335, 20)
                };
                var okBtn = new Button
                {
                    Text = "验证",
                    Size = new Size(80, 32),
                    Location = new Point(270, 105),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(0, 122, 204),
                    ForeColor = Color.White
                };
                okBtn.Click += (s, ev) =>
                {
                    if (securityManager.VerifyMasterPassword(pwdBox.Text))
                    {
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();
                    }
                    else
                    {
                        errorLabel.Text = "密码不正确";
                        pwdBox.SelectAll();
                        pwdBox.Focus();
                    }
                };
                pwdBox.KeyDown += (s, ev) =>
                {
                    if (ev.KeyCode == Keys.Enter) okBtn.PerformClick();
                };
                dialog.Controls.AddRange(new Control[] { label, pwdBox, errorLabel, okBtn });
                dialog.AcceptButton = okBtn;
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}
