using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security.Otp;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 添加 OTP 条目：粘贴 otpauth:// URI（自动预填）或手动填 Base32 密钥。
    /// </summary>
    public sealed class AddOtpForm : AntdUI.Window
    {
        private readonly AntdUI.Input _uri;
        private readonly AntdUI.Input _issuer;
        private readonly AntdUI.Input _account;
        private readonly AntdUI.Input _digits;
        private readonly AntdUI.Input _period;

        /// <summary>OK 后的新条目（Id 为空，由 store 分配）。</summary>
        public OtpEntry Entry { get; private set; }

        public AddOtpForm()
        {
            Text = "添加 OTP 条目";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Resizable = false;
            AutoHandDpi = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = DpiScale.S(this, 480, 360);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont();
            ShowInTaskbar = false;

            var pad = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 10), DpiScale.V(this, 12), DpiScale.V(this, 6));

            var lblUri = MakeLabel("otpauth:// URI 或 Base32 密钥", pad);
            _uri = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                MinimumSize = new System.Drawing.Size(0, FormFontPolicy.FieldHeight(this)),
                PlaceholderText = "otpauth://totp/Issuer:acct?secret=ABC…  或  直接粘贴 Base32 密钥",
                Font = FormFontPolicy.UiFont()
            };
            _uri.TextChanged += (s, e) => TryPrefillFromUri();

            var row1 = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 8), DpiScale.V(this, 12), 0)
            };
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));

            _issuer = new AntdUI.Input { MinimumSize = new System.Drawing.Size(0, FormFontPolicy.FieldHeight(this)), Font = FormFontPolicy.UiFont(), PlaceholderText = "如：GitHub" };
            _account = new AntdUI.Input { MinimumSize = new System.Drawing.Size(0, FormFontPolicy.FieldHeight(this)), Font = FormFontPolicy.UiFont(), PlaceholderText = "如：user@example.com" };
            _digits = new AntdUI.Input { MinimumSize = new System.Drawing.Size(0, FormFontPolicy.FieldHeight(this)), Font = FormFontPolicy.UiFont(), Text = "6" };
            _period = new AntdUI.Input { MinimumSize = new System.Drawing.Size(0, FormFontPolicy.FieldHeight(this)), Font = FormFontPolicy.UiFont(), Text = "30" };

            row1.Controls.Add(MakeLabel("发行方", pad), 0, 0);
            row1.Controls.Add(_issuer, 1, 0);
            row1.Controls.Add(MakeLabel("账户", pad), 0, 1);
            row1.Controls.Add(_account, 1, 1);
            row1.Controls.Add(MakeLabel("位数(6-8)", pad), 0, 2);
            row1.Controls.Add(_digits, 1, 2);
            row1.Controls.Add(MakeLabel("周期(秒)", pad), 0, 3);
            row1.Controls.Add(_period, 1, 3);

            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FormFontPolicy.FieldHeight(this) + 24
            };
            var btnCancel = new AntdUI.Button
            {
                Text = "取消",
                Location = DpiScale.P(this, ClientSize.Width - 96, 8),
                Size = DpiScale.S(this, 88, FormFontPolicy.FieldHeight(this)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            var btnOk = new AntdUI.Button
            {
                Text = "保存",
                Type = AntdUI.TTypeMini.Primary,
                Location = DpiScale.P(this, ClientSize.Width - 192, 8),
                Size = DpiScale.S(this, 88, FormFontPolicy.FieldHeight(this)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnOk.Click += (s, e) => OnOk();
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            bottom.Controls.Add(btnOk);
            bottom.Controls.Add(btnCancel);

            // Fill 先加、边缘 Dock 后加
            var hint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = GdtermColorTable.Foreground,
                BackColor = GdtermColorTable.Background,
                Font = FormFontPolicy.UiFont(),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 14), DpiScale.V(this, 4), DpiScale.V(this, 14), 0),
                Text = "提示：多数门户在「设置→两步验证」处提供 otpauth:// 链接（手机扫码界面长按二维码可复制链接）。\nURI 解析成功后会自动预填下方字段，可再修改。"
            };
            Controls.Add(hint);
            Controls.Add(row1);
            Controls.Add(_uri);
            Controls.Add(lblUri);
            Controls.Add(bottom);
            FormFontPolicy.Apply(this);
        }

        private Label MakeLabel(string text, Padding pad)
        {
            return new Label
            {
                Dock = DockStyle.Top,
                Text = text,
                ForeColor = GdtermColorTable.Foreground,
                BackColor = GdtermColorTable.Background,
                Font = FormFontPolicy.UiFont(),
                Padding = pad,
                AutoSize = false,
                Height = FormFontPolicy.FieldHeight(this) - 4
            };
        }

        private void TryPrefillFromUri()
        {
            var text = _uri.Text ?? "";
            if (!text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var u = OtpAuthUri.Parse(text);
                if (!string.IsNullOrEmpty(u.Issuer)) _issuer.Text = u.Issuer;
                if (!string.IsNullOrEmpty(u.Account)) _account.Text = u.Account;
                if (u.Digits != 6) _digits.Text = u.Digits.ToString();
                if (u.Period != 30) _period.Text = u.Period.ToString();
            }
            catch
            {
                // 输入过程中的半截 URI，忽略
            }
        }

        private void OnOk()
        {
            var text = (_uri.Text ?? "").Trim();
            OtpAuthUri parsed = null;
            if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                try { parsed = OtpAuthUri.Parse(text); }
                catch (FormatException ex)
                {
                    MessageBox.Show(this, "OTP URI 解析失败：\n" + ex.Message, "添加 OTP",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var secret = parsed != null ? parsed.Secret : text;
            if (string.IsNullOrWhiteSpace(secret))
            {
                MessageBox.Show(this, "请粘贴 otpauth:// URI 或 Base32 密钥。", "添加 OTP",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var key = Base32.Decode(secret);
                if (key.Length < 10)
                    throw new FormatException("密钥太短（Base32 解码后需 ≥10 字节，常见为 20）");
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, "密钥不是合法的 Base32：\n" + ex.Message, "添加 OTP",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int digits, period;
            if (!int.TryParse((_digits.Text ?? "").Trim(), out digits) || digits < 6 || digits > 8)
            {
                MessageBox.Show(this, "位数需为 6-8。", "添加 OTP",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!int.TryParse((_period.Text ?? "").Trim(), out period) || period <= 0 || period > 300)
            {
                MessageBox.Show(this, "周期需为 1-300 秒。", "添加 OTP",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (parsed != null)
            {
                Entry = OtpEntry.FromUri(parsed);
                Entry.Digits = digits;
                Entry.Period = period;
            }
            else
            {
                Entry = new OtpEntry
                {
                    Issuer = (_issuer.Text ?? "").Trim(),
                    Account = (_account.Text ?? "").Trim(),
                    Secret = secret.ToUpperInvariant(),
                    Digits = digits,
                    Period = period,
                    Algorithm = OtpAlgorithm.Sha1,
                    IsTotp = true
                };
            }

            if (string.IsNullOrEmpty(Entry.Title))
            {
                MessageBox.Show(this, "请填写发行方或账户（至少一项），便于日后识别。", "添加 OTP",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
