using System;
using System.Collections.Generic;
using System.Linq;
using Gdterm.Connections;
using Gdterm.Security.Otp;

namespace Gdterm.Tests.Security
{
    /// <summary>
    /// OTP 核心测试：RFC 4226（HOTP）/ RFC 6238（TOTP）标准测试向量 + Base32 + otpauth URI + 存储。
    /// </summary>
    public static class OtpTests
    {
        // RFC 4226/6238 附录的 20 字节测试密钥 ASCII "12345678901234567890"
        // （Base32 = GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ，即 Encoding20() 的编码）
        private static byte[] RfcKey()
        {
            return Encoding20();
        }

        public static void Run()
        {
            TestRfc4226HotpVectors();
            TestRfc6238TotpVectors();
            TestSecondsRemaining();
            TestBase32Roundtrip();
            TestBase32TolerantDecode();
            TestOtpAuthUriParse();
            TestOtpAuthUriRoundtrip();
            TestOtpAuthUriErrors();
            TestOtpEntryCodeAt();
            TestOtpStoreRoundtrip();
            TestOtpStoreProtector();
            TestParseAlgorithm();
        }

        /// <summary>RFC 4226 附录 D：SHA1，6 位，counter 0-9。</summary>
        private static void TestRfc4226HotpVectors()
        {
            var expected = new[]
            {
                "755224", "287082", "359152", "969429", "338314",
                "254676", "287922", "162583", "399871", "520489"
            };
            for (int i = 0; i < expected.Length; i++)
            {
                var code = OtpGenerator.Hotp(RfcKey(), i, 6, OtpAlgorithm.Sha1);
                Assert.Equal(expected[i], code, "RFC4226 HOTP counter=" + i);
            }
        }

        /// <summary>RFC 6238 附录 B：密钥 "12345678901234567890"（20 字节），t = 59s。</summary>
        private static void TestRfc6238TotpVectors()
        {
            var t = new DateTimeOffset(1970, 1, 1, 0, 0, 59, TimeSpan.Zero);
            var secret = Base32.Encode(Encoding20());
            Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", secret, "base32 of RFC key");
            Assert.Equal("94287082", OtpGenerator.Totp(secret, t, 30, 8, OtpAlgorithm.Sha1), "RFC6238 SHA1 8-digit");
            Assert.Equal("32247374", OtpGenerator.Totp(secret, t, 30, 8, OtpAlgorithm.Sha256), "RFC6238 SHA256 8-digit");
            Assert.Equal("69342147", OtpGenerator.Totp(secret, t, 30, 8, OtpAlgorithm.Sha512), "RFC6238 SHA512 8-digit");
            Assert.Equal("287082", OtpGenerator.Totp(secret, t, 30, 6, OtpAlgorithm.Sha1), "RFC6238 SHA1 6-digit");
        }

        private static byte[] Encoding20()
        {
            // 注意：不能写 ('1' + i%10)——i=9 时 '1'+9=':'（0x3a）而不是 '0'（0x30）。
            // CI 0.1.357 教训：曾因此生成 "123456789:123456789:" 密钥，RFC 向量全挂。
            return System.Text.Encoding.UTF8.GetBytes("12345678901234567890");
        }

        private static void TestSecondsRemaining()
        {
            // t=59s（period 30）→ 本周期 30..59，剩余 59-59=0？floor(59/30)=1，剩余 30-(59%30)=1
            var t = new DateTimeOffset(1970, 1, 1, 0, 0, 59, TimeSpan.Zero);
            Assert.Equal(1, OtpGenerator.SecondsRemaining(30, t), "remaining at t=59");
            var t0 = new DateTimeOffset(1970, 1, 1, 0, 0, 30, TimeSpan.Zero);
            Assert.Equal(30, OtpGenerator.SecondsRemaining(30, t0), "remaining at period start");
        }

        private static void TestBase32Roundtrip()
        {
            // RFC 4648 测试向量
            Assert.Equal("", Base32.Encode(new byte[0]), "empty");
            Assert.Equal("JU", Base32.Encode(new[] { (byte)0x4D }), "1-byte M");
            Assert.Equal("JVHA", Base32.Encode(new[] { (byte)0x4D, (byte)0x4E }), "2-byte MN");
            Assert.Equal("JVHE6", Base32.Encode(new[] { (byte)0x4D, (byte)0x4E, (byte)0x4F }), "3-byte MNO");
            Assert.Equal("KRSXG5A", Base32.Encode(new[] { (byte)84, (byte)101, (byte)115, (byte)116 }), "4-byte Test");
            Assert.Equal("KRSXG5DT", Base32.Encode(new[] { (byte)84, (byte)101, (byte)115, (byte)116, (byte)115 }), "5-byte Tests");

            var data = new byte[] { 1, 2, 3, 16, 17, 18, 32, 33, 34, 64, 65, 66 };
            Assert.Equal("AEBAGEARCIQCCISAIFBA", Base32.Encode(data), "12-byte vector");
            Assert.True(data.SequenceEqual(Base32.Decode("AEBAGEARCIQCCISAIFBA")), "roundtrip 12-byte");
        }

