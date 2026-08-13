using System.Collections.Generic;
using System.Windows.Forms;
using Gdterm.KeePass;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// KeePass 条目保存前的密码强度警告 helper。
    ///
    /// 设计原因：远端服务器实际可能用的是强度本身不足的旧密码，强制本地策略阻断
    /// 会让用户无法把真实凭据录入到本地密码库——本质是把"远端策略"误当成"本地策略"。
    /// 本工具将强度规则从硬阻断降级为"列出违反的规则 + 用户确认仍然保存"，
    /// 同步提示用户：弱密码本身不应被强制使用，建议尽快在远端修改。
    /// </summary>
    internal static class KeePassPasswordWarning
    {
        /// <summary>
        /// 检查密码强度，若违反规则则弹 YesNo 警告对话框让用户决定是否仍然保存。
        /// </summary>
        /// <param name="owner">父窗体（用于居中弹窗）</param>
        /// <param name="keepass">KeePass 服务（用于取 ValidatePasswordStrength）</param>
        /// <param name="password">待保存的密码（空跳过）</param>
        /// <returns>true 表示用户确认保存（或无违反）；false 表示用户取消保存。</returns>
        public static bool ConfirmSaveIfWeak(IWin32Window owner, IKeePassService keepass, string password)
        {
            if (keepass == null || string.IsNullOrEmpty(password)) return true;

            IList<string> violations;
            try { violations = keepass.ValidatePasswordStrength(password); }
            catch { return true; /* 验证异常不阻断保存（fail-open，避免锁死用户） */ }

            if (violations == null || violations.Count == 0) return true;

            var msg = "密码强度不足，违反 " + violations.Count + " 条规则：\r\n  - "
                      + string.Join("\r\n  - ", violations)
                      + "\r\n\r\n仍要保存？建议尽快在远端修改为强密码。";
            var dr = MessageBox.Show(owner, msg, "密码强度警告",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            return dr == DialogResult.Yes;
        }
    }
}
