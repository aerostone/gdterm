using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Security.Otp;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

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
            Text = L10n.T("添加 OTP 条目");
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Resizable = false;
            AutoHandDpi = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            // Font 先于 ClientSize：Font 变更会触发 AutoScaleMode=Font 整体缩放 ClientSize。
            // 若先设 ClientSize 再换字体，构造期中部的绝对坐标计算会拿到缩放后的宽
            // （CI 0.1.359 实测 480→760），Show 后回弹到设计值，页脚按钮飞出面板。
            Font = FormFontPolicy.UiFont();
            ClientSize = DpiScale.S(this, 480, 400);
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            var pad = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 10), DpiScale.V(this, 12), DpiScale.V(this, 6));

            var lblUri = MakeLabel(L10n.T("otpauth:// URI 或 Base32 密钥"), pad);
            _uri = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                MinimumSize = new System.Drawing.Size(0, FormFontPolicy.FieldHeight(this)),
                PlaceholderText = L10n.T("otpauth://totp/Issuer:acct?secret=ABC…  或  直接粘贴 Base32 密钥"),
                Font = FormFontPolicy.UiFont()
            };
            _uri.TextChanged += (s, e) => TryPrefillFromUri();

            int fh = FormFontPolicy.FieldHeight(this);
            int gap = DpiScale.V(this, 8);
            var row1 = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 4,
                // 禁用 AutoSize：Dock=Top + AutoSize=true 时表宽按“首选宽”取，Percent 列对
                // 首选宽贡献为 0 → 输入单元格塌成零尺寸（CI 0.1.359 门禁实测 4 个 Input 零宽）。
                // 固定高 + Dock=Top 取满宽，列宽显式分配，任何字号/DPI 下确定。
                Height = 4 * fh + 3 * gap + DpiScale.V(this, 8),
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 8), DpiScale.V(this, 12), 0)
            };
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DpiScale.V(this, 96)));
            row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int r = 0; r < 4; r++) row1.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

            _issuer = new AntdUI.Input { MinimumSize = new System.Drawing.Size(120, fh), Font = FormFontPolicy.UiFont(), PlaceholderText = L10n.T("如：GitHub") };
            _account = new AntdUI.Input { MinimumSize = new System.Drawing.Size(120, fh), Font = FormFontPolicy.UiFont(), PlaceholderText = L10n.T("如：user@example.com") };
            _digits = new AntdUI.Input { MinimumSize = new System.Drawing.Size(120, fh), Font = FormFontPolicy.UiFont(), Text = "6" };
            _period = new AntdUI.Input { MinimumSize = new System.Drawing.Size(120, fh), Font = FormFontPolicy.UiFont(), Text = "30" };

            row1.Controls.Add(TableLabel(L10n.T("发行方")), 0, 0);
            row1.Controls.Add(_issuer, 1, 0);
            row1.Controls.Add(TableLabel(L10n.T("账户")), 0, 1);
            row1.Controls.Add(_account, 1, 1);
            row1.Controls.Add(TableLabel(L10n.T("位数(6-8)")), 0, 2);
            row1.Controls.Add(_digits, 1, 2);
            row1.Controls.Add(TableLabel(L10n.T("周期(秒)")), 0, 3);
            row1.Controls.Add(_period, 1, 3);

            // 底部按钮栏——FlowLayoutPanel 右向流（SetupWizardForm/PasswordHealthForm 同款惯例）。
            // 流式布局每次 resize 自动重排，不依赖构造期 ClientSize，永不越界。
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 12), DpiScale.V(this, 16), DpiScale.V(this, 12))
            };
            var btnOk = new AntdUI.Button
            {
                Text = L10n.T("保存"),
                AutoSize = true,
                Type = AntdUI.TTypeMini.Primary,
                Cursor = Cursors.Hand,
                Name = "btnSave",
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 5), DpiScale.V(this, 16), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 8))
            };
            btnOk.Click += (s, e) => OnOk();
            var btnCancel = new AntdUI.Button
            {
                Text = L10n.T("取消"),
                AutoSize = true,
                Cursor = Cursors.Hand,
                Name = "btnCancel",
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 5), DpiScale.V(this, 16), DpiScale.V(this, 5)),
                Margin = new Padding(DpiScale.V(this, 8))
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            // RightToLeft 流：先加者在右，主操作（保存）居右，与 SetupWizard 一致
            bottom.Controls.Add(btnOk);
            bottom.Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            // Fill 先加、边缘 Dock 后加
            var hint = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = GdtermColorTable.Foreground,
                BackColor = GdtermColorTable.Background,
                Font = FormFontPolicy.UiFont(),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(DpiScale.V(this, 14), DpiScale.V(this, 4), DpiScale.V(this, 14), 0),
                Text = L10n.T("提示：多数门户在「设置→两步验证」处提供 otpauth:// 链接（手机扫码界面长按二维码可复制链接）。\nURI 解析成功后会自动预填下方字段，可再修改。")
            };
            Controls.Add(hint);
            Controls.Add(row1);
            Controls.Add(_uri);
            Controls.Add(lblUri);
            Controls.Add(bottom);
            FormFontPolicy.Apply(this);
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        /// <summary>表内单元格标签：Fill + 垂直居中（固定盒高下 Dock=Top 会贴顶）。</summary>
        private Label TableLabel(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = text,
                ForeColor = GdtermColorTable.Foreground,
                BackColor = GdtermColorTable.Background,
                Font = FormFontPolicy.UiFont()
            };
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
                    MessageBox.Show(this, L10n.T("OTP URI 解析失败：\n") + ex.Message, L10n.T("添加 OTP"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var secret = parsed != null ? parsed.Secret : text;
            if (string.IsNullOrWhiteSpace(secret))
            {
                MessageBox.Show(this, L10n.T("请粘贴 otpauth:// URI 或 Base32 密钥。"), L10n.T("添加 OTP"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var key = Base32.Decode(secret);
                if (key.Length < 10)
                    throw new FormatException(L10n.T("密钥太短（Base32 解码后需 ≥10 字节，常见为 20）"));
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, L10n.T("密钥不是合法的 Base32：\n") + ex.Message, L10n.T("添加 OTP"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int digits, period;
            if (!int.TryParse((_digits.Text ?? "").Trim(), out digits) || digits < 6 || digits > 8)
            {
                MessageBox.Show(this, L10n.T("位数需为 6-8。"), L10n.T("添加 OTP"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!int.TryParse((_period.Text ?? "").Trim(), out period) || period <= 0 || period > 300)
            {
                MessageBox.Show(this, L10n.T("周期需为 1-300 秒。"), L10n.T("添加 OTP"),
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
                MessageBox.Show(this, L10n.T("请填写发行方或账户（至少一项），便于日后识别。"), L10n.T("添加 OTP"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
