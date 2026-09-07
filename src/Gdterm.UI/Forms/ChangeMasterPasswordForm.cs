using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Gdterm.Security;
using Gdterm.UI.Services;
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
            Size = DpiScale.S(this, 500, 470); // 初始基准，构造末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = Gdterm.UI.Services.FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定像素步进在大字号/高 DPI 下控件重叠、底部按钮被裁剪）
            int clientW = DpiScale.V(this, 500);
            int pad = DpiScale.V(this, 20);
            int labelX = pad;
            int boxX = DpiScale.V(this, 120);
            int boxWidth = clientW - boxX - pad;
            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            int rowH = fieldH + DpiScale.V(this, 8);
            int y = DpiScale.V(this, 20);

            var titleLabel = new AntdUI.Label {
                Text = "修改主密码",
                Font = Gdterm.UI.Services.FormFontPolicy.UiFont(+6f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(labelX, y)
            };
            Controls.Add(titleLabel);
            y += Math.Max(DpiScale.V(this, 46), FormFontPolicy.RowStep(this) + DpiScale.V(this, 10));

            var tipLabel = new AntdUI.Label {
                Text = "修改后，KeePass 密码库 (gdterm.kdbx) 将用新主密码重新加密。\n请妥善保管新密码，丢失将无法找回。",
                AutoSize = true,
                Location = new Point(labelX, y)
            };
            Controls.Add(tipLabel);
            y += Math.Max(DpiScale.V(this, 58), FormFontPolicy.RowStep(this) * 2);

            Controls.Add(MakeFieldLabel("当前密码", labelX, y, fieldH));
            _oldBox = MakePasswordBox(boxX, y, boxWidth, fieldH);
            y += rowH;

            Controls.Add(MakeFieldLabel("新密码", labelX, y, fieldH));
            _newBox = MakePasswordBox(boxX, y, boxWidth, fieldH);
            _newBox.TextChanged += OnNewPasswordChanged;
            y += rowH;

            Controls.Add(MakeFieldLabel("确认新密码", labelX, y, fieldH));
            _confirmBox = MakePasswordBox(boxX, y, boxWidth, fieldH);
            y += rowH;

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
            y += rowH + DpiScale.V(this, 4);

            int btnW = DpiScale.V(this, 100);
            int btnW2 = DpiScale.V(this, 90);
            int btnGap = DpiScale.V(this, 8);
            // 按钮：Windows 惯例主按钮最右（修复：原先确认修改在左侧）
            var cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(btnW2, fieldH),
                Location = new Point(clientW - pad - btnW2, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            _okButton = new AntdUI.Button {
                Text = "确认修改",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(btnW, fieldH),
                Location = new Point(clientW - pad - btnW - btnGap - btnW2, y)
            };
            _okButton.Click += OnOkClick;
            Controls.Add(_okButton);

            // 客户区高度随内容自适应（修复：固定 470 在大字号下裁剪底部按钮）
            ClientSize = new Size(clientW, y + fieldH + DpiScale.V(this, 18));

            AcceptButton = _okButton;
            CancelButton = cancelButton;
        }

        private static AntdUI.Label MakeFieldLabel(string text, int x, int y, int fieldH)
        {
            int offset = Math.Max(4, (fieldH - 17) / 2);
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + offset) };
        }

        private AntdUI.Input MakePasswordBox(int x, int y, int width, int fieldH)
        {
            return new AntdUI.Input {
                Location = new Point(x, y),
                Size = new Size(width, fieldH),
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
