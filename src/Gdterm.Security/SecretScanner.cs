using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Gdterm.Security
{
    /// <summary>
    /// 本地敏感信息扫描引擎——三层检测：文件名匹配 + 香农熵 + 正则模式
    /// </summary>
    public class SecretScanner : IDisposable
    {
        private readonly SecretScanConfig _config;
        private readonly List<string> _whitelist;
        private CancellationTokenSource _cts;
        private Thread _scanThread;

        // ── 事件 ──
        public event Action<SecretFinding> FindingDetected;
        public event Action<SecretScanReport> ScanCompleted;
        public event Action<int, int> ProgressChanged; // current, total
        public event Action<string> ErrorOccurred;

        // ── 状态 ──
        public bool IsScanning { get; private set; }
        public int FilesScanned { get; private set; }
        public int FindingsCount { get; private set; }

        public SecretScanner(SecretScanConfig config)
        {
            _config = config ?? SecretScanConfig.GetDefault();
            _whitelist = new List<string>();
            LoadWhitelist();
        }

        /// <summary>开始异步扫描（后台线程）</summary>
        public void StartScanAsync()
        {
            if (IsScanning) return;

            _cts = new CancellationTokenSource();
            _scanThread = new Thread(() => ScanWorker(_cts.Token))
            {
                IsBackground = true,
                Priority = _config.ScanPriority == 0 ? ThreadPriority.Lowest :
                           _config.ScanPriority == 1 ? ThreadPriority.BelowNormal :
                           ThreadPriority.Normal,
                Name = "gdterm.SecretScanner"
            };
            IsScanning = true;
            FilesScanned = 0;
            FindingsCount = 0;
            _scanThread.Start();
        }

        /// <summary>停止扫描</summary>
        public void StopScan()
        {
            _cts?.Cancel();
            IsScanning = false;
        }

        /// <summary>同步扫描（阻塞调用线程）</summary>
        public SecretScanReport Scan()
        {
            var report = new SecretScanReport();
            var allFiles = CollectFiles();
            int total = allFiles.Count;

            for (int i = 0; i < total; i++)
            {
                var filePath = allFiles[i];
                try
                {
                    var findings = ScanFile(filePath);
                    foreach (var f in findings)
                    {
                        if (!_whitelist.Contains(f.FilePath + ":" + f.MatchedContent))
                        {
                            report.Findings.Add(f);
                            FindingsCount++;
                            FindingDetected?.Invoke(f);
                        }
                    }
                    FilesScanned++;
                    ProgressChanged?.Invoke(i + 1, total);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(string.Format("{0}: {1}", filePath, ex.Message));
                    report.FilesSkipped++;
                }
            }

            report.FilesScanned = FilesScanned;
            report.ScanCompleted = DateTime.Now;
            ScanCompleted?.Invoke(report);
            return report;
        }

        // ── 核心扫描逻辑 ──

        private void ScanWorker(CancellationToken ct)
        {
            try
            {
                var report = new SecretScanReport();
                var allFiles = CollectFiles();
                int total = allFiles.Count;

                for (int i = 0; i < total; i++)
                {
                    if (ct.IsCancellationRequested) break;

                    var filePath = allFiles[i];
                    try
                    {
                        var findings = ScanFile(filePath);
                        foreach (var f in findings)
                        {
                            if (!_whitelist.Contains(f.FilePath + ":" + f.MatchedContent))
                            {
                                report.Findings.Add(f);
                                FindingsCount++;
                                FindingDetected?.Invoke(f);
                            }
                        }
                        FilesScanned++;
                        ProgressChanged?.Invoke(i + 1, total);
                    }
                    catch { report.FilesSkipped++; }

                    // 降低CPU占用：每扫描50个文件让出时间片
                    if (i % 50 == 49) Thread.Sleep(10);
                }

                report.FilesScanned = FilesScanned;
                report.ScanCompleted = DateTime.Now;
                ScanCompleted?.Invoke(report);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex.Message);
            }
            finally
            {
                IsScanning = false;
            }
        }

        /// <summary>扫描单个文件</summary>
        public List<SecretFinding> ScanFile(string filePath)
        {
            var findings = new List<SecretFinding>();

            // 第1层：敏感文件名检测
            var fileFinding = CheckSensitiveFileName(filePath);
            if (fileFinding != null) findings.Add(fileFinding);

            // 第2层+第3层：内容扫描（仅文本文件）
            if (IsTextFile(filePath) && new FileInfo(filePath).Length <= _config.MaxFileSizeBytes)
            {
                try
                {
                    var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                    for (int lineNum = 0; lineNum < lines.Length; lineNum++)
                    {
                        var line = lines[lineNum];

                        // 第2层：高熵字符串检测
                        var entropyFindings = CheckHighEntropyStrings(filePath, lineNum + 1, line);
                        findings.AddRange(entropyFindings);

                        // 第3层：正则模式匹配
                        var patternFindings = CheckPatterns(filePath, lineNum + 1, line);
                        findings.AddRange(patternFindings);
                    }
                }
                catch { /* 读取失败静默跳过 */ }
            }

            return findings;
        }

        // ── 第1层：敏感文件名检测 ──

        private static readonly HashSet<string> SensitiveFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".env", ".env.local", ".env.production", ".env.staging", ".env.development",
            "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
            "credentials", "credentials.json", "credentials.xml",
            "settings.json", "config.json", "secrets.json", "secrets.yml", "secrets.yaml",
            "wp-config.php", "htpasswd", ".htpasswd",
            "pgpass", ".pgpass", "mylogin.cnf", ".mylogin.cnf",
            ".netrc", "netrc", ".npmrc", ".pypirc", ".gem/credentials",
            "jenkins-credentials.xml", "jenkins.plugins.publish_over_ssh.BapSshPublisherPlugin.xml",
            "kubeconfig", ".kube/config",
            "token", ".token", "access_token", "api_key",
            "database.yml", "database.json", "db.json",
            "key.json", "service-account.json", "service_account.json",
            "oauth.json", "client_secret.json", "client_id.json"
        };

        private static readonly HashSet<string> SensitiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pem", ".key", ".p12", ".pfx", ".jks", ".keystore",
            ".crt", ".cer", ".csr", // 证书本身不敏感，但私钥文件会匹配
        };

        private SecretFinding CheckSensitiveFileName(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var ext = Path.GetExtension(filePath);

            // 检查文件名
            if (SensitiveFileNames.Contains(fileName))
            {
                return new SecretFinding
                {
                    Severity = FindingSeverity.High,
                    Category = FindingCategory.SensitiveFile,
                    FilePath = filePath,
                    Description = string.Format("检测到敏感文件: {0}", fileName),
                    RuleName = "SensitiveFileName"
                };
            }

            // 检查扩展名
            if (SensitiveExtensions.Contains(ext))
            {
                return new SecretFinding
                {
                    Severity = FindingSeverity.Medium,
                    Category = FindingCategory.PrivateKey,
                    FilePath = filePath,
                    Description = string.Format("检测到密钥/证书文件: {0}", ext),
                    RuleName = "SensitiveExtension"
                };
            }

            // 检查路径中的敏感目录
            var lowerPath = filePath.ToLower();
            if (lowerPath.Contains("\\.ssh\\") || lowerPath.Contains("/.ssh/") ||
                lowerPath.Contains("\\.aws\\") || lowerPath.Contains("/.aws/") ||
                lowerPath.Contains("\\.azure\\") || lowerPath.Contains("/.azure/") ||
                lowerPath.Contains("\\.gcloud\\") || lowerPath.Contains("/.gcloud/") ||
                lowerPath.Contains("\\.kube\\") || lowerPath.Contains("/.kube/"))
            {
                return new SecretFinding
                {
                    Severity = FindingSeverity.Medium,
                    Category = FindingCategory.SensitiveFile,
                    FilePath = filePath,
                    Description = "敏感目录中的文件",
                    RuleName = "SensitiveDirectory"
                };
            }

            return null;
        }

        // ── 第2层：香农熵计算 ──

        private List<SecretFinding> CheckHighEntropyStrings(string filePath, int lineNumber, string line)
        {
            var findings = new List<SecretFinding>();

            // 提取可能的密钥字符串（字母数字+特殊字符，长度8-256）
            var matches = Regex.Matches(line, @"[A-Za-z0-9+/=_\-]{8,256}");
            foreach (Match m in matches)
            {
                var value = m.Value;
                if (value.Length < _config.MinEntropyStringLength || value.Length > _config.MaxEntropyStringLength)
                    continue;

                // 跳过常见的非密钥字符串
                if (IsCommonNonSecret(value)) continue;

                double entropy = CalculateShannonEntropy(value);
                if (entropy >= _config.EntropyThreshold)
                {
                    findings.Add(new SecretFinding
                    {
                        Severity = entropy >= 5.0 ? FindingSeverity.High : FindingSeverity.Medium,
                        Category = FindingCategory.HighEntropyString,
                        FilePath = filePath,
                        LineNumber = lineNumber,
                        MatchedContent = value,
                        Entropy = entropy,
                        Description = string.Format("高熵字符串 (熵={0:F2})", entropy),
                        RuleName = "ShannonEntropy"
                    });
                }
            }

            return findings;
        }

        /// <summary>计算香农熵</summary>
        public static double CalculateShannonEntropy(string input)
        {
            if (string.IsNullOrEmpty(input)) return 0;

            var freq = new Dictionary<char, int>();
            foreach (char c in input)
            {
                if (!freq.ContainsKey(c)) freq[c] = 0;
                freq[c]++;
            }

            double entropy = 0;
            int len = input.Length;
            foreach (var kvp in freq)
            {
                double p = (double)kvp.Value / len;
                if (p > 0) entropy -= p * Math.Log(p, 2);
            }

            return entropy;
        }

        private static bool IsCommonNonSecret(string value)
        {
            var lower = value.ToLower();

            // 跳过纯数字
            if (Regex.IsMatch(value, @"^\d+$")) return true;

            // 跳过常见单词/路径
            string[] common = {
                "version", "encoding", "charset", "content-type", "application",
                "javascript", "typescript", "text/html", "utf-8", "iso-8859",
                "localhost", "127.0.0.1", "0.0.0.0", "255.255.255.0",
                "background", "font-family", "font-size", "margin", "padding",
                "border-radius", "text-align", "display", "position",
                "function", "return", "import", "export", "class", "interface",
                "public", "private", "protected", "static", "readonly",
                "abcdef", "123456", "qwerty", "password", "admin", "test",
                "aaaaaa", "bbbbbb", "xxxxxx", "ffffff", "000000"
            };
            foreach (var c in common)
            {
                if (lower.Contains(c)) return true;
            }

            // 跳过 Base64 编码的图片/文件（通常以 data: 开头或很长）
            if (value.Length > 100 && !Regex.IsMatch(value, @"[A-Z][a-z]")) return true;

            return false;
        }

        // ── 第3层：正则模式匹配 ──

        private static readonly PatternRule[] PatternRules = new PatternRule[]
        {
            // AWS
            new PatternRule("AWS Access Key", @"(?<![A-Z0-9])[A-Z0-9]{20}(?![A-Z0-9])", FindingCategory.CloudCredential, FindingSeverity.Critical,
                "AWS Access Key ID 格式"),
            new PatternRule("AWS Secret Key", @"(?i)aws[_\-]?secret[_\-]?access[_\-]?key[\s]*[=:][\s]*['""]?([A-Za-z0-9/+=]{40})", FindingCategory.CloudCredential, FindingSeverity.Critical),

            // GitHub/GitLab
            new PatternRule("GitHub Token", @"gh[pousr]_[A-Za-z0-9_]{36,255}", FindingCategory.Token, FindingSeverity.Critical),
            new PatternRule("GitLab Token", @"glpat-[A-Za-z0-9\-_]{20,}", FindingCategory.Token, FindingSeverity.Critical),

            // JWT
            new PatternRule("JWT Token", @"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", FindingCategory.Token, FindingSeverity.High),

            // Bearer Token
            new PatternRule("Bearer Token", @"(?i)bearer[\s]+[A-Za-z0-9._\-]{20,}", FindingCategory.Token, FindingSeverity.High),

            // API Key 通用模式
            new PatternRule("API Key", @"(?i)(api[_\-]?key|apikey|api[_\-]?secret)[\s]*[=:][\s]*['""]?([A-Za-z0-9_\-]{16,})", FindingCategory.ApiKey, FindingSeverity.High),
            new PatternRule("Secret Key", @"(?i)(secret[_\-]?key|secret[_\-]?token)[\s]*[=:][\s]*['""]?([A-Za-z0-9_\-/+=]{16,})", FindingCategory.ApiKey, FindingSeverity.High),

            // 数据库连接串
            new PatternRule("Database URL", @"(?i)(mysql|postgres|postgresql|mongodb|redis|amqp|mssql)://[^\s]{10,}", FindingCategory.DatabaseCredential, FindingSeverity.Critical),
            new PatternRule("JDBC URL", @"(?i)jdbc:[a-z]+://[^\s]{10,}", FindingCategory.DatabaseCredential, FindingSeverity.High),

            // 密码赋值
            new PatternRule("Password Assignment", @"(?i)(password|passwd|pwd|pass)[\s]*[=:][\s]*['""]([^'""]{4,})['""]", FindingCategory.Password, FindingSeverity.High),
            new PatternRule("Password Unquoted", @"(?i)(password|passwd|pwd)[\s]*[=:][\s]*([^\s'"",;}{)]{4,})", FindingCategory.Password, FindingSeverity.Medium),

            // 私钥
            new PatternRule("RSA Private Key", @"-----BEGIN (RSA )?PRIVATE KEY-----", FindingCategory.PrivateKey, FindingSeverity.Critical),
            new PatternRule("EC Private Key", @"-----BEGIN EC PRIVATE KEY-----", FindingCategory.PrivateKey, FindingSeverity.Critical),
            new PatternRule("OpenSSH Private Key", @"-----BEGIN OPENSSH PRIVATE KEY-----", FindingCategory.PrivateKey, FindingSeverity.Critical),
            new PatternRule("PGP Private Key", @"-----BEGIN PGP PRIVATE KEY BLOCK-----", FindingCategory.PrivateKey, FindingSeverity.Critical),

            // Slack/Discord
            new PatternRule("Slack Token", @"xox[bpors]-[A-Za-z0-9\-]{10,}", FindingCategory.Token, FindingSeverity.Critical),
            new PatternRule("Discord Token", @"[MN][A-Za-z\d]{23,}\.[\w-]{6}\.[\w-]{27,}", FindingCategory.Token, FindingSeverity.High),

            // Google
            new PatternRule("Google API Key", @"AIza[0-9A-Za-z_\-]{35}", FindingCategory.ApiKey, FindingSeverity.Critical),
            new PatternRule("Google OAuth", @"[0-9]+-[0-9A-Za-z_]{32}\.apps\.googleusercontent\.com", FindingCategory.Token, FindingSeverity.High),

            // Azure
            new PatternRule("Azure Connection String", @"(?i)DefaultEndpointsProtocol=https?;AccountName=", FindingCategory.CloudCredential, FindingSeverity.Critical),

            // OpenAI/Anthropic
            new PatternRule("OpenAI API Key", @"sk-[A-Za-z0-9]{20,}", FindingCategory.ApiKey, FindingSeverity.Critical),
            new PatternRule("Anthropic API Key", @"sk-ant-[A-Za-z0-9\-_]{20,}", FindingCategory.ApiKey, FindingSeverity.Critical),

            // 通用 Base64 密钥（赋值语句中）
            new PatternRule("Base64 Secret", @"(?i)(key|secret|token|password)[\s]*[=:][\s]*['""]?[A-Za-z0-9+/]{32,}={0,2}['""]?", FindingCategory.HighEntropyString, FindingSeverity.Medium),

            // SSH 连接串
            new PatternRule("SSH URL", @"ssh://[^\s]+@[^\s]+", FindingCategory.ConfigFile, FindingSeverity.Low),

            // 内网 IP（可能暴露内网结构）
            new PatternRule("Internal IP", @"(?<![.\d])(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])|192\.168)\.\d{1,3}\.\d{1,3}(?![.\d])", FindingCategory.ConfigFile, FindingSeverity.Low),
        };

        private List<SecretFinding> CheckPatterns(string filePath, int lineNumber, string line)
        {
            var findings = new List<SecretFinding>();

            foreach (var rule in PatternRules)
            {
                var matches = Regex.Matches(line, rule.Pattern);
                foreach (Match m in matches)
                {
                    var value = m.Value;
                    // 过滤掉太短的匹配
                    if (value.Length < 8) continue;

                    findings.Add(new SecretFinding
                    {
                        Severity = rule.Severity,
                        Category = rule.Category,
                        FilePath = filePath,
                        LineNumber = lineNumber,
                        MatchedContent = value,
                        Description = rule.Description,
                        RuleName = rule.Name
                    });
                }
            }

            return findings;
        }

        // ── 辅助方法 ──

        private List<string> CollectFiles()
        {
            var files = new List<string>();
            foreach (var path in _config.ScanPaths)
            {
                if (!Directory.Exists(path)) continue;
                try
                {
                    CollectFilesRecursive(path, files, 0);
                }
                catch { /* 权限不足等 */ }
            }
            return files;
        }

        private void CollectFilesRecursive(string dir, List<string> files, int depth)
        {
            if (depth > 15) return; // 防止过深递归

            // 检查排除路径
            var dirName = Path.GetFileName(dir);
            if (_config.ExcludePaths.Exists(e => dir.Contains(e) || string.Equals(dirName, e, StringComparison.OrdinalIgnoreCase)))
                return;

            try
            {
                // 收集当前目录的文件
                foreach (var file in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(file);
                    if (_config.ExcludeExtensions.Exists(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    files.Add(file);
                }

                // 递归子目录
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    CollectFilesRecursive(subDir, files, depth + 1);
                }
            }
            catch { /* 权限不足 */ }
        }

        private static bool IsTextFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLower();
            string[] textExts = {
                ".txt", ".log", ".conf", ".cfg", ".ini", ".xml", ".json", ".yaml", ".yml",
                ".sh", ".bash", ".bat", ".cmd", ".ps1", ".py", ".rb", ".pl", ".js", ".ts",
                ".java", ".c", ".cpp", ".h", ".cs", ".go", ".rs", ".php", ".html", ".css",
                ".sql", ".properties", ".env", ".toml", ".md", ".csv", ".service", ".nginx",
                ".htaccess", ".gitignore", ".dockerfile", ".makefile", ".config"
            };
            return Array.Exists(textExts, e => e == ext) || string.IsNullOrEmpty(ext);
        }

        /// <summary>添加到白名单</summary>
        public void AddToWhitelist(string filePath, string content)
        {
            var key = filePath + ":" + content;
            if (!_whitelist.Contains(key)) _whitelist.Add(key);
            SaveWhitelist();
        }

        private void LoadWhitelist()
        {
            var path = GetWhitelistPath();
            if (File.Exists(path))
            {
                _whitelist.AddRange(File.ReadAllLines(path));
            }
        }

        private void SaveWhitelist()
        {
            var path = GetWhitelistPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, _whitelist.ToArray());
        }

        private string GetWhitelistPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config", "secret-scan-whitelist.txt");
        }

        public void Dispose()
        {
            StopScan();
            _cts?.Dispose();
        }
    }

    /// <summary>模式匹配规则</summary>
    internal class PatternRule
    {
        public string Name { get; set; }
        public string Pattern { get; set; }
        public FindingCategory Category { get; set; }
        public FindingSeverity Severity { get; set; }
        public string Description { get; set; }

        public PatternRule(string name, string pattern, FindingCategory category, FindingSeverity severity, string description = null)
        {
            Name = name;
            Pattern = pattern;
            Category = category;
            Severity = severity;
            Description = description ?? name;
        }
    }
}
