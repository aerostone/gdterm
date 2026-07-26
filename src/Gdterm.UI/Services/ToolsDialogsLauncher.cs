using System;
using System.IO;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.KeePass;
using Gdterm.Security;
using Gdterm.UI.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 工具对话框启动器——KeePass / 密码健康 / AI / 生成器 / 危险命令 / 关于（finding-10）。
    /// </summary>
    public sealed class ToolsDialogsLauncher
    {
        private readonly IWin32Window _owner;
        private readonly ISecurityManager _securityManager;
        private readonly IKeePassService _keepassService;
        private readonly DangerousCommandDetector _dangerousCmdDetector;

        public ToolsDialogsLauncher(
            IWin32Window owner,
            ISecurityManager securityManager,
            IKeePassService keepassService,
            DangerousCommandDetector dangerousCmdDetector)
        {
            _owner = owner;
            _securityManager = securityManager;
            _keepassService = keepassService;
            _dangerousCmdDetector = dangerousCmdDetector;
        }

        public bool ReAuthenticate(string action)
        {
            return MasterPasswordPrompt.Confirm(_owner, _securityManager, action);
        }

        public void OpenKeePassManager()
        {
            if (!ReAuthenticate("访问密码库管理")) return;
            if (_keepassService == null || !_keepassService.IsUnlocked)
            {
                MessageBox.Show(_owner, "密码库未解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new KeePassManagerForm(_keepassService))
                form.ShowDialog(_owner);
        }

        public void OpenPasswordHealth()
        {
            if (!ReAuthenticate("查看密码健康报告")) return;
            if (_keepassService == null || !_keepassService.IsUnlocked)
            {
                MessageBox.Show(_owner, "密码库未解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new PasswordHealthForm(_keepassService))
                form.ShowDialog(_owner);
        }

        public void OpenAppearanceSettings()
        {
            var configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config");
            using (var form = new AppearanceSettingsForm(configDir))
            {
                if (form.ShowDialog(_owner) == DialogResult.OK && form.Result != null)
                {
                    Gdterm.UI.Program.GlobalAppearance = form.Result;
                    MessageBox.Show(
                        _owner,
                        "外观已保存。新开终端立即生效；DPI 感知需重启应用。",
                        "外观设置",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
        }

        public void OpenAiSettings()
        {
            var aiModelStore = new AiModelStore(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "ai-models.json"));
            // ApiKey 用主密码派生 AES（gdk2）；锁定时退回 gdk1
            aiModelStore.SetMasterPasswordProvider(() =>
                _securityManager != null && !_securityManager.IsLocked
                    ? _securityManager.GetMasterPassword()
                    : null);
            try { aiModelStore.UpgradeSecretsToMasterKey(); } catch { }
            using (var form = new AiSettingsForm(aiModelStore))
                form.ShowDialog(_owner);
        }

        public void OpenPasswordGenerator()
        {
            using (var form = new PasswordGeneratorForm())
                form.ShowDialog(_owner);
        }

        public void OpenDangerousCmdSettings()
        {
            using (var form = new DangerousCommandConfigForm(_dangerousCmdDetector))
                form.ShowDialog(_owner);
        }

        public void ShowHotkeysHelp()
        {
            MessageBox.Show(
                _owner,
                "快捷键：\n\n" +
                "Ctrl + `          呼出/隐藏窗口\n" +
                "Ctrl + L          切换连接面板\n" +
                "Ctrl + R          重连当前标签\n" +
                "Ctrl + W          关闭当前标签\n" +
                "Ctrl + F          终端查找\n" +
                "Ctrl + P          片段搜索\n" +
                "Esc / F11         退出专注模式\n" +
                "右上角按钮         退出专注（专注模式下可见）",
                "快捷键", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void ShowAbout()
        {
            MessageBox.Show(
                _owner,
                "gdterm - 绿色运维客户端\n版本 1.0.0\n\nSSH / RDP / SFTP / 串口 / 本地终端 / 运维工具箱",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