        private static void TestBase32TolerantDecode()
        {
            // 小写 / 空格 / 连字符 / 填充均可解码
            Assert.True(Base32.Decode("aebagearc iqccis aifba".Replace(" ", "")).SequenceEqual(Base32.Decode("AEBAGEARCIQCCISAIFBA")), "lowercase");
            Assert.True(Base32.Decode("aeb-agearc iqcc-isaifba").SequenceEqual(Base32.Decode("AEBAGEARCIQCCISAIFBA")), "spaces/dashes");
            Assert.True(Base32.Decode("KRSXG5A=").SequenceEqual(Base32.Decode("KRSXG5A")), "padding");
            bool threw = false;
            try { Base32.Decode("!!not-base32!!"); }
            catch (FormatException) { threw = true; }
            Assert.True(threw, "invalid chars throw");
        }

        private static void TestOtpAuthUriParse()
        {
            var u = OtpAuthUri.Parse(
                "otpauth://totp/Example:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example&digits=6&period=30");
            Assert.Equal("totp", u.Type, "uri type");
            Assert.Equal("Example", u.Issuer, "uri issuer");
            Assert.Equal("alice@example.com", u.Account, "uri account");
            Assert.Equal("JBSWY3DPEHPK3PXP", u.Secret, "uri secret");
            Assert.Equal(6, u.Digits, "uri digits");
            Assert.Equal(30, u.Period, "uri period");

            // issuer 仅在 label @ 后
            var u2 = OtpAuthUri.Parse("otpauth://totp/GitHub:user@github.com?secret=JBSWY3DPEHPK3PXP");
            Assert.Equal("GitHub", u2.Issuer, "issuer from label");
            Assert.Equal("user@github.com", u2.Account, "account from label");

            // HOTP 带 counter
            var u3 = OtpAuthUri.Parse("otpauth://hotp/MyBank:acct?secret=JBSWY3DPEHPK3PXP&counter=7");
            Assert.Equal("hotp", u3.Type, "hotp type");
            Assert.Equal(7L, u3.Counter.Value, "hotp counter");
        }

        private static void TestOtpAuthUriRoundtrip()
        {
            var u = new OtpAuthUri
            {
                Type = "totp",
                Issuer = "GitHub",
                Account = "a b@x.com",
                Secret = "JBSWY3DPEHPK3PXP",
                Digits = 8,
                Period = 60,
                Algorithm = OtpAlgorithm.Sha256
            };
            var s = OtpAuthUri.Format(u);
            var p = OtpAuthUri.Parse(s);
            Assert.Equal("GitHub", p.Issuer, "roundtrip issuer");
            Assert.Equal("a b@x.com", p.Account, "roundtrip account");
            Assert.Equal(8, p.Digits, "roundtrip digits");
            Assert.Equal(60, p.Period, "roundtrip period");
            Assert.Equal(OtpAlgorithm.Sha256, p.Algorithm, "roundtrip algorithm");
        }

        private static void TestOtpAuthUriErrors()
        {
            ExpectFormatEx(() => OtpAuthUri.Parse("https://example.com/x"), "non-otpauth scheme");
            ExpectFormatEx(() => OtpAuthUri.Parse("otpauth://totp/Acct?issuer=A"), "missing secret");
            ExpectFormatEx(() => OtpAuthUri.Parse("otpauth://rot/Acct?secret=JBSWY3DPEHPK3PXP"), "unknown type");
            ExpectFormatEx(() => OtpAuthUri.Parse("otpauth://totp/Acct?secret=JBSWY3DPEHPK3PXP&digits=4"), "bad digits");
        }

        private static void ExpectFormatEx(Action a, string label)
        {
            bool threw = false;
            try { a(); }
            catch (FormatException) { threw = true; }
            Assert.True(threw, label);
        }

