using System;

namespace Gdterm.Security.Otp
{
    /// <summary>
    /// 一条 OTP 凭据（TOTP 为主，HOTP 预留）。Secret 为 Base32 明文（内存态）；
    /// 落盘由 OtpStoreJson 的 protector 处理（gdo1: AES 主密码加密）。
    /// </summary>
    public sealed class OtpEntry
    {
        public string Id { get; set; }
        /// <summary>发行方（如 GitHub、某云平台）。</summary>
        public string Issuer { get; set; }
        /// <summary>账户名（如 user@example.com）。</summary>
        public string Account { get; set; }
        /// <summary>Base32 密钥。</summary>
        public string Secret { get; set; }
        /// <summary>动态码位数（6-8）。</summary>
        public int Digits { get; set; }
        /// <summary>TOTP 周期秒数（常用 30）。</summary>
        public int Period { get; set; }
        public OtpAlgorithm Algorithm { get; set; }
        /// <summary>false = HOTP（Counter 手动推进）。</summary>
        public bool IsTotp { get; set; }
        public long Counter { get; set; }

        /// <summary>展示名：发行方 + 账户（缺省项省略）。</summary>
        public string Title
        {
            get
            {
                if (string.IsNullOrEmpty(Issuer)) return Account ?? "";
                if (string.IsNullOrEmpty(Account)) return Issuer;
                return Issuer + " " + Account;
            }
        }

        /// <summary>当前（或指定时刻）的动态码。Secret 未解密（gdo1: 前缀）时返回 null。</summary>
        public string CodeAt(DateTimeOffset? atUtc = null)
        {
            if (string.IsNullOrEmpty(Secret)) return null;
            if (Secret.StartsWith("gdo1:", StringComparison.Ordinal)) return null; // 未解锁
            try
            {
                if (IsTotp)
                    return OtpGenerator.Totp(Secret, atUtc, Period, Digits, Algorithm);
                return OtpGenerator.Hotp(Base32.Decode(Secret), Counter, Digits, Algorithm);
            }
            catch
            {
                return null; // 密钥损坏
            }
        }

        /// <summary>从解析后的 otpauth URI 建条目（Id 由 store 分配）。</summary>
        public static OtpEntry FromUri(OtpAuthUri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            return new OtpEntry
            {
                Issuer = uri.Issuer,
                Account = uri.Account,
                Secret = uri.Secret,
                Digits = uri.Digits,
                Period = uri.Period,
                Algorithm = uri.Algorithm,
                IsTotp = uri.Type != "hotp",
                Counter = uri.Counter ?? 0
            };
        }
    }
}
