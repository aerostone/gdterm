using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// SFTP 远端权限编辑（WinSCP 式 3×3 复选 + octal 双向联动；chmod 走 SFTP 协议，不走 shell）。
    /// </summary>
    public sealed class SftpPermissionForm : AntdUI.Window
    {
        private readonly AntdUI.Checkbox[,] _chk = new AntdUI.Checkbox[3, 3];
        private readonly AntdUI.InputNumber _octalBox;
        private bool _syncing;
        private bool _warnedIllegalOctal;

        public int OctalMode { get; private set; }

        public SftpPermissionForm(string fileName, string rwx, int initialOctal)
        {
            Font = FormFontPolicy.UiFont();
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Text = L10n.T("权限：") + (fileName ?? "");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            // AntdUI.Window 自绘边框忽略 FixedDialog 语义，显式禁缩放
            Resizable = false;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int pad = DpiScale.V(this, 16);
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = fieldH + DpiScale.V(this, 8);
            // 标签列/勾选框宽按实测文字推：14pt 下「八进制」69px、「写入(w)」77px，
            // 写死 64/60 会当场把字裁掉（矩阵门 10 处文字溢出）
            int labelW = Math.Max(DpiScale.V(this, 64),
                FormFontPolicy.MeasureTextWidth(L10n.T("八进制"), FormFontPolicy.UiFont(), this) + DpiScale.V(this, 4));
            int chkW = DpiScale.V(this, 60);
            int y = pad;

            string[] rows = { L10n.T("属主"), L10n.T("属组"), L10n.T("其他") };
            string[] cols = { L10n.T("读取(r)"), L10n.T("写入(w)"), L10n.T("执行(x)") };
            int chkH = Math.Max(DpiScale.V(this, 24), FormFontPolicy.LineBox(this));
            foreach (var c0 in cols)
            {
                // 勾选框 = 方形勾选块 + 文字；文字实测宽 + 30px 勾选块与间距（14pt 下实测 30px）
                chkW = Math.Max(chkW,
                    FormFontPolicy.MeasureTextWidth(c0, FormFontPolicy.UiFont(), this) + DpiScale.V(this, 30));
            }
            for (int r = 0; r < 3; r++)
            {
                var lbl = new AntdUI.Label
                {
                    Text = rows[r],
                    Location = new Point(pad, y + (fieldH - FontHeight) / 2),
                    Size = new Size(labelW, fieldH),
                    ForeColor = GdtermColorTable.Muted,
                    TextAlign = ContentAlignment.MiddleRight
                };
                Controls.Add(lbl);
                for (int c = 0; c < 3; c++)
                {
                    var chk = new AntdUI.Checkbox
                    {
                        Text = cols[c],
                        Location = new Point(pad + labelW + DpiScale.V(this, 8) + c * (chkW + DpiScale.V(this, 8)), y + (fieldH - chkH) / 2),
                        Size = new Size(chkW, chkH),
                        Font = FormFontPolicy.UiFont(),
                        ForeColor = GdtermColorTable.Foreground
                    };
                    chk.CheckedChanged += (s, e) => SyncFromChecks();
                    _chk[r, c] = chk;
                    Controls.Add(chk);
                }
                y += rowH;
            }

            // octal 行
            var octLbl = new AntdUI.Label
            {
                Text = L10n.T("八进制"),
                Location = new Point(pad, y + (fieldH - FontHeight) / 2),
                Size = new Size(labelW, fieldH),
                ForeColor = GdtermColorTable.Muted,
                TextAlign = ContentAlignment.MiddleRight
            };
            Controls.Add(octLbl);
            _octalBox = new AntdUI.InputNumber
            {
                Location = new Point(pad + labelW + DpiScale.V(this, 8), y),
                Size = new Size(DpiScale.V(this, 140), fieldH),
                MinimumSize = new Size(0, fieldH),
                Minimum = 0,
                Maximum = 777,
                Font = FormFontPolicy.UiFont(),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            _octalBox.ValueChanged += (s, e) => SyncFromOctal();
            Controls.Add(_octalBox);
            y += rowH + DpiScale.V(this, 8);

            // 按钮：取消左、确定右（Windows 惯例 primary 最右）
            var btnW = DpiScale.V(this, 90);
            var btnH = Math.Max(DpiScale.V(this, 32), FormFontPolicy.RowStep(this));
            var btnCancel = new AntdUI.Button
            {
                Text = L10n.T("取消"),
                DialogResult = DialogResult.Cancel,
                Size = new Size(btnW, btnH),
                Location = new Point(pad + labelW + DpiScale.V(this, 8) + 3 * (chkW + DpiScale.V(this, 8)) - 2 * btnW - DpiScale.V(this, 8), y),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            var btnOk = new AntdUI.Button
            {
                Text = L10n.T("应用"),
                DialogResult = DialogResult.None,
                Size = new Size(btnW, btnH),
                Location = new Point(btnCancel.Right + DpiScale.V(this, 8), y),
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            btnOk.Click += (s, e) =>
            {
                // 危险操作确认（F3）：「其他」带写位 = 任何本机用户可写，真实安全风险，先警告
                if (_chk[2, 1].Checked)
                {
                    var dr = AntdUI.Modal.open(this, L10n.T("开放其他用户写权限"),
                        L10n.T("该权限允许机器上的其他用户修改此文件。\n确定应用？"),
                        AntdUI.TType.Warn);
                    if (dr != DialogResult.Yes && dr != DialogResult.OK)
                        return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(btnCancel);
            Controls.Add(btnOk);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            int clientW = pad + labelW + DpiScale.V(this, 8) + 3 * (chkW + DpiScale.V(this, 8)) + pad;
            ClientSize = new Size(clientW, y + btnH + pad);

            // 初值：rwx → 勾选 → octal 联动（initialOctal 是位值如 493，转十进制位 755 再存 OctalMode）
            FormFontPolicy.Apply(this);
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private void ApplyRwx(string rwx, int octalBits)
        {
            _syncing = true;
            try
            {
                if (!string.IsNullOrEmpty(rwx) && rwx.Length >= 9)
                {
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 3; c++)
                            _chk[r, c].Checked = rwx[r * 3 + c] != '-';
                }
                else
                {
                    // rwx 缺失时按位值反推（位运算，非十进制位）
                    int[] ds = { (octalBits >> 6) & 7, (octalBits >> 3) & 7, octalBits & 7 };
                    for (int r = 0; r < 3; r++)
                    {
                        _chk[r, 0].Checked = (ds[r] & 4) != 0;
                        _chk[r, 1].Checked = (ds[r] & 2) != 0;
                        _chk[r, 2].Checked = (ds[r] & 1) != 0;
                    }
                }
                SyncFromChecks(); // 复选→OctalMode（十进制位）+ octalBox 联动，单点换算
            }
            finally { _syncing = false; }
        }

        private void SyncFromChecks()
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                int d0 = ((_chk[0, 0].Checked ? 4 : 0) | (_chk[0, 1].Checked ? 2 : 0) | (_chk[0, 2].Checked ? 1 : 0));
                int d1 = ((_chk[1, 0].Checked ? 4 : 0) | (_chk[1, 1].Checked ? 2 : 0) | (_chk[1, 2].Checked ? 1 : 0));
                int d2 = ((_chk[2, 0].Checked ? 4 : 0) | (_chk[2, 1].Checked ? 2 : 0) | (_chk[2, 2].Checked ? 1 : 0));
                OctalMode = d0 * 100 + d1 * 10 + d2;
                _octalBox.Value = OctalMode;
            }
            finally { _syncing = false; }
        }

        private void SyncFromOctal()
        {
            if (_syncing) return;
            _syncing = true;
            try
            {
                int v = (int)_octalBox.Value;
                // InputNumber 允许 778/789 之类非法位→钳回合法
                int d0 = (v / 100) % 10, d1 = (v / 10) % 10, d2 = v % 10;
                if (d0 > 7 || d1 > 7 || d2 > 7)
                {
                    // 原来静默回退：用户以为输入生效了，实际没变。这里说清规则再回退。
                    _octalBox.Value = OctalMode;
                    if (!_warnedIllegalOctal)
                    {
                        _warnedIllegalOctal = true;
                        AntdUI.Message.warn(this, L10n.T("八进制每位只能是 0-7，已回退到 ") + OctalMode.ToString("D3"));
                    }
                    return;
                }
                _warnedIllegalOctal = false;
                OctalMode = v;
                int[] ds = { d0, d1, d2 };
                for (int r = 0; r < 3; r++)
                {
                    _chk[r, 0].Checked = (ds[r] & 4) != 0;
                    _chk[r, 1].Checked = (ds[r] & 2) != 0;
                    _chk[r, 2].Checked = (ds[r] & 1) != 0;
                }
            }
            finally { _syncing = false; }
        }
    }
}