        private static void TestOtpEntryCodeAt()
        {
            var t = new DateTimeOffset(1970, 1, 1, 0, 0, 59, TimeSpan.Zero);
            var secret = Base32.Encode(Encoding20());
            var entry = new OtpEntry
            {
                Issuer = "GitHub",
                Account = "me",
                Secret = secret,
                Digits = 6,
                Period = 30,
                Algorithm = OtpAlgorithm.Sha1,
                IsTotp = true
            };
            Assert.Equal("287082", entry.CodeAt(t), "entry code at fixed time");
            Assert.Equal("GitHub me", entry.Title, "entry title");

            // 未解锁（gdo1: 前缀）→ null
            entry.Secret = "gdo1:AAAA";
            Assert.True(entry.CodeAt() == null, "locked entry code null");

            // 密钥损坏 → null（不抛）
            entry.Secret = "NOT_BASE32!!";
            Assert.True(entry.CodeAt() == null, "broken secret code null");
        }

        private static void TestOtpStoreRoundtrip()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "gdterm-otp-test-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new OtpStoreJson(path);
                var list = store.LoadAll();
                Assert.Equal(0, list.Count, "empty store");

                store.Add(new OtpEntry
                {
                    Id = "e1",
                    Issuer = "GitHub",
                    Account = "me",
                    Secret = "JBSWY3DPEHPK3PXP",
                    Digits = 6,
                    Period = 30,
                    Algorithm = OtpAlgorithm.Sha1,
                    IsTotp = true
                });
                store.Add(new OtpEntry
                {
                    Id = "e2",
                    Issuer = "MyBank",
                    Account = "acct \"quoted\"",
                    Secret = "GEZDGNBVGY3TQOJQ",
                    Digits = 8,
                    Period = 60,
                    Algorithm = OtpAlgorithm.Sha256,
                    IsTotp = false,
                    Counter = 42
                });

                var reloaded = store.LoadAll();
                Assert.Equal(2, reloaded.Count, "reloaded count");
                Assert.Equal("GitHub", reloaded[0].Issuer, "reload issuer");
                Assert.Equal("acct \"quoted\"", reloaded[1].Account, "reload quoted account");
                Assert.Equal(8, reloaded[1].Digits, "reload digits");
                Assert.Equal(OtpAlgorithm.Sha256, reloaded[1].Algorithm, "reload algorithm");
                Assert.True(!reloaded[1].IsTotp, "reload hotp flag");
                Assert.Equal(42, reloaded[1].Counter, "reload counter");

                store.Delete("e1");
                var afterDelete = store.LoadAll();
                Assert.Equal(1, afterDelete.Count, "after delete count");
                Assert.Equal("e2", afterDelete[0].Id, "after delete id");

                bool threw = false;
                try { store.Delete("nope"); }
                catch (KeyNotFoundException) { threw = true; }
                Assert.True(threw, "delete missing throws");
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                catch { }
            }
        }

        /// <summary>protector 接线：落盘密文化，读回自动还原；未解锁时保持密文。</summary>
        private static void TestOtpStoreProtector()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "gdterm-otp-test-" + Guid.NewGuid().ToString("N") + ".json");
            var plainSecret = "JBSWY3DPEHPK3PXP";
            try
            {
                OtpStoreJson.SetSecretProtector(
                    p => "gdo1:" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p)),
                    stored => stored != null && stored.StartsWith("gdo1:")
                        ? System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(stored.Substring(5)))
                        : stored);
                try
                {
                    var store = new OtpStoreJson(path);
                    store.Add(new OtpEntry
                    {
                        Id = "p1",
                        Issuer = "T",
                        Account = "a",
                        Secret = plainSecret,
                        Digits = 6,
                        Period = 30,
                        IsTotp = true
                    });
                    // 文件内容必须是密文
                    var raw = System.IO.File.ReadAllText(path);
                    Assert.True(raw.Contains("gdo1:"), "file stores cipher");
                    Assert.True(!raw.Contains(plainSecret), "file hides plain secret");
                    // 读回自动还原
                    var list = store.LoadAll();
                    Assert.Equal(plainSecret, list[0].Secret, "unprotect on load");
                }
                finally
                {
                    OtpStoreJson.SetSecretProtector(null, null);
                }
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                catch { }
            }
        }

        private static void TestParseAlgorithm()
        {
            Assert.Equal(OtpAlgorithm.Sha1, OtpGenerator.ParseAlgorithm(null), "null -> sha1");
            Assert.Equal(OtpAlgorithm.Sha1, OtpGenerator.ParseAlgorithm("sha1"), "sha1");
            Assert.Equal(OtpAlgorithm.Sha256, OtpGenerator.ParseAlgorithm("SHA256"), "sha256");
            Assert.Equal(OtpAlgorithm.Sha512, OtpGenerator.ParseAlgorithm("Sha-512"), "sha512");
            Assert.Equal(OtpAlgorithm.Sha1, OtpGenerator.ParseAlgorithm("MD5"), "unknown -> sha1");
        }
    }
}
