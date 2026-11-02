using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;

namespace Gdterm.Tests.Connections
{
    /// <summary>
    /// A2 外部格式导入回归（Xshell .xsh / MobaXterm.ini / WindTerm user.sessions）。
    /// fixture 全部由代码生成到临时目录（MobaXterm 用真实 GBK 字节，覆盖编码路径）。
    /// </summary>
    public static class ExternalFormatImporterTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] ExternalFormatImporter");
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-extimport-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                TestXshellFile(dir);
                TestXshellDirectory(dir);
                TestMobaXterm(dir);
                TestWindTerm(dir);
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TestXshellFile(string dir)
        {
            var path = Path.Combine(dir, "prod-web.xsh");
            File.WriteAllText(path,
                "Type=SSH\r\n" +
                "HostName=192.168.1.10\r\n" +
                "Port=2222\r\n" +
                "UserName=admin\r\n" +
                "Password=must-not-import\r\n",
                new UTF8Encoding(true)); // UTF-8 BOM 路径

            var list = ExternalFormatImporter.ImportXshell(path);
            Assert.Equal(1, list.Count, "xsh: one connection");
            var c = list[0];
            Assert.Equal("prod-web", c.Name, "xsh: name from filename");
            Assert.Equal("192.168.1.10", c.Host, "xsh: host");
            Assert.Equal(2222, c.Port, "xsh: port");
            Assert.Equal("admin", c.Username, "xsh: username");
            Assert.Equal(ProtocolType.SSH, c.Protocol, "xsh: protocol");
            Assert.True(c.Metadata != null && c.Metadata.ContainsKey("source")
                && c.Metadata["source"] == "Xshell", "xsh: metadata source");
        }

        private static void TestXshellDirectory(string dir)
        {
            var sub = Path.Combine(dir, "xsessions");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "a-ssh.xsh"),
                "Type=SSH\r\nHostname=10.0.0.1\r\nPort=22\r\nUserName=root\r\n");
            File.WriteAllText(Path.Combine(sub, "b-rdp.xsh"),
                "Type=RDP\r\nHostname=10.0.0.2\r\nPort=3390\r\nUsername=ops\r\n");
            File.WriteAllText(Path.Combine(sub, "notes.txt"), "not a session\r\n");

            var list = ExternalFormatImporter.ImportXshell(sub);
            Assert.Equal(2, list.Count, "xsh dir: two .xsh sessions");

            var rdp = list.FirstOrDefault(x => x.Host == "10.0.0.2");
            Assert.True(rdp != null, "xsh dir: rdp session found");
            if (rdp != null)
            {
                Assert.Equal(ProtocolType.RDP, rdp.Protocol, "xsh dir: rdp protocol");
                Assert.Equal(3390, rdp.Port, "xsh dir: rdp port");
            }

            // GBK（无 BOM）.xsh：中文用户名 + 严格 UTF-8 解码失败 → cp936 回退
            var gbkPath = Path.Combine(sub, "gbk-会话.xsh");
            File.WriteAllBytes(gbkPath, Encoding.GetEncoding(936).GetBytes(
                "Type=SSH\r\nHostname=10.9.9.9\r\nUserName=中文用户\r\n"));
            var gbkList = ExternalFormatImporter.ImportXshell(gbkPath);
            Assert.Equal(1, gbkList.Count, "xsh gbk: parsed");
            Assert.Equal("中文用户", gbkList[0].Username, "xsh gbk: cp936 username");
        }

        private static void TestMobaXterm(string dir)
        {
            // MobaXterm 按 ANSI 页写盘：构造真实 GBK 字节（中文分组名）
            var ini =
                "[Settings]\r\n" +
                "LastCmd=something\r\n" +
                "\r\n" +
                "[Bookmarks]\r\n" +
                "SubRep=生产\r\n" +
                "web1=#109#1%192.168.1.100%22%root%\r\n" +
                "db=#1#0%10.1.1.5%3390%administrator%\r\n" +
                "serial1=#131#0%COM3%115200%\r\n" +
                "vnc1=#2#0%192.168.9.8%5900%user%\r\n" +
                "\r\n" +
                "[Bookmarks_1]\r\n" +
                "SubRep=dev\r\n" +
                "tel1=#98#0%192.168.9.9%2323%user%\r\n" +
                "plainline=not-a-bookmark\r\n";

            var path = Path.Combine(dir, "MobaXterm.ini");
            File.WriteAllBytes(path, Encoding.GetEncoding(936).GetBytes(ini));

            var list = ExternalFormatImporter.ImportMobaXterm(path);
            // vnc1 跳过（gdterm 无 VNC），plainline 跳过 → 4 条
            Assert.Equal(4, list.Count, "moba: 4 importable sessions (vnc skipped)");

            var web = list.FirstOrDefault(x => x.Name == "web1");
            Assert.True(web != null, "moba: web1 found");
            if (web != null)
            {
                Assert.Equal(ProtocolType.SSH, web.Protocol, "moba: ssh code 109");
                Assert.Equal("192.168.1.100", web.Host, "moba: host");
                Assert.Equal(22, web.Port, "moba: port");
                Assert.Equal("root", web.Username, "moba: username");
                Assert.Equal("生产", web.GroupPath, "moba: SubRep folder gbk");
            }

            var db = list.FirstOrDefault(x => x.Name == "db");
            Assert.True(db != null && db.Protocol == ProtocolType.RDP, "moba: rdp code 1");
            if (db != null) Assert.Equal(3390, db.Port, "moba: rdp port");

            var serial = list.FirstOrDefault(x => x.Name == "serial1");
            Assert.True(serial != null && serial.Protocol == ProtocolType.Serial, "moba: serial code 131");
            if (serial != null)
            {
                Assert.Equal("COM3", serial.Host, "moba: serial host=port name");
                Assert.True(serial.Serial != null
                    && serial.Serial.PortName == "COM3"
                    && serial.Serial.BaudRate == 115200, "moba: serial config filled");
            }

            var tel = list.FirstOrDefault(x => x.Name == "tel1");
            Assert.True(tel != null && tel.Protocol == ProtocolType.Telnet, "moba: telnet code 98");
            if (tel != null) Assert.Equal("dev", tel.GroupPath, "moba: second folder");
        }

        private static void TestWindTerm(string dir)
        {
            var json =
                "{\r\n" +
                "  \"sessions\": [\r\n" +
                "    {\r\n" +
                "      \"session.uuid\": \"abc-1\",\r\n" +
                "      \"session.label\": \"prod-web\",\r\n" +
                "      \"session.group\": \"prod\",\r\n" +
                "      \"session.protocol\": \"SSH\",\r\n" +
                "      \"session.target\": \"root@10.2.0.1\",\r\n" +
                "      \"session.port\": 22,\r\n" +
                "      \"terminal.altModifierKey\": 1,\r\n" +
                "      \"window.position\": { \"x\": 0, \"y\": 0 }\r\n" +
                "    },\r\n" +
                "    {\r\n" +
                "      \"session.uuid\": \"def-2\",\r\n" +
                "      \"session.label\": \"git-bash\",\r\n" +
                "      \"session.protocol\": \"Shell\",\r\n" +
                "      \"session.target\": \"D:/Program Files/Git/bin/bash.exe\"\r\n" +
                "    },\r\n" +
                "    {\r\n" +
                "      \"session.uuid\": \"ghi-3\",\r\n" +
                "      \"session.label\": \"win-rdp\",\r\n" +
                "      \"session.protocol\": \"RDP\",\r\n" +
                "      \"session.target\": \"10.3.0.2\",\r\n" +
                "      \"session.port\": 3389\r\n" +
                "    }\r\n" +
                "  ]\r\n" +
                "}\r\n";

            var path = Path.Combine(dir, "user.sessions");
            File.WriteAllText(path, json, new UTF8Encoding(false)); // 无 BOM UTF-8 路径

            var list = ExternalFormatImporter.ImportWindTerm(path);
            Assert.Equal(2, list.Count, "windterm: 2 remote sessions (Shell skipped)");

            var web = list.FirstOrDefault(x => x.Name == "prod-web");
            Assert.True(web != null, "windterm: prod-web found");
            if (web != null)
            {
                Assert.Equal(ProtocolType.SSH, web.Protocol, "windterm: ssh");
                Assert.Equal("10.2.0.1", web.Host, "windterm: user@host split host");
                Assert.Equal("root", web.Username, "windterm: user@host split user");
                Assert.Equal(22, web.Port, "windterm: numeric port");
                Assert.Equal("prod", web.GroupPath, "windterm: group");
            }

            var rdp = list.FirstOrDefault(x => x.Name == "win-rdp");
            Assert.True(rdp != null && rdp.Protocol == ProtocolType.RDP, "windterm: rdp");
            if (rdp != null) Assert.Equal(3389, rdp.Port, "windterm: rdp port");

            // 空文件 / 缺失文件不抛
            var empty = ExternalFormatImporter.ImportWindTerm(Path.Combine(dir, "missing.sessions"));
            Assert.Equal(0, empty.Count, "windterm: missing file empty list");
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }
}
