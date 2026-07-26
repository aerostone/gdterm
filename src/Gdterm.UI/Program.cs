using System;
using System.IO;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.Connections;
using Gdterm.KeePass;
using Gdterm.Logging;
using Gdterm.Security;
using Gdterm.Security.Models;
using Gdterm.Sftp;
using Gdterm.Terminal;
using Gdterm.Rdp;
using Gdterm.Tools;
using Gdterm.Tools.Modules;
using Gdterm.Tunnel;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Forms;
using Gdterm.Logging.Models;
using System.Threading.Tasks;

namespace Gdterm.UI
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(appDir, "data");
            var configDir = Path.Combine(dataDir, "config");
            var logsDir = Path.Combine(dataDir, "logs");
            var toolsConfigDir = Path.Combine(configDir, "tools");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(toolsConfigDir);
            Directory.CreateDirectory(Path.Combine(logsDir, "commands"));
            Directory.CreateDirectory(Path.Combine(logsDir, "terminal"));

            // 全局未处理异常：落盘 crash.jsonl + 审计（audit 就绪后补写）
            CrashLog.Initialize(logsDir);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                CrashLog.Write("Application.ThreadException", e.Exception, isTerminating: false);
                try
                {
                    MessageBox.Show(
                        "发生未处理错误，详情已写入 data/logs/crash.jsonl\n\n" +
                        (e.Exception != null ? e.Exception.Message : ""),
                        "gdterm",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                CrashLog.Write("AppDomain.UnhandledException", ex, isTerminating: e.IsTerminating);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
                try { e.SetObserved(); } catch { }
            };

            var connectionsPath = Path.Combine(dataDir, "connections.json");
            var keepassPath = Path.Combine(dataDir, "gdterm.kdbx");
            var passwordConfigPath = Path.Combine(dataDir, "master-password.json");
            // bookmarks + recent 都在 dataDir 下由 BookmarkStoreJson 管理
            var commandHistoryDir = Path.Combine(logsDir, "commands");
            var dangerousCmdPath = Path.Combine(configDir, "dangerous-commands.json");
            var folderCredPath = Path.Combine(dataDir, "folder-credentials.json");
            var sessionStatePath = Path.Combine(dataDir, "session-state.json");
            var quickCmdPath = Path.Combine(dataDir, "quick-commands.json");
            var keybindPath = Path.Combine(configDir, "keybindings.json");
            var highlightPath = Path.Combine(configDir, "highlights.json");

            MasterPasswordConfig savedPasswordConfig = null;
            if (File.Exists(passwordConfigPath))
            {
                try
                {
                    savedPasswordConfig = ParsePasswordConfig(File.ReadAllText(passwordConfigPath));
                }
                catch { }
            }

            bool isFirstRun = savedPasswordConfig == null;
            var securityManager = new SecurityManager(
                idleTimeout: TimeSpan.FromMinutes(10),
                passwordConfig: savedPasswordConfig);

            if (isFirstRun)
            {
                using (var wizard = new SetupWizardForm(securityManager))
                {
                    if (wizard.ShowDialog() != DialogResult.OK)
                        return;
                }
                SavePasswordConfig(securityManager.GetPasswordConfig(), passwordConfigPath);
            }

            var connectionStore = new ConnectionStoreJson(connectionsPath);
            var tunnelManager = new TunnelManager();
            var terminalFactory = new TerminalSessionFactory();
            var rdpFactory = new RdpClientFactory();
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new KeePassService(keepassPath);
            // 异常退出时尽量清掉本进程注入的 TERMSRV 凭据
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                DiagLog.Try("Program.ProcessExit.CleanupRdp", () => keepassService.CleanupAllRdpCredentials());
            };
            AppDomain.CurrentDomain.DomainUnload += (s, e) =>
            {
                DiagLog.Try("Program.DomainUnload.CleanupRdp", () => keepassService.CleanupAllRdpCredentials());
            };
            var auditLogger = new AuditLogger(logsDir);
            GlobalExceptionBridge.Attach(auditLogger);
            var aiConfig = new AiConfiguration
            {
                ApiEndpoint = "https://api.openai.com/v1",
                Model = "gpt-4",
                MaxTokens = 2048,
                Temperature = 0.7
            };
            var aiService = new AiAssistantService(aiConfig);
            var dangerousCmdDetector = new DangerousCommandDetector(dangerousCmdPath);
            var folderCredStore = new FolderCredentialStoreJson(folderCredPath);
            var sessionStateStore = new SessionStateStore(sessionStatePath);
            var bookmarkStore = new BookmarkStoreJson(dataDir);
            var commandHistoryStore = new CommandHistoryStore(commandHistoryDir);
            var quickCommandStore = new QuickCommandStore(quickCmdPath);
            var keyBindingStore = new TerminalKeyBindingStore(keybindPath);
            var highlightStore = new HighlightStore(highlightPath);
            var reconnectWatchdog = new AutoReconnectWatchdog { MaxRetries = 5 };
            var multiChannelManager = new MultiChannelManager();

            // 工具注册（内置，非插件）
            var toolRegistry = new ToolRegistry();
            try
            {
                toolRegistry.Register(new CertificateInstallerTool());
                toolRegistry.Register(new TimeSyncTool());
                toolRegistry.Register(new RepoConfigTool());
                toolRegistry.Register(new PortScannerTool());
                toolRegistry.Register(new NetworkScannerTool());
                toolRegistry.LoadAllConfigs();
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("Program.ToolRegistryInit", ex);
            }

            // Secret scanner：默认不后台扫
            var secretConfig = SecretScanConfig.GetDefault();
            secretConfig.EnableBackgroundScan = false;
            var secretScanner = new SecretScanner(secretConfig);

            var mainForm = new MainForm(
                connectionStore,
                tunnelManager,
                terminalFactory,
                sftpFactory,
                keepassService,
                auditLogger,
                aiService,
                securityManager,
                dangerousCmdDetector,
                folderCredStore,
                sessionStateStore,
                rdpFactory,
                bookmarkStore,
                commandHistoryStore,
                quickCommandStore,
                keyBindingStore,
                highlightStore,
                reconnectWatchdog,
                multiChannelManager,
                toolRegistry,
                secretScanner);

            // finding-03：旧 SHA256 解锁升级为 PBKDF2 后立刻落盘
            securityManager.LockStateChanged += (s, e) =>
            {
                if (e != null && !e.IsLocked && securityManager.PasswordConfigUpgraded)
                {
                    SavePasswordConfig(securityManager.GetPasswordConfig(), passwordConfigPath);
                    securityManager.PasswordConfigUpgraded = false;
                }
            };

            mainForm.FormClosed += (s, e) =>
            {
                SavePasswordConfig(securityManager.GetPasswordConfig(), passwordConfigPath);
                DiagLog.Try("Program.FormClosed.SaveTools", () => toolRegistry.SaveAllConfigs());
                DiagLog.Try("Program.FormClosed.Watchdog", () => reconnectWatchdog.Dispose());
                DiagLog.Try("Program.FormClosed.SecretScanner", () => secretScanner.Dispose());
                DiagLog.Try("Program.FormClosed.CmdHistory", () => commandHistoryStore.Dispose());
            };

            Application.Run(mainForm);
        }

        private static void SavePasswordConfig(MasterPasswordConfig config, string path)
        {
            if (config == null) return;
            try
            {
                var algorithm = string.IsNullOrEmpty(config.Algorithm) ? "pbkdf2" : config.Algorithm;
                var iterations = config.Iterations > 0 ? config.Iterations : SecurityManager.DefaultPbkdf2Iterations;
                var json = string.Format(
                    "{{\"passwordHash\":\"{0}\",\"salt\":\"{1}\",\"algorithm\":\"{2}\",\"iterations\":{3},\"lastChanged\":\"{4:O}\"}}",
                    config.PasswordHash ?? "",
                    config.Salt ?? "",
                    algorithm,
                    iterations,
                    config.LastChanged);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("Program.SavePasswordConfig", ex);
            }
        }

        private static MasterPasswordConfig ParsePasswordConfig(string json)
        {
            var hash = ExtractJsonString(json, "passwordHash");
            var salt = ExtractJsonString(json, "salt");
            var lastChangedStr = ExtractJsonString(json, "lastChanged");
            var algorithm = ExtractJsonString(json, "algorithm");
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
                return null;
            var config = new MasterPasswordConfig
            {
                PasswordHash = hash,
                Salt = salt,
                Algorithm = algorithm // null/empty → 旧版 SHA256
            };
            var iterStr = ExtractJsonNumber(json, "iterations");
            if (!string.IsNullOrEmpty(iterStr) && int.TryParse(iterStr, out var iters) && iters > 0)
                config.Iterations = iters;
            if (DateTime.TryParse(lastChangedStr, out var dt))
                config.LastChanged = dt;
            return config;
        }

        private static string ExtractJsonNumber(string json, string key)
        {
            var pattern = "\"" + key + "\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end <= start) return null;
            return json.Substring(start, end - start);
        }

        private static string ExtractJsonString(string json, string key)
        {
            var pattern = "\"" + key + "\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return null;
            start += pattern.Length;
            int end = start;
            while (end < json.Length)
            {
                if (json[end] == '\\') { end += 2; continue; }
                if (json[end] == '"') break;
                end++;
            }
            return json.Substring(start, end - start);
        }
    }

    /// <summary>
    /// 把全局异常钩子接到 IAuditLogger（audit 就绪后 Attach）。
    /// CrashLog 始终先落盘，审计是第二通道。
    /// </summary>
    internal static class GlobalExceptionBridge
    {
        private static IAuditLogger _audit;
        private static int _attached;

        public static void Attach(IAuditLogger audit)
        {
            _audit = audit;
            if (System.Threading.Interlocked.Exchange(ref _attached, 1) == 1)
                return;

            Application.ThreadException += (s, e) =>
            {
                try
                {
                    _audit?.LogSecurityEvent(
                        SecurityEvent.UnhandledUiException,
                        e.Exception != null ? e.Exception.ToString() : "unknown");
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    _audit?.LogSecurityEvent(
                        SecurityEvent.UnhandledDomainException,
                        ex != null ? ex.ToString() : (e.ExceptionObject != null ? e.ExceptionObject.ToString() : "unknown"));
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try
                {
                    _audit?.LogSecurityEvent(
                        SecurityEvent.UnobservedTaskException,
                        e.Exception != null ? e.Exception.ToString() : "unknown");
                }
                catch { }
            };
        }
    }

}
