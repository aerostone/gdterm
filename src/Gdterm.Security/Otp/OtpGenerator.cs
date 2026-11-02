using System;
using System.Security.Cryptography;

namespace Gdterm.Security.Otp
{
    /// <summary>OTP 摘要算法（otpauth algorithm 参数）。</summary>
    public enum OtpAlgorithm
    {
        Sha1,
        Sha256,
        Sha512
    }

    /// <summary>
    /// TOTP/HOTP 生成器（纯逻辑，可测）。
    /// HOTP：RFC 4226（计数器）；TOTP：RFC 6238（时间驱动，counter = floor(unixtime / period)）。
    /// </summary>
    public static class OtpGenerator
    {
        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// HOTP：RFC 4226。HMAC(key, counter-8字节大端) → 动态截断 → 取 digits 位十进制。
        /// </summary>
        public static string Hotp(byte[] key, long counter, int digits, OtpAlgorithm algorithm)
        {
            if (key == null || key.Length == 0) throw new ArgumentException("key 为空");
            if (counter < 0) throw new ArgumentOutOfRangeException(nameof(counter));
            if (digits < 6 || digits > 10) throw new ArgumentOutOfRangeException(nameof(digits));

            var msg = new byte[8];
            for (int i = 7; i >= 0; i--)
            {
                msg[i] = (byte)(counter & 0xFF);
                counter >>= 8;
            }

            byte[] mac;
            using (var hmac = CreateHmac(key, algorithm))
                mac = hmac.ComputeHash(msg);

            // 动态截断：低 4 位作偏移，取 4 字节大端，清最高位
            int offset = mac[mac.Length - 1] & 0x0F;
            int bin = ((mac[offset] & 0x7F) << 24)
                     | ((mac[offset + 1] & 0xFF) << 16)
                     | ((mac[offset + 2] & 0xFF) << 8)
                     | (mac[offset + 3] & 0xFF);
            long mod = 1;
            for (int i = 0; i < digits; i++) mod *= 10;
            return (bin % mod).ToString("D" + digits);
        }

        /// <summary>
        /// TOTP：RFC 6238。secret 为 Base32；atUtc 缺省为当前 UTC。
        /// </summary>
        public static string Totp(
            string base32Secret,
            DateTimeOffset? atUtc = null,
            int periodSeconds = 30,
            int digits = 6,
            OtpAlgorithm algorithm = OtpAlgorithm.Sha1)
        {
            var key = Base32.Decode(base32Secret);
            var now = atUtc ?? DateTimeOffset.UtcNow;
            long unixSeconds = (long)Math.Floor((now - UnixEpoch).TotalSeconds);
            if (unixSeconds < 0) unixSeconds = 0;
            return Hotp(key, unixSeconds / periodSeconds, digits, algorithm);
        }

        /// <summary>当前 TOTP 周期剩余秒数（UI 倒计时显示）。</summary>
        public static int SecondsRemaining(int periodSeconds = 30, DateTimeOffset? atUtc = null)
        {
            var now = atUtc ?? DateTimeOffset.UtcNow;
            long unixSeconds = (long)Math.Floor((now - UnixEpoch).TotalSeconds);
            if (unixSeconds < 0) unixSeconds = 0;
            return periodSeconds - (int)(unixSeconds % periodSeconds);
        }

        /// <summary>把 algorithm 名（SHA1/SHA256/SHA512，大小写不敏感）解析为枚举，未知回退 Sha1。</summary>
        public static OtpAlgorithm ParseAlgorithm(string name)
        {
            if (string.IsNullOrEmpty(name)) return OtpAlgorithm.Sha1;
            switch (name.Trim().ToUpperInvariant())
            {
                case "SHA256":
                case "SHA-256":
                    return OtpAlgorithm.Sha256;
                case "SHA512":
                case "SHA-512":
                    return OtpAlgorithm.Sha512;
                default:
                    return OtpAlgorithm.Sha1;
            }
        }

        private static HMAC CreateHmac(byte[] key, OtpAlgorithm algorithm)
        {
            switch (algorithm)
            {
                case OtpAlgorithm.Sha256: return new HMACSHA256(key);
                case OtpAlgorithm.Sha512: return new HMACSHA512(key);
                default: return new HMACSHA1(key);
            }
        }
    }
}
