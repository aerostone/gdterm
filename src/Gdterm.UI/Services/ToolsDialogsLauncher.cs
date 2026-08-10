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
        private readonly Action _applyAppearanceToTerminals;

        public ToolsDialogsLauncher(
            IWin32Window owner,
            ISecurityManager securityManager,
            IKeePassService keepassService,
            DangerousCommandDetector dangerousCmdDetector,
            Action applyAppearanceToTerminals = null)
        {
            _owner = owner;
            _securityManager = securityManager;
            _keepassService = keepassService;
            _dangerousCmdDetector = dangerousCmdDetector;
            _applyAppearanceToTerminals = applyAppearanceToTerminals;
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

        public void OpenSshKeyManager()
        {
            if (!ReAuthenticate("管理 SSH 密钥")) return;
            if (_keepassService == null || !_keepassService.IsUnlocked)
            {
                MessageBox.Show(_owner, "密码库未解锁", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new SshKeyManagerForm(_keepassService))
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
                    // 即时应用终端字体到所有已开标签页（含 split-pane 子面板）——以前只刷 UI 不刷终端，用户改字号看不出变化。
                    try { _applyAppearanceToTerminals?.Invoke(); }
                    catch (Exception ex) { Gdterm.UI.Diagnostics.DiagLog.Swallowed("Appearance.ApplyTerminals", ex); }
                    // 即时应用界面主题（若改了）——ToolStrip 不会自感静态颜色变化，所以调用 ApplyTheme 后手动 Invalidate 所有工具条
                    if (!string.IsNullOrEmpty(form.Result.UiTheme))
                    {
                        try
                        {
                            Gdterm.UI.Diagnostics.GdtermColorTable.ApplyTheme(form.Result.UiTheme);
                            // ToolStripManager.Renderer 是静态的，设成新实例会触发 Toolstrip 重画；但我们的自定义 GdtermToolStripRenderer 事件是读静态颜色属性，所以只要主动 Refresh 一下可见的 UI 即可
                            if (_owner is Gdterm.UI.Forms.MainForm mf2)
                            {
                                mf2.Refresh();
                                mf2.Invalidate(true);
                            }
                        }
                        catch { }
                    }
                    // UI 字体即时生效；DPI 需重启
                    try
                    {
                        if (_owner is Gdterm.UI.Forms.MainForm mf)
                            mf.ApplyGlobalUIFont();
                    }
                    catch { }
                    try { Gdterm.UI.Diagnostics.ToastNotifier.Success("外观已保存（DPI 需重启）"); }
                    catch
                    {
                        MessageBox.Show(
                            _owner,
                            "外观已保存。终端与界面字体已即时生效；DPI 感知需重启应用。",
                            "外观设置",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
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

        public void OpenChangeMasterPassword()
        {
            if (_securityManager == null)
            {
                MessageBox.Show(_owner, "安全模块未初始化", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 必须先解锁主会话，否则无法拿到旧主密码 / 无法重加密 kdbx
            if (_securityManager.IsLocked)
            {
                if (!MasterPasswordPrompt.Confirm(_owner, _securityManager, "修改主密码"))
                    return;
            }

            using (var form = new ChangeMasterPasswordForm(_securityManager))
            {
                form.ChangeRequested += (s, args) =>
                {
                    // 1) 重加密 kdbx：用旧主密码解锁已存在库，再用新主密码重新加密保存
                    if (_keepassService != null)
                    {
                        var ok = _keepassService.ChangeMasterPasswordAsync(args.OldPassword, args.NewPassword)
                            .GetAwaiter().GetResult();
                        if (!ok)
                            throw new InvalidOperationException("密码库重加密失败：旧密码不正确或文件损坏");
                    }

                    // 2) 更新 SecurityManager 哈希（同时会重新强度校验）
                    _securityManager.SetMasterPassword(args.OldPassword, args.NewPassword);

                    // 3) 持久化 master-password.ini
                    Program.PersistMasterPasswordConfig(_securityManager);
                };
                form.ShowDialog(_owner);
            }
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
