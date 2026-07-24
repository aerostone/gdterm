using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gdterm.Logging
{
    /// <summary>
    /// 日志脱敏器——识别并替换敏感信息（密码、token、密钥等）
    /// </summary>
    public class LogSanitizer
    {
        private readonly string _replacement;

        // 敏感模式列表
        private static readonly List<SensitivePattern> Patterns = new List<SensitivePattern>
        {
            // SSH 密码
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

            // 数据库连接串
            new SensitivePattern(@"(mysql|postgres|mongodb|redis)://\S+@", "db_connection"),

            // Bearer Token
            new SensitivePattern(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", "bearer_token"),

            // Basic Auth
            new SensitivePattern(@"Basic\s+[A-Za-z0-9+/]+=*", "basic_auth"),

            // JWT
            new SensitivePattern(@"eyJ[A-Za-z0-9\-._~+/]+=*\.[A-Za-z0-9\-._~+/]+=*\.[A-Za-z0-9\-._~+/]+=*", "jwt"),

            // 通用密码模式（key=value 中的 password/passwd/pwd）
            new SensitivePattern(@"\b(pwd|password|passwd|pass)\s*=\s*[^\s&;]+", "password_kv"),
        };

        public LogSanitizer(string replacement = "***")
        {
            _replacement = replacement;
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
                text = pattern.Regex.Replace(text, m =>
                {
                    // 保留键名，替换值
                    var match = m.Value;
                    var eqIndex = match.IndexOfAny(new[] { '=', ':' });
                    if (eqIndex > 0 && eqIndex < match.Length - 1)
                    {
                        return match.Substring(0, eqIndex + 1) + _replacement;
                    }
                    return _replacement + $"[{pattern.Name}]";
                });
            }

            return text;
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

            public SensitivePattern(string pattern, string name)
            {
                Regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                Name = name;
            }
        }
    }
}
