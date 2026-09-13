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
            int[] modes = { 0, 644, 755, 600, 777, 400, 750 };
            foreach (var m in modes)
            {
                string rwx = SftpEnhancements.OctalToPermissionString(m);
                Assert.Equal(9, rwx.Length, "rwx-len-" + m);
                int back = SftpEnhancements.ParsePermissionToOctal(rwx);
                Assert.Equal(m, back, "octal-roundtrip-" + m);
            }
        }

        private static void OctalKnownValues()
        {
            Assert.Equal("rw-r--r--", SftpEnhancements.OctalToPermissionString(644), "octal-644");
            Assert.Equal("rwxr-xr-x", SftpEnhancements.OctalToPermissionString(755), "octal-755");
            Assert.Equal(755, SftpEnhancements.ParsePermissionToOctal("rwxr-xr-x"), "parse-755");
        }
    }
}
