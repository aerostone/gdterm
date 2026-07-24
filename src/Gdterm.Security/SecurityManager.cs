using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Timers;
using Gdterm.Security.Models;

namespace Gdterm.Security
{
    /// <summary>
    /// 安全管理器实现——闲时锁定、手动锁定/解锁、主密码哈希存储
    /// </summary>
    public class SecurityManager : ISecurityManager
    {
        private readonly Timer _idleTimer;
        private DateTime _lastActivity;
        private MasterPasswordConfig _passwordConfig;
        private bool _disposed;

        public bool IsLocked { get; private set; } = true; // 默认锁定

        public TimeSpan IdleTimeout
        {
            get => TimeSpan.FromMilliseconds(_idleTimer.Interval);
            set => _idleTimer.Interval = value.TotalMilliseconds;
        }

        public event EventHandler<LockStateChangedEventArgs> LockStateChanged;

        /// <param name="idleTimeout">闲时超时时间（默认 5 分钟）</param>
        /// <param name="passwordConfig">已保存的主密码配置（null 表示首次使用）</param>
        public SecurityManager(TimeSpan? idleTimeout = null, MasterPasswordConfig passwordConfig = null)
        {
            _passwordConfig = passwordConfig;
            _lastActivity = DateTime.UtcNow;

            _idleTimer = new Timer();
            _idleTimer.Interval = (idleTimeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds;
            _idleTimer.AutoReset = true;
            _idleTimer.Elapsed += OnIdleTimerElapsed;
            _idleTimer.Start();
        }

        public void ResetIdleTimer()
        {
            _lastActivity = DateTime.UtcNow;
        }

        public void LockNow()
        {
            if (!IsLocked)
            {
                IsLocked = true;
                OnLockStateChanged(new LockStateChangedEventArgs(true, "manual"));
            }
        }

        public bool Unlock(string masterPassword)
        {
            if (string.IsNullOrEmpty(masterPassword))
                return false;

            // 如果没有设置过主密码，首次解锁即设置
            if (_passwordConfig == null)
            {
                SetMasterPassword(null, masterPassword);
                IsLocked = false;
                OnLockStateChanged(new LockStateChangedEventArgs(false, "unlock"));
                return true;
            }

            // 验证密码
            if (VerifyPassword(masterPassword, _passwordConfig))
            {
                IsLocked = false;
                OnLockStateChanged(new LockStateChangedEventArgs(false, "unlock"));
                return true;
            }

            return false;
        }

        public void SetMasterPassword(string oldPassword, string newPassword)
        {
            // 如果已设置主密码，需要验证旧密码
            if (_passwordConfig != null)
            {
                if (string.IsNullOrEmpty(oldPassword))
                    throw new ArgumentException("必须提供旧密码");

                if (!VerifyPassword(oldPassword, _passwordConfig))
                    throw new ArgumentException("旧密码不正确");
            }

            // 校验新密码强度
            var violations = ValidatePasswordStrength(newPassword);
            if (violations.Count > 0)
                throw new WeakPasswordException(violations);

            // 生成新 salt + 哈希
            var salt = GenerateSalt();
            var hash = HashPassword(newPassword, salt);

            _passwordConfig = new MasterPasswordConfig
            {
                PasswordHash = Convert.ToBase64String(hash),
                Salt = Convert.ToBase64String(salt),
                LastChanged = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 获取当前主密码配置（用于持久化保存）
        /// </summary>
        public MasterPasswordConfig GetPasswordConfig()
        {
            return _passwordConfig;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _idleTimer?.Stop();
            _idleTimer?.Dispose();
        }

        private void OnIdleTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (IsLocked) return;

            var idleTime = DateTime.UtcNow - _lastActivity;
            if (idleTime >= IdleTimeout)
            {
                IsLocked = true;
                OnLockStateChanged(new LockStateChangedEventArgs(true, "idle"));
            }
        }

        private void OnLockStateChanged(LockStateChangedEventArgs e)
        {
            LockStateChanged?.Invoke(this, e);
        }

        private static bool VerifyPassword(string password, MasterPasswordConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.Salt))
                return false;

            var salt = Convert.FromBase64String(config.Salt);
            var expectedHash = Convert.FromBase64String(config.PasswordHash);
            var actualHash = HashPassword(password, salt);

            return ConstantTimeEquals(expectedHash, actualHash);
        }

        private static byte[] HashPassword(string password, byte[] salt)
        {
            using (var sha256 = SHA256.Create())
            {
                var passwordBytes = Encoding.UTF8.GetBytes(password);
                var combined = new byte[salt.Length + passwordBytes.Length];
                Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
                Buffer.BlockCopy(passwordBytes, 0, combined, salt.Length, passwordBytes.Length);
                return sha256.ComputeHash(combined);
            }
        }

        private static byte[] GenerateSalt()
        {
            var salt = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;

            var result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }
            return result == 0;
        }

        private static IList<string> ValidatePasswordStrength(string password)
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

            return violations;
        }
    }

    /// <summary>
    /// 密码强度不足异常
    /// </summary>
    public class WeakPasswordException : Exception
    {
        public IList<string> Violations { get; set; }

        public WeakPasswordException(IList<string> violations)
            : base($"密码强度不足，违反 {violations.Count} 条规则")
        {
            Violations = violations;
        }
    }
}
