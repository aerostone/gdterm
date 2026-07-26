using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

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

            var ext = Path.GetExtension(filePath).ToLower();
            var fileSize = new FileInfo(filePath).Length;

            if (fileSize > _config.MaxFileSizeBytes) return findings;

            // CSV 文件 — 直接按行读取
            if (ext == ".csv")
            {
                try
                {
                    var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        findings.AddRange(CheckHighEntropyStrings(filePath, i + 1, lines[i]));
                        findings.AddRange(CheckPatterns(filePath, i + 1, lines[i]));
                    }
                    // CSV 密码表特征：如果某列全是高熵短字符串，标记为疑似密码表
                    findings.AddRange(CheckPasswordSpreadsheet(filePath, lines));
                }
                catch { }
                return findings;
            }

            // Excel .xlsx — 解压解析 XML
            if (ext == ".xlsx")
            {
                try
                {
                    var cellTexts = ExtractXlsxText(filePath);
                    findings.AddRange(ScanExtractedTexts(filePath, cellTexts));
                    findings.AddRange(CheckPasswordSpreadsheetFromCells(filePath, cellTexts));
                }
                catch { }
                return findings;
            }

            // Excel .xls (旧格式) — 无法无依赖解析，用文件名+大小启发式
            if (ext == ".xls")
            {
                findings.Add(new SecretFinding
                {
                    Severity = FindingSeverity.Medium,
                    Category = FindingCategory.SensitiveFile,
                    FilePath = filePath,
                    Description = string.Format("旧格式 Excel 文件（{0}KB，建议手动检查是否含密码）", fileSize / 1024),
                    RuleName = "XlsPasswordCandidate"
                });
                return findings;
            }

            // Word .docx — 解压解析 XML
            if (ext == ".docx")
            {
                try
                {
                    var texts = ExtractDocxText(filePath);
                    findings.AddRange(ScanExtractedTexts(filePath, texts));
                }
                catch { }
                return findings;
            }

            // 纯文本文件
            if (IsTextFile(filePath))
            {
                try
                {
                    var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        findings.AddRange(CheckHighEntropyStrings(filePath, i + 1, lines[i]));
                        findings.AddRange(CheckPatterns(filePath, i + 1, lines[i]));
                    }
                }
                catch { }
            }

            return findings;
        }

        // ── 第1层：敏感文件名检测（文件名优先策略） ──

        /// <summary>精确文件名匹配（不含扩展名）</summary>
        private static readonly HashSet<string> SensitiveFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 环境变量/配置
            ".env", ".env.local", ".env.production", ".env.staging", ".env.development",
            "credentials", "credentials.json", "credentials.xml", "credentials.csv",
            "settings.json", "config.json", "secrets.json", "secrets.yml", "secrets.yaml",
            "wp-config.php", "htpasswd", ".htpasswd",
            "pgpass", ".pgpass", "mylogin.cnf", ".mylogin.cnf",
            ".netrc", "netrc", ".npmrc", ".pypirc", ".gem/credentials",
            "jenkins-credentials.xml", "kubeconfig", ".kube/config",
            "token", ".token", "access_token", "api_key",
            "database.yml", "database.json", "db.json",
            "key.json", "service-account.json", "service_account.json",
            "oauth.json", "client_secret.json", "client_id.json",
            // SSH/密钥
            "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
            "known_hosts", "authorized_keys",
            // Windows
            "unattend.xml", "sysprep.xml", "Autounattend.xml"
        };

        /// <summary>文件名关键词——只要文件名（不含扩展名）包含这些词就告警</summary>
        /// <remarks>
        /// 策略：只保留最小关键词（2-3字），已包含更短关键词的词组自动覆盖。
        /// 例如 "密码" 一个词就能覆盖：密码表/密码本/密码单/密码清单/密码列表/
        /// 服务器密码/数据库密码/交换机密码/路由器密码/系统密码/运维密码/
        /// 密码备份/密码记录/密码台账/密码管理/密码导出/密码存储 等30+个词。
        /// </remarks>
        private static readonly string[] SensitiveKeywords = new string[]
        {
            // ── 核心：2字关键词（最小命中集） ──
            "密码",   // 覆盖所有 X密码、密码X 组合
            "口令",   // 口令表、口令本、系统口令
            "账号",   // 账号密码、系统账号、服务器账号
            "账户",   // 账户密码、系统账户、管理账户
            "凭据",   // 凭据信息、凭据管理
            "秘钥",   // 秘钥文件、秘钥管理
            "密钥",   // 密钥文件、密钥管理、API密钥
            "token",  // token文件、access_token
            "secret", // secret文件、client_secret
            // ── 补充：3字关键词（2字无法覆盖的场景） ──
            "密码本", // 虽含"密码"但密码本是专有概念，放在这里双重保险
            "资产",   // 资产清单、资产信息、资产台账
            "运维",   // 运维手册、运维清单（运维环境高概率含密码）
            // ── 英文 ──
            "password",
            "passwd",
            "credential",
            "login",
            "passlist",
        };

        /// <summary>敏感扩展名（私钥/证书）</summary>
        private static readonly HashSet<string> SensitiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pem", ".key", ".p12", ".pfx", ".jks", ".keystore",
            ".p7b", ".p7c", ".der", ".csr"
        };

        /// <summary>敏感文件名+扩展名组合（文件名本身不敏感，但组合后敏感）</summary>
        private static readonly string[] SensitiveNamePatterns = new string[]
        {
            @"密码.*\.(xlsx?|csv|txt|docx?|md|json)$",
            @"(password|passwd|pwd|secret|credential).*\.(xlsx?|csv|txt|docx?|md|json)$",
            @"(账号|账户|登录|口令).*\.(xlsx?|csv|txt|docx?|md)$",
            @"(server|host|device|switch|router|网络|服务器|交换机|路由器).*(密码|password|pwd|账号).*\.(xlsx?|csv|txt|docx?|md)$",
            @"(密码|password).*(备份|backup|导出|export|dump).*\.(xlsx?|csv|txt|gz|zip|7z)$",
            @"(database|数据库|db).*(密码|password|credential).*\.(xlsx?|csv|txt|json|yml)$",
            @"(密码|password|secret).*\d{4}[-_]?\d{2}.*\.(xlsx?|csv|txt)$",  // 带日期的密码文件
        };

        private static Regex[] _sensitiveNameRegex;

        private static Regex[] GetSensitiveNameRegex()
        {
            if (_sensitiveNameRegex == null)
            {
                _sensitiveNameRegex = new Regex[SensitiveNamePatterns.Length];
                for (int i = 0; i < SensitiveNamePatterns.Length; i++)
                    _sensitiveNameRegex[i] = new Regex(SensitiveNamePatterns[i], RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            return _sensitiveNameRegex;
        }

        private SecretFinding CheckSensitiveFileName(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath);
            var lowerName = fileNameNoExt.ToLower();

            // 1. 精确文件名匹配
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

            // 2. 文件名关键词匹配（只要文件名包含密码相关关键词）
            foreach (var keyword in SensitiveKeywords)
            {
                if (lowerName.Contains(keyword.ToLower()))
                {
                    // 密码关键词 + 文档扩展名 = 高风险
                    var docExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ".xlsx", ".xls", ".csv", ".docx", ".doc", ".txt", ".md", ".json", ".yaml", ".yml"
                    };
                    bool isDoc = docExts.Contains(ext);

                    return new SecretFinding
                    {
                        Severity = isDoc ? FindingSeverity.Critical : FindingSeverity.Medium,
                        Category = FindingCategory.Password,
                        FilePath = filePath,
                        Description = isDoc
                            ? string.Format("疑似密码本：文件名含 \"{0}\"，类型 {1}", keyword, ext)
                            : string.Format("敏感关键词：文件名含 \"{0}\"", keyword),
                        RuleName = "FileNameKeyword"
                    };
                }
            }

            // 3. 正则模式匹配（组合模式，如 "服务器密码_2024.xlsx"）
            foreach (var regex in GetSensitiveNameRegex())
            {
                if (regex.IsMatch(fileName))
                {
                    return new SecretFinding
                    {
                        Severity = FindingSeverity.Critical,
                        Category = FindingCategory.Password,
                        FilePath = filePath,
                        Description = string.Format("疑似密码本（模式匹配）: {0}", fileName),
                        RuleName = "FileNamePattern"
                    };
                }
            }

            // 4. 敏感扩展名（私钥/证书）
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

            // 5. 敏感目录检测
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
                ".sql", ".properties", ".env", ".toml", ".md", ".service", ".nginx",
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

        // ── Office 文档解析（无需外部依赖） ──

        /// <summary>从 .xlsx 提取所有单元格文本（解压 zip → 解析 sheet*.xml）</summary>
        private static List<string> ExtractXlsxText(string filePath)
        {
            var texts = new List<string>();
            using (var zip = ZipFile.OpenRead(filePath))
            {
                // 解析 sharedStrings.xml（共享字符串表）
                var sharedStringsEntry = zip.GetEntry("xl/sharedStrings.xml");
                var sharedStrings = new List<string>();
                if (sharedStringsEntry != null)
                {
                    using (var stream = sharedStringsEntry.Open())
                    {
                        var doc = XDocument.Load(stream);
                        var ns = doc.Root.GetDefaultNamespace();
                        foreach (var si in doc.Descendants(ns + "si"))
                        {
                            // <si><t>text</t></si> 或 <si><r><t>text</t></r>...</si>
                            var tElements = si.Descendants(ns + "t");
                            var sb = new StringBuilder();
                            foreach (var t in tElements) sb.Append(t.Value);
                            sharedStrings.Add(sb.ToString());
                        }
                    }
                }

                // 解析每个 sheet
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith("xl/worksheets/sheet") || !entry.FullName.EndsWith(".xml"))
                        continue;

                    using (var stream = entry.Open())
                    {
                        var doc = XDocument.Load(stream);
                        var ns = doc.Root.GetDefaultNamespace();

                        foreach (var row in doc.Descendants(ns + "row"))
                        {
                            foreach (var cell in row.Descendants(ns + "c"))
                            {
                                var value = cell.Element(ns + "v");
                                if (value == null || string.IsNullOrEmpty(value.Value)) continue;

                                var cellRef = cell.Attribute("t");
                                if (cellRef != null && cellRef.Value == "s")
                                {
                                    // 共享字符串引用
                                    int idx;
                                    if (int.TryParse(value.Value, out idx) && idx < sharedStrings.Count)
                                        texts.Add(sharedStrings[idx]);
                                }
                                else
                                {
                                    // 直接值（数字、布尔、日期等）
                                    texts.Add(value.Value);
                                }
                            }
                        }
                    }
                }
            }
            return texts;
        }

        /// <summary>从 .docx 提取所有段落文本（解压 zip → 解析 document.xml）</summary>
        private static List<string> ExtractDocxText(string filePath)
        {
            var texts = new List<string>();
            using (var zip = ZipFile.OpenRead(filePath))
            {
                var docEntry = zip.GetEntry("word/document.xml");
                if (docEntry == null) return texts;

                using (var stream = docEntry.Open())
                {
                    var doc = XDocument.Load(stream);
                    var ns = doc.Root.GetDefaultNamespace();

                    // 提取所有 <w:t> 文本节点
                    foreach (var t in doc.Descendants(ns + "t"))
                    {
                        if (!string.IsNullOrEmpty(t.Value))
                            texts.Add(t.Value);
                    }
                }

                // 也解析 .docx 中的表格
                using (var stream = docEntry.Open())
                {
                    var doc = XDocument.Load(stream);
                    var ns = doc.Root.GetDefaultNamespace();

                    foreach (var tc in doc.Descendants(ns + "tc"))
                    {
                        var sb = new StringBuilder();
                        foreach (var t in tc.Descendants(ns + "t")) sb.Append(t.Value);
                        if (sb.Length > 0) texts.Add(sb.ToString());
                    }
                }
            }
            return texts;
        }

        /// <summary>扫描提取出的文本列表（用于 Excel/Word）</summary>
        private List<SecretFinding> ScanExtractedTexts(string filePath, List<string> texts)
        {
            var findings = new List<SecretFinding>();
            for (int i = 0; i < texts.Count; i++)
            {
                var text = texts[i];
                if (string.IsNullOrEmpty(text) || text.Length < 4) continue;

                // 高熵检测
                double entropy = CalculateShannonEntropy(text);
                if (text.Length >= _config.MinEntropyStringLength &&
                    text.Length <= _config.MaxEntropyStringLength &&
                    !IsCommonNonSecret(text) &&
                    entropy >= _config.EntropyThreshold)
                {
                    findings.Add(new SecretFinding
                    {
                        Severity = entropy >= 5.0 ? FindingSeverity.High : FindingSeverity.Medium,
                        Category = FindingCategory.HighEntropyString,
                        FilePath = filePath,
                        LineNumber = i + 1,
                        MatchedContent = text,
                        Entropy = entropy,
                        Description = string.Format("Office 文档中的高熵字符串 (熵={0:F2})", entropy),
                        RuleName = "OfficeShannonEntropy"
                    });
                }

                // 正则模式检测
                findings.AddRange(CheckPatterns(filePath, i + 1, text));
            }
            return findings;
        }

        /// <summary>CSV 密码表特征检测——如果某列全是高熵短字符串，标记为疑似密码表</summary>
        private List<SecretFinding> CheckPasswordSpreadsheet(string filePath, string[] lines)
        {
            var findings = new List<SecretFinding>();
            if (lines.Length < 2) return findings;

            // 解析 CSV 列
            var headers = ParseCsvLine(lines[0]);
            int colCount = headers.Count;
            if (colCount < 2) return findings;

            // 检查每列
            for (int col = 0; col < colCount; col++)
            {
                var header = col < headers.Count ? headers[col].ToLower() : "";
                bool isPasswordCol = header.Contains("密码") || header.Contains("password") ||
                                     header.Contains("passwd") || header.Contains("pwd") ||
                                     header.Contains("pass") || header.Contains("secret") ||
                                     header.Contains("key") || header.Contains("token");

                int highEntropyCount = 0;
                int totalRows = 0;

                for (int row = 1; row < lines.Length && row < 500; row++) // 最多检查500行
                {
                    var cols = ParseCsvLine(lines[row]);
                    if (col >= cols.Count) continue;
                    var cell = cols[col];
                    if (string.IsNullOrEmpty(cell) || cell.Length < 4) continue;

                    totalRows++;
                    double entropy = CalculateShannonEntropy(cell);
                    if (entropy >= 3.5 && cell.Length >= 6) // 比正文阈值稍低，因为密码可能不长
                        highEntropyCount++;
                }

                // 如果列名含密码关键词，或该列超过 60% 是高熵字符串
                if (isPasswordCol && totalRows > 0)
                {
                    findings.Add(new SecretFinding
                    {
                        Severity = FindingSeverity.Critical,
                        Category = FindingCategory.Password,
                        FilePath = filePath,
                        Description = string.Format("疑似密码表：列 '{0}' 标题含密码关键词（{1}行数据）", header, totalRows),
                        RuleName = "PasswordColumnHeader"
                    });
                }
                else if (highEntropyCount > 3 && (double)highEntropyCount / totalRows > 0.6)
                {
                    findings.Add(new SecretFinding
                    {
                        Severity = FindingSeverity.High,
                        Category = FindingCategory.Password,
                        FilePath = filePath,
                        Description = string.Format("疑似密码表：列 {0} 有 {1}/{2} 行为高熵字符串", col + 1, highEntropyCount, totalRows),
                        RuleName = "PasswordColumnEntropy"
                    });
                }
            }

            return findings;
        }

        /// <summary>Excel 密码表特征检测（基于单元格列表）</summary>
        private List<SecretFinding> CheckPasswordSpreadsheetFromCells(string filePath, List<string> cells)
        {
            // 统计高熵短字符串比例
            int highEntropy = 0;
            int total = 0;
            foreach (var cell in cells)
            {
                if (string.IsNullOrEmpty(cell) || cell.Length < 4 || cell.Length > 64) continue;
                total++;
                double entropy = CalculateShannonEntropy(cell);
                if (entropy >= 3.5 && cell.Length >= 6) highEntropy++;
            }

            if (total > 5 && (double)highEntropy / total > 0.3)
            {
                return new List<SecretFinding>
                {
                    new SecretFinding
                    {
                        Severity = FindingSeverity.High,
                        Category = FindingCategory.Password,
                        FilePath = filePath,
                        Description = string.Format("疑似密码表：{0} 个单元格中有 {1} 个高熵字符串 ({2:F0}%)",
                            total, highEntropy, (double)highEntropy / total * 100),
                        RuleName = "ExcelPasswordSheet"
                    }
                };
            }
            return new List<SecretFinding>();
        }

        /// <summary>简单 CSV 行解析（支持引号内逗号）</summary>
        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(line)) return result;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            result.Add(sb.ToString().Trim());
            return result;
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
