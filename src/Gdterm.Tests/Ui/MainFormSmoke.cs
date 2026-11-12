using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.Connections;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Tunnel;
using Gdterm.UI.Forms;
using Gdterm.Rdp;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 主窗冒烟：用临时目录构造 MainForm 全依赖（不碰真实 data/），Show 后 dump 整棵树。
    /// 锁定时跳过会话恢复（_sessionRestored 逻辑不变）；只验 layout，不连任何主机。
    /// </summary>
    public static class MainFormSmoke
    {
        public static void Run(string outDir, Action<string> log, Action<bool, string> check)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "gdterm-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var connectionStore = new ConnectionStoreJson(Path.Combine(tmp, "connections.json"));
            var tunnelManager = new TunnelManager();
            var terminalFactory = new TerminalSessionFactory();
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new FakeKeePassService();
            var auditLogger = new AuditLogger(Path.Combine(tmp, "logs"));
            var aiService = new AiAssistantService(new AiConfiguration());
            var securityManager = new SecurityManager();
            // 保持锁定：解锁会走 Shown→SessionState.Restore（空 session 下仍可能弹连接），冒烟只验 layout 不连主机。
            // 锁定时 _lockOverlay 可见，dump 照做（遮罩也是盒模型的一部分）。
            var dangerousCmdDetector = new DangerousCommandDetector(Path.Combine(tmp, "dangerous.json"));
            var folderCredStore = new FolderCredentialStoreJson(Path.Combine(tmp, "folder.json"));
            var sessionStore = new SessionStateStore(Path.Combine(tmp, "session.json"));
            var rdpFactory = new RdpClientFactory();
            var bookmarkStore = new BookmarkStoreJson(tmp);
            var commandHistoryStore = new CommandHistoryStore(Path.Combine(tmp, "cmdhist"));
            var quickCommandStore = new QuickCommandStore(Path.Combine(tmp, "quick.json"));
            var keyBindingStore = new TerminalKeyBindingStore(Path.Combine(tmp, "keybind.json"));
            var highlightStore = new HighlightStore(Path.Combine(tmp, "highlight.json"));
            var reconnectWatchdog = new AutoReconnectWatchdog { MaxRetries = 0 };
            var multiChannelManager = new MultiChannelManager();
            var toolRegistry = new Gdterm.Tools.ToolRegistry();
            var secretScanner = new SecretScanner(SecretScanConfig.GetDefault());
            // 补齐 UiDependencies 最后两个可选依赖：漏传时 MainForm 走的是**生产不存在的降级形态**
            // （ToolsDialogsLauncher 对 null OtpStore 静默降级），dump 覆盖的就不是真实启动配置。
            var commandTemplateStore = new CommandTemplateStore(Path.Combine(tmp, "cmdtemplate.json"));
            var otpStore = new Gdterm.Security.Otp.OtpStoreJson(Path.Combine(tmp, "otp.json"));

            log("STEP mainform-construct");
            // 审计 F4：组合根依赖包
            var deps = new Gdterm.UI.Services.UiDependencies
            {
                ConnectionStore = connectionStore,
                TunnelManager = tunnelManager,
                TerminalFactory = terminalFactory,
                SftpFactory = sftpFactory,
                KeePassService = keepassService,
                AuditLogger = auditLogger,
                AiService = aiService,
                SecurityManager = securityManager,
                DangerousCommandDetector = dangerousCmdDetector,
                FolderCredentialStore = folderCredStore,
                SessionStateStore = sessionStore,
                RdpFactory = rdpFactory,
                BookmarkStore = bookmarkStore,
                CommandHistoryStore = commandHistoryStore,
                QuickCommandStore = quickCommandStore,
                KeyBindingStore = keyBindingStore,
                HighlightStore = highlightStore,
                ReconnectWatchdog = reconnectWatchdog,
                MultiChannelManager = multiChannelManager,
                ToolRegistry = toolRegistry,
                SecretScanner = secretScanner,
                CommandTemplateStore = commandTemplateStore,
                OtpStore = otpStore
            };
            using (var f = new MainForm(deps))
            {
                log("STEP mainform-show");
                f.Show();
                f.BringToFront();
                f.Activate();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                Thread.Sleep(600);
                Application.DoEvents();

                log("STEP mainform-assert");
                // 主窗 layout 断言：菜单/树/Tab/底栏/状态齐备
                int count = CountControls(f);
                check(count > 40, "mainform-controls=" + count + " (>40)");
                check(FindByType(f, "ConnectionTreeControl") != null, "tree-found");
                check(FindByType(f, "TabContainerControl") != null, "tabs-found");
                // 场景要求（2026-11-01 复核修正）：自绘命中区规则的真实目标（标签 × 区）
                // 只在有标签时存在。空会话下 dump 的 hitRegions 永远为空，规则受检=0 → 空过。
                // 开 1 个本地终端标签：本地 PTY，不连主机不用凭据，符合“冒烟不连主机”。
                // 先断言存在、再判尺寸（检查器侧）：存在性是场景前提，不是可选项。
                var tabs = FindByType(f, "TabContainerControl") as Gdterm.UI.Controls.TabContainerControl;
                check(tabs != null, "tabs-instance");
                if (tabs != null)
                {
                    tabs.OpenLocalTerminal();
                    Application.DoEvents();
                    Thread.Sleep(500);
                    Application.DoEvents();
                    check(tabs.GetHitRegions().Count >= 1,
                        "hitregions>0 after open-tab (label close target must exist)");
                }
                check(FindByType(f, "BottomBarPanel") != null, "bottombar-found");
                // v2 单栏：StatusBarControl 已合并进 BottomBarPanel（独立状态条不存在），状态项由底栏承载
                check(FindByType(f, "BottomBarPanel") != null, "status-found");
                check(f.MainMenuStrip != null, "menu-found");
                // 截图：R7 主题色残留查到 TabControl/TabPage 还是系统默认 #F0F0F0，
                // 这类"白条/白页"必须眼见为实——没有图就只能猜它到底露不露出来。
                try { UiSmokeRunner.Shot(f, Path.Combine(outDir, "mainform.png")); } catch (Exception exShot) { log("SHOT_FAIL " + exShot.Message); }
                log("STEP mainform-dump");
                File.WriteAllText(Path.Combine(outDir, "mainform.json"), UiTreeDumper.Dump(f), System.Text.Encoding.UTF8);
            }

            try { Directory.Delete(tmp, true); } catch { }
            try { securityManager.Dispose(); } catch { }
            try { auditLogger.Dispose(); } catch { }
            try { reconnectWatchdog.Dispose(); } catch { }
            try { multiChannelManager.Dispose(); } catch { }
            try { toolRegistry.Dispose(); } catch { }
            try { secretScanner.Dispose(); } catch { }
            try { commandHistoryStore.Dispose(); } catch { }
            try { dangerousCmdDetector.Dispose(); } catch { }
            try { aiService.Dispose(); } catch { }
        }

        private static int CountControls(Control root)
        {
            int n = 1;
            foreach (Control c in root.Controls) n += CountControls(c);
            return n;
        }

        private static Control FindByType(Control root, string typeName)
        {
            if (root.GetType().Name == typeName) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByType(c, typeName);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Control FindByName(Control root, string name)
        {
            if (root.Name == name) return root;
            foreach (Control c in root.Controls)
            {
                var hit = FindByName(c, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
