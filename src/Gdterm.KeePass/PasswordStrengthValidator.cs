using System;
using System.Collections.Generic;
using System.Linq;

namespace Gdterm.KeePass
{
    /// <summary>
    /// 密码强度校验器——最小12字符、含大写+小写+数字+特殊字符、不含常见弱密码
    /// </summary>
    internal class PasswordStrengthValidator
    {
        private static readonly HashSet<string> CommonWeakPasswords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "password1", "password123", "123456", "12345678", "123456789",
            "1234567890", "qwerty", "abc123", "monkey", "master", "dragon", "login",
            "princess", "football", "shadow", "sunshine", "trustno1", "iloveyou",
            "batman", "access", "hello", "charlie", "donald", "password1!", "admin",
            "letmein", "welcome", "p@ssw0rd", "passw0rd", "test", "guest", "root"
        };

        /// <summary>
        /// 校验密码强度，返回违反的规则列表（空列表表示通过）
        /// </summary>
        public IList<string> Validate(string password)
        {
            var violations = new List<string>();

            if (string.IsNullOrEmpty(password))
            {
                violations.Add("密码不能为空");
                return violations;
            }

            if (password.Length < 12)
                violations.Add("密码长度不得少于12个字符");

            if (!password.Any(char.IsUpper))
                violations.Add("密码必须包含至少一个大写字母");

            if (!password.Any(char.IsLower))
                violations.Add("密码必须包含至少一个小写字母");

            if (!password.Any(char.IsDigit))
                violations.Add("密码必须包含至少一个数字");

            if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
                violations.Add("密码必须包含至少一个特殊字符");

            if (IsCommonWeakPassword(password))
                violations.Add("密码不能使用常见弱密码");

            return violations;
        }

        /// <summary>
        /// 是否为常见弱密码
        /// </summary>
        public bool IsCommonWeakPassword(string password)
        {
            return CommonWeakPasswords.Contains(password);
        }
    }
}
