using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Security;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 修改主密码对话框（AntdUI 版）。
    /// 流程：旧密码验证 -> 新密码强度校验 -> 确认 -> 触发 <see cref="ChangeRequested"/>。
    /// 调用方在事件里负责：(1) KeePass 重加密 kdbx (2) SecurityManager.SetMasterPassword
    /// (3) 持久化 master-password.ini (4) 更新内存主密码
    /// 任一步失败需 throw，由本对话框捕获并回滚 UI。
    /// </summary>
    public class ChangeMasterPasswordForm : AntdUI.Window
    {
        private readonly ISecurityManager _securityManager;

        private AntdUI.Input _oldBox;
        private AntdUI.Input _newBox;
        private AntdUI.Input _confirmBox;
        private AntdUI.Label _strengthLabel;
        private AntdUI.Label _errorLabel;
        private AntdUI.Checkbox _showPwdCheck;
        private AntdUI.Button _okButton;

        /// <summary>
        /// 用户点击确定且本地校验通过时触发。
        /// EventArgs: OldPassword, NewPassword。
        /// 处理器抛异常表示失败（调用方负责回滚已执行的步骤）。
        /// </summary>
        public event EventHandler<ChangeMasterPasswordEventArgs> ChangeRequested;

        /// <summary>是否成功完成修改（处理器未抛异常）。</summary>
        public bool IsChanged { get; private set; }

        public ChangeMasterPasswordForm(ISecurityManager securityManager)
        {
            _securityManager = securityManager;
            InitializeComponent();
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导

            try
            {
                var iconStream = typeof(ChangeMasterPasswordForm).Assembly
                    .GetManifestResourceStream("Gdterm.UI.Resources.gdterm.ico");
                if (iconStream != null)
                {
                    Icon = new Icon(iconStream);
                    iconStream.Dispose();
                }
            }
            catch { }
        }

        private void InitializeComponent()
        {
            Text = "修改主密码";
            Size = new Size(500, 470);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int labelX = 20;
            int boxX = 120;
            int boxWidth = 340;
            int rowH = 46;
            int y = 20;

            var titleLabel = new AntdUI.Label {
                Text = "修改主密码",
                Font = Gdterm.UI.Services.FormFontPolicy.UiFont(+6f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(labelX, y)
            };
            y += 46;

            var tipLabel = new AntdUI.Label {
                Text = "修改后，KeePass 密码库 (gdterm.kdbx) 将用新主密码重新加密。\n请妥善保管新密码，丢失将无法找回。",
                AutoSize = true,
                Location = new Point(labelX, y)
            };
            y += 58;

            Controls.Add(MakeFieldLabel("当前密码", labelX, y));
            _oldBox = MakePasswordBox(boxX, y, boxWidth);
            y += rowH;

            Controls.Add(MakeFieldLabel("新密码", labelX, y));
            _newBox = MakePasswordBox(boxX, y, boxWidth);
            _newBox.TextChanged += OnNewPasswordChanged;
            y += rowH;

            Controls.Add(MakeFieldLabel("确认新密码", labelX, y));
            _confirmBox = MakePasswordBox(boxX, y, boxWidth);
            y += rowH + 2;

            _strengthLabel = new AntdUI.Label {
                Text = "密码强度：未输入",
                AutoSize = true,
                Location = new Point(boxX, y)
            };
            Controls.Add(_strengthLabel);
            y += rowH;

            _errorLabel = new AntdUI.Label {
                Text = "",
                AutoSize = true,
                ForeColor = GdtermColorTable.Danger,
                Location = new Point(labelX, y)
            };
            Controls.Add(_errorLabel);
            y += rowH;

            _showPwdCheck = new AntdUI.Checkbox {
                Text = "显示密码",
                AutoSize = true,
                Location = new Point(labelX, y)
            };
            _showPwdCheck.CheckedChanged += (s, e) =>
            {
                _oldBox.UseSystemPasswordChar = !_showPwdCheck.Checked;
                _newBox.UseSystemPasswordChar = !_showPwdCheck.Checked;
                _confirmBox.UseSystemPasswordChar = !_showPwdCheck.Checked;
            };
            Controls.Add(_showPwdCheck);
            y += rowH + 4;

            _okButton = new AntdUI.Button {
                Text = "确认修改",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(100, 38),
                Location = new Point(500 - 20 - 100 - 8 - 90, y)
            };
            _okButton.Click += OnOkClick;
            Controls.Add(_okButton);

            var cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(90, 38),
                Location = new Point(500 - 20 - 90, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            AcceptButton = _okButton;
            CancelButton = cancelButton;
        }

        private static AntdUI.Label MakeFieldLabel(string text, int x, int y)
        {
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + 10) };
        }

        private AntdUI.Input MakePasswordBox(int x, int y, int width)
        {
            return new AntdUI.Input {
                Location = new Point(x, y),
                Size = new Size(width, 38),
                Font = new Font("Consolas", 11f),
                UseSystemPasswordChar = true
            };
        }

        private void OnNewPasswordChanged(object sender, EventArgs e)
        {
            var pwd = _newBox.Text;
            if (string.IsNullOrEmpty(pwd))
            {
                _strengthLabel.Text = "密码强度：未输入";
                _strengthLabel.ForeColor = GdtermColorTable.Muted;
                return;
            }

            int score = 0;
            if (pwd.Length >= 12) score++;
            if (pwd.Length >= 16) score++;
            if (pwd.Any(char.IsUpper)) score++;
            if (pwd.Any(char.IsLower)) score++;
            if (pwd.Any(char.IsDigit)) score++;
            if (pwd.Any(ch => !char.IsLetterOrDigit(ch))) score++;

            string strength;
            Color color;
            if (score <= 2) { strength = "弱"; color = GdtermColorTable.Danger; }
            else if (score <= 4) { strength = "中"; color = GdtermColorTable.Warning; }
            else { strength = "强"; color = GdtermColorTable.Success; }

            _strengthLabel.Text = $"密码强度：{strength}（{pwd.Length} 字符）";
            _strengthLabel.ForeColor = color;
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            _errorLabel.Text = "";

            var oldPw = _oldBox.Text;
            var newPw = _newBox.Text;
            var confirmPw = _confirmBox.Text;

            if (string.IsNullOrEmpty(oldPw))
            {
                _errorLabel.Text = "请输入当前密码";
                _oldBox.Focus();
                return;
            }

            // 先验证当前密码正确（不改变锁定状态）
            if (_securityManager != null && !_securityManager.VerifyMasterPassword(oldPw))
            {
                _errorLabel.Text = "当前密码不正确";
                _oldBox.Focus();
                return;
            }

            if (string.IsNullOrEmpty(newPw))
            {
                _errorLabel.Text = "新密码不能为空";
                _newBox.Focus();
                return;
            }

            if (newPw != confirmPw)
            {
                _errorLabel.Text = "两次输入的新密码不一致";
                _confirmBox.Focus();
                return;
            }

            if (newPw == oldPw)
            {
                _errorLabel.Text = "新密码不能与当前密码相同";
                _newBox.Focus();
                return;
            }

            // 触发调用方处理器：重加密 kdbx + 更新 SecurityManager + 持久化 ini
            // 同步异常表示失败，UI 保留对话框；异步处理器（finding-06）的失败
            // 由处理器内部接住并弹窗，此处无法感知——IsChanged 仅反映同步阶段结果。
            try
            {
                var handler = ChangeRequested;
                if (handler != null)
                {
                    handler(this, new ChangeMasterPasswordEventArgs(oldPw, newPw));
                }
                IsChanged = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (WeakPasswordException ex)
            {
                _errorLabel.Text = "新密码不符合要求：" + (ex.Violations != null && ex.Violations.Count > 0
                    ? string.Join("; ", ex.Violations)
                    : ex.Message);
                _newBox.Focus();
            }
            catch (Exception ex)
            {
                _errorLabel.Text = "修改失败：" + ex.Message;
            }
        }
    }

    /// <summary>修改主密码事件参数。</summary>
    public sealed class ChangeMasterPasswordEventArgs : EventArgs
    {
        public string OldPassword { get; }
        public string NewPassword { get; }

        public ChangeMasterPasswordEventArgs(string oldPassword, string newPassword)
        {
            OldPassword = oldPassword;
            NewPassword = newPassword;
        }
    }
}
