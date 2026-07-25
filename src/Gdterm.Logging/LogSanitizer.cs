using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gdterm.Logging
{
    /// <summary>
    /// 日志脱敏器——识别并替换敏感信息（密码、token、密钥、CLI 位置参数等）
    /// </summary>
    public class LogSanitizer
    {
        private readonly string _replacement;

        private static readonly List<SensitivePattern> Patterns = new List<SensitivePattern>
        {
            // ---- CLI 位置/短选项（优先于 key=value，避免只匹配半截）----
            // mysql -pSECRET / mysql --password=SECRET
            new SensitivePattern(@"\b(mysql|mysqldump|mysqladmin)\b([^\n]*?\s)(-p)([^\s\-][^\s]*)", "mysql_p", preserveGroups: true),
            new SensitivePattern(@"\b--password(?:=|\s+)\S+", "cli_password_long"),
            // sshpass -p secret
            new SensitivePattern(@"\bsshpass\b([^\n]*?\s)(-p)(\s*)(\S+)", "sshpass_p", preserveGroups: true),
            // curl -u user:pass / --user user:pass
            new SensitivePattern(@"\b(-u|--user)(=|\s+)\S+", "curl_user"),
            // redis-cli -a password
            new SensitivePattern(@"\bredis-cli\b([^\n]*?\s)(-a)(\s+)(\S+)", "redis_a", preserveGroups: true),
            // psql PGPASSWORD=x / postgresql://user:pass@
            new SensitivePattern(@"\bPGPASSWORD=\S+", "pgpassword"),
            new SensitivePattern(@"\b(PG|MYSQL|MONGO|REDIS|FTP|HTTP|HTTPS)_?PASSWORD=\S+", "env_password"),
            // openssl pass:xxx / passin pass:xxx
            new SensitivePattern(@"\bpass(?:in|out)?:?\s*\S+", "openssl_pass"),
            // docker login -p / --password-stdin already ok; -p SECRET
            new SensitivePattern(@"\bdocker\s+login\b([^\n]*?\s)(-p|--password)(\s+)(\S+)", "docker_login_p", preserveGroups: true),
            // kubectl / helm token flags
            new SensitivePattern(@"\b--token(?:=|\s+)\S+", "cli_token"),
            new SensitivePattern(@"\b--api-key(?:=|\s+)\S+", "cli_api_key"),
            new SensitivePattern(@"\b--secret(?:=|\s+)\S+", "cli_secret"),
            // wget --password=
            new SensitivePattern(@"\b--(http-)?password(?:=|\s+)\S+", "wget_password"),
            // general -p/--password after common tools already covered; bare echo secrets less reliable

            // SSH 密码 key=value
            new SensitivePattern(@"password\s*[=:]\s*\S+", "password"),
            new SensitivePattern(@"passwd\s*[=:]\s*\S+", "passwd"),

            // Token / API Key
            new SensitivePattern(@"token\s*[=:]\s*\S+", "token"),
            new SensitivePattern(@"api[_-]?key\s*[=:]\s*\S+", "api_key"),
            new SensitivePattern(@"apikey\s*[=:]\s*\S+", "apikey"),
            new SensitivePattern(@"secret\s*[=:]\s*\S+", "secret"),
            new SensitivePattern(@"access[_-]?key\s*[=:]\s*\S+", "access_key"),

            // AWS
            new SensitivePattern(@"AKIA[0-9A-Z]{16}", "aws_key"),
            new SensitivePattern(@"aws[_-]?secret[_-]?access[_-]?key\s*[=:]\s*\S+", "aws_secret"),

            // SSH 私钥
            new SensitivePattern(@"-----BEGIN\s+(RSA|DSA|EC|OPENSSH)\s+PRIVATE\s+KEY-----", "ssh_key"),
            new SensitivePattern(@"ssh-rsa\s+[A-Za-z0-9+/=]+", "ssh_pubkey"),

            // 数据库连接串 user:pass@
            new SensitivePattern(@"(mysql|postgres|postgresql|mongodb|redis|amqp|ftp|sftp|http|https)://[^\s/@:]+:[^\s/@]+@", "db_connection"),

            // Bearer / Basic / JWT
            new SensitivePattern(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", "bearer_token"),
            new SensitivePattern(@"Basic\s+[A-Za-z0-9+/]+=*", "basic_auth"),
            new SensitivePattern(@"eyJ[A-Za-z0-9\-._~+/]+=*\.[A-Za-z0-9\-._~+/]+=*\.[A-Za-z0-9\-._~+/]+=*", "jwt"),

            // 通用 password_kv
            new SensitivePattern(@"\b(pwd|password|passwd|pass)\s*=\s*[^\s&;]+", "password_kv"),
        };

        public LogSanitizer(string replacement = "***")
        {
            _replacement = replacement ?? "***";
        }

        /// <summary>
        /// 对文本进行脱敏处理
        /// </summary>
        public string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            foreach (var pattern in Patterns)
            {
                text = pattern.Regex.Replace(text, m => ReplaceMatch(m, pattern));
            }

            return text;
        }

        private string ReplaceMatch(Match m, SensitivePattern pattern)
        {
            var match = m.Value;

            // CLI 分组：保留工具与开关，只打码密码值
            if (pattern.PreserveGroups && m.Groups.Count >= 5)
            {
                // groups: 1=tool tail, 2=flag, 3=space?, 4=secret — 不同模式略有差异
                // 统一：整段匹配里把最后一个非空 group 当 secret
                for (int i = m.Groups.Count - 1; i >= 1; i--)
                {
                    var g = m.Groups[i];
                    if (g.Success && !string.IsNullOrWhiteSpace(g.Value) &&
                        g.Value != "-p" && g.Value != "-a" &&
                        !g.Value.StartsWith("--", StringComparison.Ordinal) &&
                        g.Value.Trim().Length > 0 &&
                        !char.IsWhiteSpace(g.Value[0]))
                    {
                        // 若 group 看起来像 secret（不是 flag）
                        if (g.Value.StartsWith("-", StringComparison.Ordinal)) continue;
                        return match.Substring(0, g.Index - m.Index) + _replacement +
                               match.Substring(g.Index - m.Index + g.Length);
                    }
                }
            }

            var eqIndex = match.IndexOfAny(new[] { '=', ':' });
            if (eqIndex > 0 && eqIndex < match.Length - 1)
            {
                // 避免 "http://" 被当成 key:value — 要求 = 或 : 后不是 //
                if (eqIndex + 1 < match.Length - 1 &&
                    match[eqIndex] == ':' && match[eqIndex + 1] == '/' && match[eqIndex + 2] == '/')
                {
                    return _replacement + "[" + pattern.Name + "]";
                }
                return match.Substring(0, eqIndex + 1) + _replacement;
            }

            // -pSECRET 粘连：保留 -p
            if (match.StartsWith("-p", StringComparison.OrdinalIgnoreCase) && match.Length > 2 &&
                match[2] != ' ' && match[2] != '-')
            {
                return "-p" + _replacement;
            }

            return _replacement + "[" + pattern.Name + "]";
        }

        /// <summary>
        /// 检测文本是否包含敏感信息
        /// </summary>
        public bool ContainsSensitive(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (var pattern in Patterns)
            {
                if (pattern.Regex.IsMatch(text))
                    return true;
            }

            return false;
        }

        private class SensitivePattern
        {
            public Regex Regex { get; }
            public string Name { get; }
            public bool PreserveGroups { get; }

            public SensitivePattern(string pattern, string name, bool preserveGroups = false)
            {
                Regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                Name = name;
                PreserveGroups = preserveGroups;
            }
        }
    }
}
