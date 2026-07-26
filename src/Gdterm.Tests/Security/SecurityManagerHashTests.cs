using System;
using System.Security.Cryptography;
using System.Text;
using Gdterm.Security.Models;

namespace Gdterm.Tests.Security
{
    /// <summary>
    /// 与 SecurityManager 对齐的主密码哈希行为静态复现测试。
    /// </summary>
    public static class SecurityManagerHashTests
    {
        public static void Run()
        {
            var password = "CorrectHorseBattery1!";
            var salt = Encoding.UTF8.GetBytes("0123456789abcdef"); // 16 bytes

            byte[] legacy;
            using (var sha = SHA256.Create())
            {
                var pw = Encoding.UTF8.GetBytes(password);
                var combined = new byte[salt.Length + pw.Length];
                Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
                Buffer.BlockCopy(pw, 0, combined, salt.Length, pw.Length);
                legacy = sha.ComputeHash(combined);
            }

            byte[] pbkdf2;
            using (var derive = new Rfc2898DeriveBytes(password, salt, 100000))
                pbkdf2 = derive.GetBytes(32);

            Assert.True(legacy.Length == 32, "legacy hash length");
            Assert.True(pbkdf2.Length == 32, "pbkdf2 length");
            Assert.True(!ByteEqual(legacy, pbkdf2), "legacy != pbkdf2 for same salt/pass");

            byte[] pbkdf2b;
            using (var derive = new Rfc2898DeriveBytes("WrongPassword1!", salt, 100000))
                pbkdf2b = derive.GetBytes(32);
            Assert.True(!ByteEqual(pbkdf2, pbkdf2b), "different password different hash");

            var cfg = new MasterPasswordConfig
            {
                PasswordHash = Convert.ToBase64String(pbkdf2),
                Salt = Convert.ToBase64String(salt),
                Algorithm = "pbkdf2",
                Iterations = 100000
            };
            Assert.True(!cfg.IsLegacySha256, "pbkdf2 not legacy");

            var legacyCfg = new MasterPasswordConfig
            {
                PasswordHash = Convert.ToBase64String(legacy),
                Salt = Convert.ToBase64String(salt)
            };
            Assert.True(legacyCfg.IsLegacySha256, "null algorithm is legacy");
        }

        private static bool ByteEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
