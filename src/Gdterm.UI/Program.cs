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
using Gdterm.Tools;
using Gdterm.Tools.Modules;
using Gdterm.Tunnel;
using Gdterm.UI.Forms;

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
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new KeePassService(keepassPath);
            // 异常退出时尽量清掉本进程注入的 TERMSRV 凭据
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { keepassService.CleanupAllRdpCredentials(); } catch { }
            };
            AppDomain.CurrentDomain.DomainUnload += (s, e) =>
            {
                try { keepassService.CleanupAllRdpCredentials(); } catch { }
            };
            var auditLogger = new AuditLogger(logsDir);
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
            catch { }

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
                bookmarkStore,
                commandHistoryStore,
                quickCommandStore,
                keyBindingStore,
                highlightStore,
                reconnectWatchdog,
                multiChannelManager,
                toolRegistry,
                secretScanner);

            mainForm.FormClosed += (s, e) =>
            {
                SavePasswordConfig(securityManager.GetPasswordConfig(), passwordConfigPath);
                try { toolRegistry.SaveAllConfigs(); } catch { }
                try { reconnectWatchdog.Dispose(); } catch { }
                try { secretScanner.Dispose(); } catch { }
                try { commandHistoryStore.Dispose(); } catch { }
            };

            Application.Run(mainForm);
        }

        private static void SavePasswordConfig(MasterPasswordConfig config, string path)
        {
            if (config == null) return;
            try
            {
                var json = string.Format(
                    "{{\"passwordHash\":\"{0}\",\"salt\":\"{1}\",\"lastChanged\":\"{2:O}\"}}",
                    config.PasswordHash ?? "",
                    config.Salt ?? "",
                    config.LastChanged);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private static MasterPasswordConfig ParsePasswordConfig(string json)
        {
            var hash = ExtractJsonString(json, "passwordHash");
            var salt = ExtractJsonString(json, "salt");
            var lastChangedStr = ExtractJsonString(json, "lastChanged");
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
                return null;
            var config = new MasterPasswordConfig { PasswordHash = hash, Salt = salt };
            if (DateTime.TryParse(lastChangedStr, out var dt))
                config.LastChanged = dt;
            return config;
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
}
