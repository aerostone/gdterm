using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Core;
using Gdterm.Core.Security;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 主机密钥确认对话框（TOFU 首次信任 / 密钥变更警报）。
    ///
    /// 为什么要有它：SSH.NET 不注册 HostKeyReceived 就完全不校验服务端公钥，
    /// 中间人可以透明接管链路。校验必须配一个能看清指纹的出口，否则用户只会无脑点「是」。
    ///
    /// 设计取舍：
    ///  - 指纹用等宽字体、分两行给 SHA256 与 MD5（老设备控制台常只印 MD5），带复制按钮。
    ///  - 密钥**变更**时用危险色 + 默认焦点在「取消」，且「信任并保存」多一次显式确认——
    ///    最常见的原因是重装机器，但也是最需要人停下来看一眼的场景。
    ///  - 所有出口返回 HostKeyDecision，取消/Esc/关闭窗口 = Reject（不是 AcceptOnce）。
    /// </summary>
    public class HostKeyDialog : AntdUI.Window
    {
        private readonly HostKeyPromptInfo _info;

        /// <summary>用户的选择；默认拒绝。</summary>
        public HostKeyDecision Decision { get; private set; }

        public HostKeyDialog(HostKeyPromptInfo info)
        {
            if (info == null) throw new ArgumentNullException("info");
            _info = info;
            Decision = HostKeyDecision.Reject;
            InitializeComponent();
            UiA11y.AutoName(this);
        }

        private bool IsChanged { get { return _info.Situation == HostKeySituation.Changed; } }

        private void InitializeComponent()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false;
            AutoHandDpi = false;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = GdtermColorTable.Background;

            var accent = IsChanged ? GdtermColorTable.Danger : GdtermColorTable.Warning;
            Size = new Size(DpiScale.V(this, 600), DpiScale.V(this, IsChanged ? 500 : 440));
            Text = IsChanged ? L10n.T("主机密钥已变更") : L10n.T("首次连接 - 确认主机密钥");

            // —— 标题 ——
            var title = new AntdUI.Label
            {
                Text = IsChanged ? L10n.T("⚠ 主机密钥与记录不一致") : L10n.T("确认这台主机的身份"),
                Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold),
                ForeColor = accent,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(10)
            };

            // —— 目标 ——
            var target = new AntdUI.Label
            {
                Text = _info.Target + "  ·  " + _info.KeyBits + " " + L10n.T("位") + "  " + (_info.KeyType ?? ""),
                Font = new Font(Services.FormFontPolicy.MonoFont().FontFamily, Font.Size + 1f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Foreground,
                Dock = DockStyle.Top,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(4)
            };

            // —— 结论性说明 ——
            var message = new AntdUI.Label
            {
                Text = IsChanged
                    ? L10n.T("这台主机出示的公钥与 known_hosts 里的记录不同。\n可能是重装/更换了主机密钥，也可能是中间人攻击。\n确认之前请通过其他渠道（管理员、控制台）核对指纹。")
                    : L10n.T("这是第一次连接这台主机，还没有它的公钥记录。\n请核对下面的指纹与服务端实际指纹是否一致（sshd -T | grep hostkey，或管理员提供）。"),
                ForeColor = IsChanged ? GdtermColorTable.Danger : GdtermColorTable.Foreground,
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(DpiScale.V(this, 560), 0),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 8), DpiScale.V(this, 16), DpiScale.V(this, 8))
            };

            // —— 指纹块 ——
            var prints = new Panel
            {
                Dock = DockStyle.Top,
                Height = DpiScale.V(this, IsChanged ? 190 : 150),
                Padding = new Padding(DpiScale.V(this, 16), DpiScale.V(this, 4), DpiScale.V(this, 16), DpiScale.V(this, 4))
            };
            var printBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                Text = BuildPrintText(),
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true
            };
            var copy = new AntdUI.Button
            {
                Text = L10n.T("复制指纹"),
                Type = AntdUI.TTypeMini.Default,
                AutoSize = true,
                Dock = DockStyle.Bottom,
                Padding = new Padding(DpiScale.V(this, 10), DpiScale.V(this, 4), DpiScale.V(this, 10), DpiScale.V(this, 4))
            };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(_info.Sha256Fingerprint ?? ""); }
                catch (Exception ex) { Diagnostics.DiagLog.Swallowed("HostKeyDialog.Copy", ex); }
            };
            prints.Controls.Add(printBox);
            prints.Controls.Add(copy);

            // —— 按钮 ——
            var strip = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = DpiScale.V(this, 52),
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 10), DpiScale.V(this, 12), DpiScale.V(this, 10))
            };

            var reject = new AntdUI.Button
            {
                Text = L10n.T("取消并断开 (Esc)"),
                Type = AntdUI.TTypeMini.Default,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5))
            };
            reject.Click += (s, e) => Close(HostKeyDecision.Reject);

            var save = new AntdUI.Button
            {
                Text = L10n.T("信任并记住"),
                Type = IsChanged ? AntdUI.TTypeMini.Warn : AntdUI.TTypeMini.Primary,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5))
            };
            save.Click += (s, e) => OnAcceptAndSave();

            var once = new AntdUI.Button
            {
                Text = L10n.T("仅本次信任"),
                Type = AntdUI.TTypeMini.Default,
                AutoSize = true,
                Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 5), DpiScale.V(this, 12), DpiScale.V(this, 5))
            };
            once.Click += (s, e) => Close(HostKeyDecision.AcceptOnce);

            strip.Controls.Add(reject);
            strip.Controls.Add(save);
            strip.Controls.Add(once);

            // Dock=Top 的加入顺序：后加的在上面
            Controls.Add(prints);
            Controls.Add(message);
            Controls.Add(target);
            Controls.Add(title);
            Controls.Add(strip);

            CancelButton = reject;
            // 变更场景默认焦点给「仅本次信任」而不是「记住」：逼用户先做一次最小授权
            if (!IsChanged) AcceptButton = save; else AcceptButton = once;

            FormFontPolicy.Apply(this);
        }

        private string BuildPrintText()
        {
            var sb = new System.Text.StringBuilder();
            if (IsChanged)
            {
                sb.Append(L10n.T("已知记录（旧）")).AppendLine()
                  .AppendLine("  " + (_info.PreviousSha256Fingerprint ?? "(无法解析)"))
                  .AppendLine();
            }
            sb.Append(L10n.T("本次出示（新）")).AppendLine()
              .AppendLine("  " + (_info.Sha256Fingerprint ?? ""));
            sb.AppendLine();
            sb.AppendLine(L10n.T("MD5（老设备控制台）"));
            sb.AppendLine("  " + (_info.Md5Fingerprint ?? ""));
            sb.AppendLine();
            sb.AppendLine(L10n.T("算法"));
            sb.AppendLine("  " + (_info.KeyType ?? "?"));
            return sb.ToString();
        }

        private void OnAcceptAndSave()
        {
            if (IsChanged)
            {
                // 密钥变更 = 最需要人停下来看一眼的场景：再来一次显式确认
                var answer = MessageBox.Show(this,
                    L10n.T("确定要把新的主机密钥写入 known_hosts 吗？\n如果设备没有更换过密钥，这就是中间人攻击。"),
                    L10n.T("确认主机密钥变更"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return;
            }
            Close(HostKeyDecision.AcceptAndSave);
        }

        private void Close(HostKeyDecision decision)
        {
            Decision = decision;
            DialogResult = decision == HostKeyDecision.Reject ? DialogResult.Cancel : DialogResult.OK;
        }
    }
}
