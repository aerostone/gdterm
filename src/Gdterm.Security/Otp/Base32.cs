using System;
using System.Text;
using Gdterm.Core;

namespace Gdterm.Security.Otp
{
    /// <summary>
    /// Base32（RFC 4648，A-Z2-7）。TOTP/HOTP 密钥的行业标准编码（otpauth:// URI 的 secret 参数）。
    /// 编码输出不带填充（与 Google Authenticator 等生态一致）；解码容忍大小写/空格/连字符/填充。
    /// </summary>
    public static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        /// <summary>编码为无填充 Base32 字符串。</summary>
        public static string Encode(byte[] data)
        {
            if (data == null) return "";
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        /// <summary>
        /// 解码 Base32（输入可含小写/空格/连字符/尾部 '=' 填充）。
        /// <exception cref="FormatException">含非法字符或空输入。</exception>
        /// </summary>
        public static byte[] Decode(string input)
        {
            if (string.IsNullOrEmpty(input))
                throw new FormatException(L10n.T("Base32 输入为空"));

            var cleaned = new StringBuilder();
            foreach (var ch in input)
            {
                var up = char.ToUpperInvariant(ch);
                if (up == '=' || up == ' ' || up == '-' || up == '\t' || up == '\r' || up == '\n') continue;
                int idx = Alphabet.IndexOf(up);
                if (idx < 0) throw new FormatException(L10n.T("非法 Base32 字符: '") + ch + "'");
                cleaned.Append(Alphabet[idx]);
            }
            if (cleaned.Length == 0)
                throw new FormatException(L10n.T("Base32 输入为空"));

            var outp = new byte[cleaned.Length * 5 / 8];
            int buffer = 0, bits = 0, o = 0;
            for (int i = 0; i < cleaned.Length; i++)
            {
                buffer = (buffer << 5) | Alphabet.IndexOf(cleaned[i]);
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    outp[o++] = (byte)((buffer >> bits) & 0xFF);
                }
            }
            return outp;
        }
    }
}
