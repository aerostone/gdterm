using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.Connections;
using Gdterm.KeePass;
using Gdterm.Security;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;
using Gdterm.Core;

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
        private readonly Func<Gdterm.Tools.ISshRemoteSession> _remoteSessionFactory;
        private readonly Gdterm.Tools.Scanning.ScanPluginStore _scanPluginStore;
        // 审计 F7 SSOT：MainForm 构造 router 后注入其表渲染结果,帮助文本不再手抄键位
        private Func<string> _hotkeyTableLines;
        // 一体化 change 2026-09-26：密码库管理器注入连接库，启用「关联连接」列与「从凭据新建连接」
        private IConnectionStore _connectionStore;
        private Action _connectionsChanged;
        // nyaterm 对比 B1：OTP 管理器注入条目存储
        private IOtpStore _otpStore;

        public ToolsDialogsLauncher(
            IWin32Window owner,
            ISecurityManager securityManager,
            IKeePassService keepassService,
            DangerousCommandDetector dangerousCmdDetector,
            Action applyAppearanceToTerminals = null,
            Func<Gdterm.Tools.ISshRemoteSession> remoteSessionFactory = null,
            Gdterm.Tools.Scanning.ScanPluginStore scanPluginStore = null)
        {
            _owner = owner;
            _securityManager = securityManager;
            _keepassService = keepassService;
            _dangerousCmdDetector = dangerousCmdDetector;
            _applyAppearanceToTerminals = applyAppearanceToTerminals;
            _remoteSessionFactory = remoteSessionFactory;
            // 组合根注入（finding-09）：不再用静态单例，生命周期由 MainForm/AppShutdownCoordinator 管理；
            // 未注入时退回实例级 store（仅影响旧调用方，当前组合根总是传入）。
            _scanPluginStore = scanPluginStore ?? new Gdterm.Tools.Scanning.ScanPluginStore();
        }

        public bool ReAuthenticate(string action)
        {
            return MasterPasswordPrompt.Confirm(_owner, _securityManager, action);
        }


        /// <summary>MainForm 在创建 MainFormCommandRouter 后调用,把路由表投影喂给帮助窗。</summary>
        public void SetHotkeyTableSource(Func<string> tableLines)
        {
            _hotkeyTableLines = tableLines;
        }

        /// <summary>注入连接库与变更回调（一体化 change）：密码库管理器据此显示「关联连接」并支持从凭据建连接。</summary>
        public void SetConnectionIntegration(IConnectionStore connectionStore, Action connectionsChanged)
        {
            _connectionStore = connectionStore;
            _connectionsChanged = connectionsChanged;
        }

        /// <summary>注入 OTP 条目存储（nyaterm 对比 B1）。</summary>
        public void SetOtpStore(IOtpStore otpStore)
        {
            _otpStore = otpStore;
        }

        /// <summary>打开 OTP 动态码管理器（nyaterm 对比 B1）。</summary>
        public void OpenOtpManager()
        {
            if (_otpStore == null)
            {
                MessageBox.Show(_owner, L10n.T("OTP 存储未初始化。"), "OTP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new OtpManagerForm(_otpStore))
                form.ShowDialog(_owner);
        }

        /// <summary>帮助文本=路由表投影(平表部分)+特例键(不入平表的参数化/全局热键),杜绝两处手抄漂移。</summary>
        private string BuildHotkeyHelpText()
        {
            string tableLines = null;
            try { tableLines = _hotkeyTableLines != null ? _hotkeyTableLines() : null; } catch { }
            return L10n.T("快捷键（UI 动作均为 Ctrl+Shift 组合，普通 Ctrl 留给终端）：\n\n") +
                   L10n.T("Ctrl + `            呼出/隐藏窗口\n") +
                   (tableLines ?? "") + "\n" +
                   L10n.T("Ctrl + Shift + L    切换连接面板\n") +
                   L10n.T("Ctrl + Shift + M    tmux 快捷面板（底栏 tmux 键组切换）\n") +
                   L10n.T("Alt + 8             tmux 键组与全部之间快速切换\n") +
                   L10n.T("Ctrl + Tab          下一个标签（Ctrl+Shift+Tab 上一个）\n") +
                   L10n.T("Ctrl + Alt + 1..9   直达第 N 个标签\n") +
                   L10n.T("中键点标签头        关闭该标签\n") +
                   L10n.T("Esc / F11           退出专注模式\n") +
                   L10n.T("右上角按钮          退出专注（专注模式下可见）\n\n") +
                   L10n.T("提示：在终端里，普通 Ctrl 组合直接发给 shell，\n") +
                   L10n.T("例如 Ctrl+R 反向搜索历史、Ctrl+W 删词、Ctrl+L 清屏。");
        }

        /// <summary>
        /// 审计 F8：未解锁时不再死端弹窗，而是弹既有 KeePassUnlockForm 引导解锁（仿 ProtocolTabOpener 先例），
        /// 成功后放行原目标窗体。返回 false 表示用户放弃或服务缺失。
        /// </summary>
        private bool EnsureKeePassUnlocked(string action)
        {
            if (_keepassService == null)
            {
                MessageBox.Show(_owner, L10n.T("密码服务不可用"), action, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_keepassService.IsUnlocked) return true;
            try
            {
                using (var unlock = new KeePassUnlockForm(_keepassService))
                {
                    if (unlock.ShowDialog(_owner) != DialogResult.OK || !_keepassService.IsUnlocked)
                        return false; // 用户取消或解锁失败：静默放弃，不重复打扰
                }
                return true;
            }
            catch (Exception ex)
            {
                Gdterm.UI.Diagnostics.DiagLog.Swallowed("ToolsDialogs.KeePassUnlock", ex);
                MessageBox.Show(_owner, L10n.T("解锁失败: ") + ex.Message, action, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public void OpenKeePassManager()
        {
            if (!ReAuthenticate(L10n.T("访问密码库管理"))) return;
            if (!EnsureKeePassUnlocked(L10n.T("密码库管理"))) return;
            using (var form = new KeePassManagerForm(_keepassService, _connectionStore, _connectionsChanged))
                form.ShowDialog(_owner);
        }

        public void OpenPasswordHealth()
        {
            if (!ReAuthenticate(L10n.T("查看密码健康报告"))) return;
            if (!EnsureKeePassUnlocked(L10n.T("密码健康报告"))) return;
            using (var form = new PasswordHealthForm(_keepassService))
                form.ShowDialog(_owner);
        }

        /// <summary>回收站（F16 加分项「撤销/回收站」）：会话级，重启后清空。</summary>
        public void OpenTrash()
        {
            using (var form = new TrashForm())
                form.ShowDialog(_owner);
        }

        public void OpenSshKeyManager()
        {
            if (!ReAuthenticate(L10n.T("管理 SSH 密钥"))) return;
            if (!EnsureKeePassUnlocked(L10n.T("SSH 密钥管理"))) return;
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
                            // 审计 C6：ApplyTheme 内部会 raise ThemeChanged(old→new 映射)，
                            // MainForm 订阅后重贴整棵控件树；这里只兜底“还没订阅”的宿主。
                            Gdterm.UI.Diagnostics.GdtermColorTable.ApplyTheme(form.Result.UiTheme);
                            AntdUI.Style.SetPrimary(Gdterm.UI.Diagnostics.GdtermColorTable.Accent);
                        }
                        catch (Exception ex) { Gdterm.UI.Diagnostics.DiagLog.Swallowed("ToolsDialogs.ApplyTheme", ex); }
                    }
                    // UI 字体即时生效；DPI 需重启
                    try
                    {
                        if (_owner is Gdterm.UI.Forms.MainForm mf)
                        {
                            mf.ApplyGlobalUIFont();
                            mf.ApplyWindowOpacity();
                        }
                    }
                    catch (Exception ex) { Gdterm.UI.Diagnostics.DiagLog.Swallowed("ToolsDialogs.ApplyUIFont", ex); }
                    try { Gdterm.UI.Diagnostics.ToastNotifier.Success(L10n.T("外观已保存（DPI 需重启）")); }
                    catch
                    {
                        MessageBox.Show(
                            _owner,
                            L10n.T("外观已保存。终端与界面字体已即时生效；DPI 感知需重启应用。"),
                            L10n.T("外观设置"),
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
            try { aiModelStore.UpgradeSecretsToMasterKey(); }
            catch (Exception ex)
            {
                // 密钥升级失败必须留痕（finding-12）：静默失败会让后续排障无从下手
                Gdterm.UI.Diagnostics.DiagLog.Swallowed("ToolsDialogs.UpgradeSecretsToMasterKey", ex);
            }
            using (var form = new AiSettingsForm(aiModelStore))
                form.ShowDialog(_owner);
        }

        /// <summary>
        /// 检查更新（P2-9）：查 GitHub releases/latest。异步 + 静默失败——断网/限流都只提示一句，
        /// 绝不阻塞 UI 线程（HttpClient 同步等待在 Win7 上会假死）。
        /// </summary>
        public async void OpenUpdateCheck()
        {
            UpdateInfo info;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                info = await UpdateChecker.CheckAsync(UpdateChecker.DefaultRepo, 10).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                info = new UpdateInfo { Error = ex.Message };
            }
            finally
            {
                try { Cursor.Current = Cursors.Default; } catch (Exception) { }
            }
            ShowUpdateResult(info);
        }

        private void ShowUpdateResult(UpdateInfo info)
        {
            bool failed = info == null || !string.IsNullOrEmpty(info.Error);
            bool newer = !failed && info.IsNewer;

            using (var form = new Form
            {
                Text = L10n.T("检查更新"),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground
            })
            {
                int pad = DpiScale.V(form, 14);
                int fieldH = FormFontPolicy.FieldHeight(form);
                var head = new AntdUI.Label
                {
                    Text = failed ? L10n.T("检查失败：") + (info != null ? info.Error : L10n.T("未知错误"))
                         : newer ? L10n.T("发现新版本 ") + info.Version
                         : L10n.T("已是最新版本（") + UpdateChecker.CurrentVersion + "）",
                    AutoSize = true,
                    Location = new Point(pad, pad),
                    ForeColor = failed ? GdtermColorTable.Danger
                             : newer ? GdtermColorTable.Accent : GdtermColorTable.Foreground,
                    Font = FormFontPolicy.UiFont(+1f)
                };
                form.Controls.Add(head);

                var body = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    WordWrap = true,
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = GdtermColorTable.Surface,
                    ForeColor = GdtermColorTable.Foreground,
                    Font = FormFontPolicy.MonoFont(),
                    Location = new Point(pad, pad + DpiScale.V(form, 34)),
                    Size = new Size(DpiScale.V(form, 520), DpiScale.V(form, 220)),
                    Text = failed ? L10n.T("无法获取 release 信息，请检查网络后重试；也可直接打开项目主页查看。")
                         : newer ? (info.Name ?? "") + "\r\n\r\n" + (info.Body ?? L10n.T("（该 release 未写说明）"))
                         : L10n.T("当前版本：") + UpdateChecker.CurrentVersion
                    + L10n.T("\r\n最新版本：") + (info != null ? info.Version : "?")
                    + L10n.T("\r\n\r\n没有可用的更新。")
                };
                form.Controls.Add(body);

                int btnY = body.Bottom + DpiScale.V(form, 12);
                var btnOpen = new AntdUI.Button
                {
                    Text = newer ? L10n.T("打开下载页") : L10n.T("打开项目主页"),
                    Size = new Size(DpiScale.V(form, 110), fieldH),
                    Location = new Point(pad, btnY),
                    Type = AntdUI.TTypeMini.Primary
                };
                btnOpen.Click += (s, e) =>
                {
                    string url = !failed && !string.IsNullOrEmpty(info.HtmlUrl)
                        ? info.HtmlUrl
                        : "https://github.com/" + UpdateChecker.DefaultRepo + "/releases";
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex) { DiagLog.Swallowed("ToolsDialogsLauncher.OpenReleasePage", ex); }
                };
                var btnClose = new AntdUI.Button
                {
                    Text = L10n.T("关闭"),
                    Size = new Size(DpiScale.V(form, 90), fieldH),
                    Location = new Point(body.Right - DpiScale.V(form, 90), btnY),
                    BackColor = GdtermColorTable.Surface2,
                    ForeColor = GdtermColorTable.Foreground,
                    DialogResult = DialogResult.Cancel
                };
                form.Controls.Add(btnOpen);
                form.Controls.Add(btnClose);
                form.CancelButton = btnClose;
                form.ClientSize = new Size(body.Right + pad, btnY + fieldH + pad);
                form.ShowDialog(_owner);
            }
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
                MessageBox.Show(_owner, L10n.T("安全模块未初始化"), L10n.T("提示"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 必须先解锁主会话，否则无法拿到旧主密码 / 无法重加密 kdbx
            if (_securityManager.IsLocked)
            {
                if (!MasterPasswordPrompt.Confirm(_owner, _securityManager, L10n.T("修改主密码")))
                    return;
            }

            using (var form = new ChangeMasterPasswordForm(_securityManager))
            {
                // async 事件处理器（finding-06）：重加密可能耗时数秒，禁止同步阻塞 UI 线程；
                // 异常必须接住（async void 会直接打进消息循环）并回传给对话框展示。
                form.ChangeRequested += async (s, args) =>
                {
                    try
                    {
                        // 1) 重加密 kdbx：用旧主密码解锁已存在库，再用新主密码重新加密保存
                        if (_keepassService != null)
                        {
                            var ok = await _keepassService.ChangeMasterPasswordAsync(args.OldPassword, args.NewPassword);
                            if (!ok)
                                throw new InvalidOperationException(L10n.T("密码库重加密失败：旧密码不正确或文件损坏"));
                        }

                        // 2) 更新 SecurityManager 哈希（同时会重新强度校验）
                        _securityManager.SetMasterPassword(args.OldPassword, args.NewPassword);

                        // 3) 持久化 master-password.ini
                        Program.PersistMasterPasswordConfig(_securityManager);
                    }
                    catch (Exception ex)
                    {
                        Gdterm.UI.Diagnostics.DiagLog.Swallowed("ToolsDialogs.ChangeMasterPassword", ex);
                        MessageBox.Show(form, L10n.T("修改主密码失败: ") + ex.Message, L10n.T("错误"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
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
                BuildHotkeyHelpText(),
                L10n.T("快捷键"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>用资源管理器打开诊断日志目录（程序主目录 logs\），方便排查问题。</summary>
        public void ShowLogsFolder()
        {
            var logsDir = Gdterm.UI.Program.LogsDir;
            if (!Directory.Exists(logsDir))
                Directory.CreateDirectory(logsDir);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + logsDir + "\"");
        }

        /// <summary>帮助 → 导出诊断包：一键打包版本+脱敏日志+脱敏配置，方便报障（nyaterm 对比吸收 A7）。</summary>
        /// <summary>诊断包一键导出（A7）：弹保存框→打包脱敏→Toast 提示路径（R08：成功提示走非阻塞通道）。</summary>
        public void ShowDiagnosticsPackage()
        {
            var path = DiagnosticsPackageExporter.BuildPackage(_owner);
            if (path != null)
            {
                try { DiagLog.Info("ToolsDialogs", "diagnostics package exported: " + path); } catch { }
                try { ToastNotifier.Success(L10n.T("已导出诊断包：") + path + L10n.T("（已脱敏，可直接发给维护者）")); }
                catch { /* Toast 不可用时日志已留痕 */ }
            }
        }

        public void ShowAbout()
        {
            // 版本与 git/CI 联动：构建时由 tools/gen-version.ps1 注入（CI=AppVeyor 版本，本地=git describe）
            var asm = typeof(ToolsDialogsLauncher).Assembly;
            var v = asm.GetName().Version;
            var info = Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                       as System.Reflection.AssemblyInformationalVersionAttribute;
            var verLine = L10n.T("版本 ") + (v != null ? v.ToString(3) : "?");
            if (!string.IsNullOrEmpty(info != null ? info.InformationalVersion : null)
                && info.InformationalVersion != (v != null ? v.ToString() : null))
                verLine += "\r\ngit: " + info.InformationalVersion;
            MessageBox.Show(
                _owner,
                L10n.T("gdterm - 绿色运维客户端\n") + verLine + L10n.T("\n\nSSH / RDP / SFTP / 串口 / 本地终端 / 运维工具箱"),
                L10n.T("关于"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 扫描中心（插件化扫描体系）：内置+用户自建脚本插件，支持热更新。
        /// </summary>
        public void OpenScannerCenter()
        {
            try
            {
                var store = _scanPluginStore;
                store.Start(); // 幂等
                using (var form = new ScannerCenterForm(store, _remoteSessionFactory))
                    form.ShowDialog(_owner);
            }
            catch (Exception ex)
            {
                Gdterm.UI.Diagnostics.DiagLog.Swallowed("ToolsDialogs.ScannerCenter", ex);
                MessageBox.Show(_owner, L10n.T("打开扫描中心失败: ") + ex.Message, L10n.T("错误"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
