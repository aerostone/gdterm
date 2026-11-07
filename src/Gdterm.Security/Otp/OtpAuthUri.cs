using System;
using System.Collections.Generic;
using System.Text;
using Gdterm.Core;

namespace Gdterm.Security.Otp
{
    /// <summary>
    /// otpauth:// URI 解析/生成（otpauth 标准，Google Authenticator 兼容）。
    /// 例：otpauth://totp/Example:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example&digits=6&period=30
    /// </summary>
    public sealed class OtpAuthUri
    {
        /// <summary>totp | hotp</summary>
        public string Type { get; set; }
        /// <summary>账户名（label 的 @ 前部分）。</summary>
        public string Account { get; set; }
        /// <summary>发行方（issuer 参数优先，否则 label 的 @ 后部分）。</summary>
        public string Issuer { get; set; }
        /// <summary>Base32 密钥（secret 参数）。</summary>
        public string Secret { get; set; }
        public int Digits { get; set; }
        public int Period { get; set; }
        public OtpAlgorithm Algorithm { get; set; }
        /// <summary>HOTP 起始计数器（totp 为 null）。</summary>
        public long? Counter { get; set; }

        /// <summary>
        /// 解析 otpauth:// URI。
        /// <exception cref="FormatException">不是 otpauth URI / 缺 secret / 参数非法。</exception>
        /// </summary>
        public static OtpAuthUri Parse(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new FormatException(L10n.T("OTP URI 为空"));

            var u = uri.Trim();
            const string Prefix = "otpauth://";
            if (u.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase) != 0)
                throw new FormatException(L10n.T("不是 otpauth:// URI"));

            var rest = u.Substring(Prefix.Length);
            int q = rest.IndexOf('?');
            var head = q < 0 ? rest : rest.Substring(0, q);
            var query = q < 0 ? "" : rest.Substring(q + 1);

            int slash = head.IndexOf('/');
            if (slash <= 0)
                throw new FormatException(L10n.T("URI 缺少类型段（totp/hotp）"));

            var type = head.Substring(0, slash).Trim().ToLowerInvariant();
            if (type != "totp" && type != "hotp")
                throw new FormatException(L10n.T("未知 OTP 类型: ") + type);

            // label 语义：含 ':' 时按「Issuer:account」（account 可含 @）；
            // 不含 ':' 且含 '@' 时按「account@issuer」启发式；issuer 参数优先级最高。
            var label = Unescape(head.Substring(slash + 1));
            var account = label;
            string issuerFromLabel = null;
            int colon = label.IndexOf(':');
            if (colon > 0)
            {
                issuerFromLabel = label.Substring(0, colon);
                account = label.Substring(colon + 1);
            }
            else
            {
                int at = label.LastIndexOf('@');
                if (at > 0)
                {
                    account = label.Substring(0, at);
                    issuerFromLabel = label.Substring(at + 1);
                }
            }

            var params_ = ParseQuery(query);
            var secret = GetParam(params_, "secret");
            if (string.IsNullOrWhiteSpace(secret))
                throw new FormatException(L10n.T("URI 缺少 secret 参数"));

            var digits = GetInt(params_, "digits", 6);
            if (digits < 6 || digits > 8)
                throw new FormatException(L10n.T("digits 非法（6-8）: ") + digits);
            var period = GetInt(params_, "period", 30);
            if (period <= 0 || period > 300)
                throw new FormatException(L10n.T("period 非法: ") + period);

            long? counter = null;
            if (type == "hotp")
                counter = GetLong(params_, "counter", 0);

            return new OtpAuthUri
            {
                Type = type,
                Account = account,
                Issuer = GetParam(params_, "issuer") ?? issuerFromLabel,
                Secret = secret.Trim().ToUpperInvariant(),
                Digits = digits,
                Period = period,
                Algorithm = OtpGenerator.ParseAlgorithm(GetParam(params_, "algorithm")),
                Counter = counter
            };
        }

        /// <summary>生成 otpauth:// URI（用于导出/分享，二维码场景由外部工具生成）。</summary>
        public static string Format(OtpAuthUri u)
        {
            if (u == null) throw new ArgumentNullException(nameof(u));
            var sb = new StringBuilder("otpauth://");
            sb.Append(u.Type == "hotp" ? "hotp" : "totp");
            var label = Escape(u.Issuer) + ":" + Escape(u.Account ?? "");
            sb.Append('/');
            sb.Append(label);
            sb.Append("?secret=").Append(Escape(u.Secret));
            if (!string.IsNullOrEmpty(u.Issuer)) sb.Append("&issuer=").Append(Escape(u.Issuer));
            if (u.Digits != 6) sb.Append("&digits=").Append(u.Digits);
            if (u.Type != "hotp" && u.Period != 30) sb.Append("&period=").Append(u.Period);
            if (u.Algorithm != OtpAlgorithm.Sha1)
                sb.Append("&algorithm=").Append(u.Algorithm == OtpAlgorithm.Sha256 ? "SHA256" : "SHA512");
            if (u.Type == "hotp" && u.Counter.HasValue) sb.Append("&counter=").Append(u.Counter.Value);
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return Uri.EscapeDataString(s ?? "");
        }

        private static string Unescape(string s)
        {
            try { return Uri.UnescapeDataString(s ?? ""); }
            catch { return s ?? ""; }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var kv in query.Split('&'))
            {
                if (string.IsNullOrWhiteSpace(kv)) continue;
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                var k = Unescape(kv.Substring(0, eq));
                var v = Unescape(kv.Substring(eq + 1));
                dict[k] = v;
            }
            return dict;
        }

        private static string GetParam(Dictionary<string, string> dict, string key)
        {
            string v;
            return dict.TryGetValue(key, out v) ? v : null;
        }

        private static int GetInt(Dictionary<string, string> dict, string key, int fallback)
        {
            var v = GetParam(dict, key);
            int n;
            if (v != null && int.TryParse(v, out n)) return n;
            return fallback;
        }

        private static long GetLong(Dictionary<string, string> dict, string key, long fallback)
        {
            var v = GetParam(dict, key);
            long n;
            if (v != null && long.TryParse(v, out n)) return n;
            return fallback;
        }
    }
}
