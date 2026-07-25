using System;
using System.Collections.Generic;

namespace Gdterm.Security
{
    /// <summary>
    /// 本地敏感信息扫描配置
    /// </summary>
    public class SecretScanConfig
    {
        /// <summary>扫描路径列表（默认扫描用户目录）</summary>
        public List<string> ScanPaths { get; set; }

        /// <summary>排除路径列表</summary>
        public List<string> ExcludePaths { get; set; }

        /// <summary>排除文件扩展名</summary>
        public List<string> ExcludeExtensions { get; set; }

        /// <summary>最大文件大小（字节，超过跳过）</summary>
        public long MaxFileSizeBytes { get; set; }

        /// <summary>熵阈值（超过此值报告为高熵字符串）</summary>
        public double EntropyThreshold { get; set; }

        /// <summary>最小高熵字符串长度</summary>
        public int MinEntropyStringLength { get; set; }

        /// <summary>最大高熵字符串长度</summary>
        public int MaxEntropyStringLength { get; set; }

        /// <summary>是否启用后台扫描</summary>
        public bool EnableBackgroundScan { get; set; }

        /// <summary>后台扫描间隔（分钟）</summary>
        public int BackgroundScanIntervalMinutes { get; set; }

        /// <summary>扫描优先级（0=空闲, 1=低, 2=正常）</summary>
        public int ScanPriority { get; set; }

        public SecretScanConfig()
        {
            ScanPaths = new List<string>();
            ExcludePaths = new List<string>
            {
                "node_modules", ".git", "bin", "obj", "Debug", "Release",
                ".vs", ".idea", "packages", "vendor", "__pycache__",
                ".nuget", "AppData\\Local\\Temp", "Windows", "Program Files"
            };
            ExcludeExtensions = new List<string>
            {
                ".exe", ".dll", ".so", ".dylib", ".bin", ".dat", ".db",
                ".sqlite", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico",
                ".mp3", ".mp4", ".avi", ".mov", ".zip", ".tar", ".gz",
                ".7z", ".rar", ".pdf", ".ttf", ".otf", ".woff", ".woff2", ".eot"
            };
            MaxFileSizeBytes = 10 * 1024 * 1024; // 10MB
            EntropyThreshold = 4.5;
            MinEntropyStringLength = 8;
            MaxEntropyStringLength = 256;
            // 默认关闭后台扫描，避免低配机器磁盘/内存抖动；由用户手动触发
            EnableBackgroundScan = false;
            BackgroundScanIntervalMinutes = 60;
            ScanPriority = 0; // 空闲优先级
        }

        public static SecretScanConfig GetDefault()
        {
            var config = new SecretScanConfig();
            // 默认扫描常见敏感路径
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                config.ScanPaths.Add(userProfile);
            }
            return config;
        }
    }

    /// <summary>
    /// 扫描结果条目
    /// </summary>
    public class SecretFinding
    {
        public string Id { get; set; }
        public FindingSeverity Severity { get; set; }
        public FindingCategory Category { get; set; }
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public string MatchedContent { get; set; }
        public string Description { get; set; }
        public string RuleName { get; set; }
        public double Entropy { get; set; }
        public DateTime FoundAt { get; set; }
        public bool IsWhitelisted { get; set; }

        public SecretFinding()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            FoundAt = DateTime.Now;
        }

        /// <summary>获取脱敏后的内容（仅显示前4后4）</summary>
        public string GetRedactedContent()
        {
            if (string.IsNullOrEmpty(MatchedContent) || MatchedContent.Length <= 12)
                return "****";
            return MatchedContent.Substring(0, 4) + "****" + MatchedContent.Substring(MatchedContent.Length - 4);
        }
    }

    /// <summary>严重程度</summary>
    public enum FindingSeverity
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    /// <summary>发现类别</summary>
    public enum FindingCategory
    {
        SensitiveFile,      // 敏感文件（.env, id_rsa 等）
        HighEntropyString,  // 高熵字符串（可能是密钥）
        ApiKey,             // API 密钥
        Password,           // 明文密码
        PrivateKey,         // 私钥
        DatabaseCredential, // 数据库凭据
        Token,              // 令牌（JWT, Bearer 等）
        CloudCredential,    // 云服务凭据
        ConfigFile          // 配置文件中的敏感信息
    }

    /// <summary>
    /// 扫描报告
    /// </summary>
    public class SecretScanReport
    {
        public DateTime ScanStarted { get; set; }
        public DateTime ScanCompleted { get; set; }
        public TimeSpan Duration { get { return ScanCompleted - ScanStarted; } }
        public int FilesScanned { get; set; }
        public int FilesSkipped { get; set; }
        public List<SecretFinding> Findings { get; set; }
        public int CriticalCount { get { return Findings.FindAll(f => f.Severity == FindingSeverity.Critical).Count; } }
        public int HighCount { get { return Findings.FindAll(f => f.Severity == FindingSeverity.High).Count; } }
        public int MediumCount { get { return Findings.FindAll(f => f.Severity == FindingSeverity.Medium).Count; } }
        public int LowCount { get { return Findings.FindAll(f => f.Severity == FindingSeverity.Low).Count; } }
        public int TotalFindings { get { return Findings.Count; } }

        public SecretScanReport()
        {
            Findings = new List<SecretFinding>();
            ScanStarted = DateTime.Now;
        }

        /// <summary>安全评分 (0-100, 100=最安全)</summary>
        public int SecurityScore
        {
            get
            {
                if (Findings.Count == 0) return 100;
                int penalty = CriticalCount * 20 + HighCount * 10 + MediumCount * 5 + LowCount * 2;
                return Math.Max(0, 100 - penalty);
            }
        }
    }
}
