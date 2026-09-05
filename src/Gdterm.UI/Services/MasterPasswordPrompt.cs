using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

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
                dialog.Size = DpiScale.S(dialog, 380, 180);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.BackColor = GdtermColorTable.Surface;

                var label = new Label
                {
                    Text = (action ?? "操作") + "需要验证主密码：",
                    Font = Services.FormFontPolicy.UiFont(+1f),
                    ForeColor = GdtermColorTable.Foreground,
                    Location = DpiScale.P(dialog, 15, 15),
                    Size = DpiScale.S(dialog, 340, 25)
                };
                var pwdBox = new TextBox
                {
                    Location = DpiScale.P(dialog, 15, 45),
                    Size = DpiScale.S(dialog, 335, 28),
                    Font = new Font("Consolas", DpiScale.Factor(dialog) * 11f),
                    UseSystemPasswordChar = true,
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                var errorLabel = new Label
                {
                    Text = "",
                    Font = Services.FormFontPolicy.UiFont(),
                    ForeColor = GdtermColorTable.Danger,
                    Location = DpiScale.P(dialog, 15, 78),
                    Size = DpiScale.S(dialog, 335, 20)
                };
                var okBtn = new Button
                {
                    Text = "验证",
                    Size = DpiScale.S(dialog, 80, 32),
                    Location = DpiScale.P(dialog, 270, 105),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = GdtermColorTable.Accent,
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
