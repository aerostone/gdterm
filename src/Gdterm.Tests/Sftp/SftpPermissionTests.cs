using System;
using Gdterm.Sftp;

namespace Gdterm.Tests.Sftp
{
    /// <summary>
    /// 权限三元组与 octal 双向：ToRwx 九字符 + Parse/Octal roundtrip（纯函数，零连接）。
    /// </summary>
    public static class SftpPermissionTests
    {
        public static void Run()
        {
            OctalRoundtrip();
            OctalKnownValues();
        }

        private static void OctalRoundtrip()
        {
            // OctalTo/Parse 的口径都是真实位值（如 493 = 0o755），不是十进制位 755
            string[] names = { "0", "644", "755", "600", "777", "400", "750" };
            foreach (var s in names)
            {
                int mode = Convert.ToInt32(s, 8);
                string rwx = SftpEnhancements.OctalToPermissionString(mode);
                Assert.Equal(9, rwx.Length, "rwx-len-" + s);
                int back = SftpEnhancements.ParsePermissionToOctal(rwx);
                Assert.Equal(mode, back, "octal-roundtrip-" + s);
            }
        }

        private static void OctalKnownValues()
        {
            int m644 = Convert.ToInt32("644", 8); // 420
            int m755 = Convert.ToInt32("755", 8); // 493
            Assert.Equal("rw-r--r--", SftpEnhancements.OctalToPermissionString(m644), "octal-644");
            Assert.Equal("rwxr-xr-x", SftpEnhancements.OctalToPermissionString(m755), "octal-755");
            Assert.Equal(m755, SftpEnhancements.ParsePermissionToOctal("rwxr-xr-x"), "parse-755");
        }
    }
}
