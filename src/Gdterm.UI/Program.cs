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
        /// <summary>诊断日志根目录：程序主目录下 logs\（绿色版便携，随包携带）。</summary>
        public static readonly string LogsDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "logs");

        [STAThread]
        static void Main()
        {
            // app.manifest 已声明 PerMonitorV2 DPI awareness，OS 会自动处理缩放。
            // 不要再调用 SetProcessDPIAware —— 那会和 manifest 冲突，且让 WinForms 再做一次手工缩放。
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 统一 ToolStrip / MenuStrip / StatusStrip 渲染：暗色专业主题。
            // 默认的 SystemRenderer 在自定义暗色 BackColor 上会留灰边;
            // ProfessionalRenderer 跟随系统主题色彩表格，没有突兑。
            // 这个小修改让菜单/状态栏瞬间「稳重」，不再发虚。
            try
            {
                ToolStripManager.Renderer = new Gdterm.UI.Diagnostics.GdtermToolStripRenderer();
            }
            catch { }

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Path.Combine(appDir, "data");
            var configDir = Path.Combine(dataDir, "config");
            var logsDir = LogsDir;
            var toolsConfigDir = Path.Combine(configDir, "tools");

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(toolsConfigDir);
            Directory.CreateDirectory(Path.Combine(logsDir, "commands"));
            Directory.CreateDirectory(Path.Combine(logsDir, "terminal"));

            try
            {
                GlobalAppearance = Gdterm.UI.Forms.AppearanceSettings.Load(
                    Path.Combine(configDir, "appearance.ini"));
            }
            catch { GlobalAppearance = new Gdterm.UI.Forms.AppearanceSettings(); }
            // 初始外壳主题（与终端 ColorScheme 独立）
            try { Gdterm.UI.Diagnostics.GdtermColorTable.ApplyTheme(GlobalAppearance.UiTheme); } catch { }


            // 全局未处理异常：落盘 diag.log + 审计（audit 就绪后补写）
            CrashLog.Initialize(logsDir);

            // 原生 DLL 搜索目录：winpty.dll 等集中放 lib\（DllImport 默认只搜 exe 目录，
            // 集中分类后需 SetDllDirectory 让 Windows 加载器也搜 lib\；lib\ 不存在则跳过，
            // 开发构建态 winpty.dll 仍在 exe 目录照样可加载）。
            try
            {
                var libDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lib");
                if (Directory.Exists(libDir))
                    SetDllDirectory(libDir);
            }
            catch { }
            // 随附终端工具加入 PATH：本地终端子进程（ConPTY/winpty/cmd/PowerShell）直接敲 fzf/fd 即可用。
            // 业界布局 bin\ 放 CLI 工具；保留 fzf\/fd\ 兑容旧发行包布局。追加在末尾，系统里若已装更新版本优先用系统的。
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var toolDirs = new[]
                {
                    Path.Combine(baseDir, "bin"),
                    Path.Combine(baseDir, "fzf"),
                    Path.Combine(baseDir, "fd")
                };
                var existing = Environment.GetEnvironmentVariable("PATH") ?? "";
                var add = new System.Text.StringBuilder();
                foreach (var d in toolDirs)
                {
                    if (Directory.Exists(d) &&
                        (existing + ";" + add).IndexOf(d, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        if (add.Length > 0) add.Append(';');
                        add.Append(d);
                    }
                }
                if (add.Length > 0)
                    Environment.SetEnvironmentVariable("PATH", existing.TrimEnd(';') + ";" + add);
            }
            catch { }

            // RDP 诊断日志接线：Gdterm.Rdp 的静态 sink → diag.log（source 带级别前缀，与 DiagLog 同约定）
            try
            {
                Gdterm.Rdp.RdpLog.Initialize((source, message) =>
                    CrashLog.Write(source, new Exception(message ?? ""), isTerminating: false));
            }
            catch { }
            // 终端层诊断日志接线：渲染器/会话/引擎（Gdterm.Terminal 不引用 UI，同 RdpLog 模式）
            try
            {
                Gdterm.Terminal.Diagnostics.TerminalLog.Initialize((source, message) =>
                    CrashLog.Write(source, new Exception(message ?? ""), isTerminating: false));
            }
            catch { }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                CrashLog.Write("Application.ThreadException", e.Exception, isTerminating: false);
                try
                {
                    MessageBox.Show(
                        "发生未处理错误，详情已写入 logs/diag.log\n\n" +
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
            // 配置优先 INI；兼容旧 master-password.json
            var passwordConfigPath = Path.Combine(dataDir, "master-password.ini");
            var passwordConfigPathLegacy = Path.Combine(dataDir, "master-password.json");
            // bookmarks + recent 都在 dataDir 下由 BookmarkStoreJson 管理
            var commandHistoryDir = Path.Combine(logsDir, "commands");
            var dangerousCmdPath = Path.Combine(configDir, "dangerous-commands.json");
            var folderCredPath = Path.Combine(dataDir, "folder-credentials.json");
            var sessionStatePath = Path.Combine(dataDir, "session-state.json");
            var quickCmdPath = Path.Combine(dataDir, "quick-commands.json");
            var keybindPath = Path.Combine(configDir, "keybindings.json");
            var highlightPath = Path.Combine(configDir, "highlights.json");

            MasterPasswordConfig savedPasswordConfig = null;
            try
            {
                if (File.Exists(passwordConfigPath))
                    savedPasswordConfig = ParsePasswordConfig(File.ReadAllText(passwordConfigPath));
                else if (File.Exists(passwordConfigPathLegacy))
                    savedPasswordConfig = ParsePasswordConfig(File.ReadAllText(passwordConfigPathLegacy));
            }
            catch { }

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

            // connections.json 主机/用户名：有主密码时用 gdk2 风格可逆保护（无主密码则明文兼容）
            WireConnectionHostProtection(securityManager);
            var connectionStore = new ConnectionStoreJson(connectionsPath);
            var tunnelManager = new TunnelManager();
            var terminalFactory = new TerminalSessionFactory();
            var rdpFactory = new RdpClientFactory();
            var sftpFactory = new SftpServiceFactory();
            var keepassService = new KeePassService(keepassPath);
            // 首次运行（或 .kdbx 被删除）时，用主密码初始化 KeePass 密码库；否则尝试解锁，
            // 这样进入主界面后连接才不会因“密码库未解锁”而拿不到凭据。
            try
            {
                var masterPw = securityManager.GetMasterPassword();
                if (!string.IsNullOrEmpty(masterPw))
                {
                    keepassService.EnsureDatabaseAsync(masterPw).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { DiagLog.Swallowed("Program.KeePassInit", ex); }
            // 异常退出时尽量清掉本进程注入的 TERMSRV 凭据
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                DiagLog.Try("Program.ProcessExit.CleanupRdp", () => keepassService.CleanupAllRdpCredentials());
            };
            AppDomain.CurrentDomain.DomainUnload += (s, e) =>
            {
                DiagLog.Try("Program.DomainUnload.CleanupRdp", () => keepassService.CleanupAllRdpCredentials());
            };
            // 试运行默认：连接/命令/安全/凭据使用全开，便于排查「点连接无反应」
            var auditConfig = new AuditLogConfig
            {
                LogConnections = true,
                LogCommands = true,
                LogSecurityEvents = true,
                LogCredentialUsage = true,
                LogAiInteractions = true,
                SanitizeCommands = true,
                SanitizeAiContent = true,
                EncryptLogs = false,
                MaxFileCount = 20,
                MaxFileSizeMB = 10,
                RetentionDays = 30
            };
            var auditLogger = new AuditLogger(logsDir, auditConfig);
            GlobalExceptionBridge.Attach(auditLogger);
            DiagLog.Info("Program.Main", "gdterm v" + (typeof(Program).Assembly.GetName().Version)
                + "; audit debug defaults enabled; logsDir=" + logsDir);
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
                // INI 人可读；hash/salt 仍是不可逆派生结果
                var ini = string.Format(
                    "[master]\r\npasswordHash={0}\r\nsalt={1}\r\nalgorithm={2}\r\niterations={3}\r\nlastChanged={4:O}\r\n",
                    config.PasswordHash ?? "",
                    config.Salt ?? "",
                    algorithm,
                    iterations,
                    config.LastChanged);
                // 始终写 .ini；若传入的是 legacy json 路径也改写为 ini 旁路
                var iniPath = path;
                if (iniPath != null && iniPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    iniPath = Path.ChangeExtension(iniPath, ".ini");
                File.WriteAllText(iniPath ?? path, ini);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("Program.SavePasswordConfig", ex);
            }
        }

        /// <summary>
        /// 持久化主密码哈希到 data/master-password.ini。
        /// 供 修改主密码 流程在 SecurityManager.SetMasterPassword 后调用。
        /// </summary>
        public static void PersistMasterPasswordConfig(ISecurityManager securityManager)
        {
            if (securityManager == null) return;
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            var iniPath = Path.Combine(dataDir, "master-password.ini");
            SavePasswordConfig(securityManager.GetPasswordConfig(), iniPath);
        }

        private static MasterPasswordConfig ParsePasswordConfig(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.TrimStart();
            // INI（[master] 或 passwordHash=）优先；旧 JSON 以 { 开头
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                var hash = ExtractIniValue(text, "passwordHash");
                var salt = ExtractIniValue(text, "salt");
                var algorithm = ExtractIniValue(text, "algorithm");
                var lastChangedStr = ExtractIniValue(text, "lastChanged");
                if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
                    return null;
                var config = new MasterPasswordConfig
                {
                    PasswordHash = hash,
                    Salt = salt,
                    Algorithm = algorithm
                };
                var iterStr = ExtractIniValue(text, "iterations");
                int iters;
                if (!string.IsNullOrEmpty(iterStr) && int.TryParse(iterStr, out iters) && iters > 0)
                    config.Iterations = iters;
                DateTime dt;
                if (DateTime.TryParse(lastChangedStr, out dt))
                    config.LastChanged = dt;
                return config;
            }

            // 旧 JSON 兼容
            var jhash = ExtractJsonString(text, "passwordHash");
            var jsalt = ExtractJsonString(text, "salt");
            var jlast = ExtractJsonString(text, "lastChanged");
            var jalg = ExtractJsonString(text, "algorithm");
            if (string.IsNullOrEmpty(jhash) || string.IsNullOrEmpty(jsalt))
                return null;
            var jconfig = new MasterPasswordConfig
            {
                PasswordHash = jhash,
                Salt = jsalt,
                Algorithm = jalg
            };
            var jiter = ExtractJsonNumber(text, "iterations");
            int jit;
            if (!string.IsNullOrEmpty(jiter) && int.TryParse(jiter, out jit) && jit > 0)
                jconfig.Iterations = jit;
            DateTime jdt;
            if (DateTime.TryParse(jlast, out jdt))
                jconfig.LastChanged = jdt;
            return jconfig;
        }

        private static string ExtractIniValue(string ini, string key)
        {
            if (string.IsNullOrEmpty(ini) || string.IsNullOrEmpty(key)) return null;
            var lines = ini.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var prefix = key + "=";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            }
            return null;
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

        /// <summary>
        /// 试运行加固：connections.json 中 host/username 写 gdh1: 混淆（固定密钥 XOR，可离线读）。
        /// 旧明文 / gdh2 仍兼容；密码本体只在 kdbx。
        /// </summary>
        private static void WireConnectionHostProtection(SecurityManager securityManager)
        {
            if (securityManager == null) return;
            ConnectionStoreJson.SetHostProtector(
                plain => ProtectHostField(securityManager, plain),
                stored => UnprotectHostField(securityManager, stored));
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true,
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        private const string HostSecretKey = "gdterm-conn-host-key-v1";

        private static string ProtectHostField(SecurityManager security, string plain)
        {
            // 试运行：固定密钥 XOR 混淆主机/用户名，启动未解锁也可读写。
            // 真机密仍只在 kdbx；此举防 connections.json 裸奔主机，非强加密。
            if (string.IsNullOrEmpty(plain)) return plain;
            if (plain.StartsWith("gdh1:") || plain.StartsWith("gdh2:")) return plain;
            try { return "gdh1:" + XorB64(plain, HostSecretKey); }
            catch { return plain; }
        }

        private static string UnprotectHostField(SecurityManager security, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            try
            {
                if (stored.StartsWith("gdh1:"))
                    return XorB64Decode(stored.Substring(5), HostSecretKey);
                if (stored.StartsWith("gdh2:"))
                {
                    // 兼容曾写入的主密码 AES：解锁后可读
                    var master = security != null ? security.GetMasterPassword() : null;
                    if (!string.IsNullOrEmpty(master))
                        return AesUnprotect(stored.Substring(5), master);
                    return stored;
                }
                return stored; // 旧明文兼容
            }
            catch { return stored; }
        }

        private static string XorB64(string plain, string key)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(plain ?? "");
            var k = System.Text.Encoding.UTF8.GetBytes(key ?? "");
            for (int i = 0; i < data.Length; i++) data[i] ^= k[i % k.Length];
            return Convert.ToBase64String(data);
        }

        private static string XorB64Decode(string b64, string key)
        {
            var data = Convert.FromBase64String(b64);
            var k = System.Text.Encoding.UTF8.GetBytes(key ?? "");
            for (int i = 0; i < data.Length; i++) data[i] ^= k[i % k.Length];
            return System.Text.Encoding.UTF8.GetString(data);
        }

        private static string AesProtect(string plain, string master)
        {
            var salt = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(salt);
            byte[] key;
            using (var derive = new System.Security.Cryptography.Rfc2898DeriveBytes(master, salt, 10000))
                key = derive.GetBytes(32);
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                aes.Key = key;
                aes.GenerateIV();
                using (var enc = aes.CreateEncryptor())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
                    var cipher = enc.TransformFinalBlock(bytes, 0, bytes.Length);
                    var all = new byte[salt.Length + aes.IV.Length + cipher.Length];
                    Buffer.BlockCopy(salt, 0, all, 0, salt.Length);
                    Buffer.BlockCopy(aes.IV, 0, all, salt.Length, aes.IV.Length);
                    Buffer.BlockCopy(cipher, 0, all, salt.Length + aes.IV.Length, cipher.Length);
                    return Convert.ToBase64String(all);
                }
            }
        }

        private static string AesUnprotect(string payload, string master)
        {
            var all = Convert.FromBase64String(payload);
            if (all.Length < 33) return payload;
            var salt = new byte[16];
            var iv = new byte[16];
            Buffer.BlockCopy(all, 0, salt, 0, 16);
            Buffer.BlockCopy(all, 16, iv, 0, 16);
            var cipher = new byte[all.Length - 32];
            Buffer.BlockCopy(all, 32, cipher, 0, cipher.Length);
            byte[] key;
            using (var derive = new System.Security.Cryptography.Rfc2898DeriveBytes(master, salt, 10000))
                key = derive.GetBytes(32);
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                {
                    var bytes = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
            }
        }

        /// <summary>全局外观，供新开终端读取。</summary>
        internal static Gdterm.UI.Forms.AppearanceSettings GlobalAppearance { get; set; }
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
