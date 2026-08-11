using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Security;
using Gdterm.Sftp.Models;
using Renci.SshNet;

namespace Gdterm.Sftp
{
    /// <summary>
    /// SFTP 增强服务——文件预览、权限编辑
    /// </summary>
    public static class SftpEnhancements
    {
        // ── 文件预览（文本前100行 + 图片缩略图检测） ──

        /// <summary>预览文本文件内容（前 maxLines 行）</summary>
        public static async Task<string> PreviewTextFileAsync(ISftpService sftp, string remotePath, int maxLines = 100, CancellationToken ct = default)
        {
            // 下载到临时文件
            var tempPath = Path.Combine(Path.GetTempPath(), "gdterm_preview_" + Path.GetFileName(remotePath));
            try
            {
                await sftp.DownloadAsync(remotePath, tempPath, null, ct);
                var lines = new List<string>();
                using (var reader = new StreamReader(tempPath, Encoding.UTF8, true))
                {
                    string line;
                    int count = 0;
                    while ((line = await reader.ReadLineAsync()) != null && count < maxLines)
                    {
                        lines.Add(line);
                        count++;
                    }
                    if (!reader.EndOfStream)
                        lines.Add(string.Format("... (共{0}行以上，仅显示前{1}行)", maxLines, maxLines));
                }
                return string.Join("\n", lines);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>检测文件是否为可预览的图片类型</summary>
        public static bool IsImageFile(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".ico" || ext == ".svg";
        }

        /// <summary>检测文件是否为文本类型</summary>
        public static bool IsTextFile(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext == ".txt" || ext == ".log" || ext == ".conf" || ext == ".cfg" || ext == ".ini" || ext == ".xml" ||
                   ext == ".json" || ext == ".yaml" || ext == ".yml" || ext == ".sh" || ext == ".bash" || ext == ".py" ||
                   ext == ".rb" || ext == ".pl" || ext == ".js" || ext == ".ts" || ext == ".java" || ext == ".c" ||
                   ext == ".cpp" || ext == ".h" || ext == ".cs" || ext == ".go" || ext == ".rs" || ext == ".md" ||
                   ext == ".csv" || ext == ".sql" || ext == ".properties" || ext == ".env" || ext == ".toml" ||
                   ext == ".service" || ext == ".cron" || ext == ".hosts" || ext == ".nginx" || ext == ".makefile" ||
                   ext == ".dockerfile" || ext == ".gitignore" || ext == ".htaccess";
        }

        // ── 权限编辑器 ──

        /// <summary>解析 rwxrwxrwx 权限字符串为八进制</summary>
        public static int ParsePermissionToOctal(string rwx)
        {
            if (string.IsNullOrEmpty(rwx) || rwx.Length < 9) return 0;
            int octal = 0;
            // Owner: rwx
            if (rwx[0] == 'r') octal += 4 << 6;
            if (rwx[1] == 'w') octal += 2 << 6;
            if (rwx[2] == 'x' || rwx[2] == 's') octal += 1 << 6;
            // Group: rwx
            if (rwx[3] == 'r') octal += 4 << 3;
            if (rwx[4] == 'w') octal += 2 << 3;
            if (rwx[5] == 'x' || rwx[5] == 's') octal += 1 << 3;
            // Other: rwx
            if (rwx[6] == 'r') octal += 4;
            if (rwx[7] == 'w') octal += 2;
            if (rwx[8] == 'x' || rwx[8] == 't') octal += 1;
            return octal;
        }

        /// <summary>八进制转 rwxrwxrwx 字符串</summary>
        public static string OctalToPermissionString(int octal)
        {
            var sb = new StringBuilder(9);
            sb.Append((octal & 256) != 0 ? 'r' : '-');
            sb.Append((octal & 128) != 0 ? 'w' : '-');
            sb.Append((octal & 64) != 0 ? 'x' : '-');
            sb.Append((octal & 32) != 0 ? 'r' : '-');
            sb.Append((octal & 16) != 0 ? 'w' : '-');
            sb.Append((octal & 8) != 0 ? 'x' : '-');
            sb.Append((octal & 4) != 0 ? 'r' : '-');
            sb.Append((octal & 2) != 0 ? 'w' : '-');
            sb.Append((octal & 1) != 0 ? 'x' : '-');
            return sb.ToString();
        }

        /// <summary>chmod 修改远程文件权限（符号模式）</summary>
        public static Task<bool> ChmodAsync(SshClient ssh, string remotePath, string permission)
        {
            if (ssh == null || !ssh.IsConnected) return Task.FromResult(false);
            // SEC-02: permission 白名单 + remotePath ShellQuote
            try
            {
                ShellArgument.ValidatePermission(permission);
            }
            catch (ArgumentException)
            {
                return Task.FromResult(false);
            }
            try
            {
                var cmd = ssh.RunCommand("chmod " + permission + " " + ShellArgument.ShellQuote(remotePath));
                return Task.FromResult(cmd.ExitStatus.GetValueOrDefault(-1) == 0);
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>chown 修改远程文件所有者</summary>
        public static Task<bool> ChownAsync(SshClient ssh, string remotePath, string owner, string group = null)
        {
            if (ssh == null || !ssh.IsConnected) return Task.FromResult(false);
            // SEC-02: owner/group 白名单 + remotePath ShellQuote
            string target;
            try
            {
                target = ShellArgument.ValidateOwner(owner, group);
            }
            catch (ArgumentException)
            {
                return Task.FromResult(false);
            }
            try
            {
                var cmd = ssh.RunCommand("chown " + target + " " + ShellArgument.ShellQuote(remotePath));
                return Task.FromResult(cmd.ExitStatus.GetValueOrDefault(-1) == 0);
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>获取文件权限详情（stat）</summary>
        public static FilePermissionInfo GetPermissionInfo(SshClient ssh, string remotePath)
        {
            if (ssh == null || !ssh.IsConnected) return null;
            try
            {
                // SEC-02: remotePath ShellQuote
                var cmd = ssh.RunCommand("stat -c '%a %U %G %F' " + ShellArgument.ShellQuote(remotePath));
                if (cmd.ExitStatus.GetValueOrDefault(-1) != 0) return null;

                var parts = cmd.Result.Trim().Split(new[] { ' ' }, 4);
                if (parts.Length < 4) return null;

                int octal;
                int.TryParse(parts[0], out octal);

                return new FilePermissionInfo
                {
                    OctalMode = octal,
                    PermissionString = OctalToPermissionString(octal),
                    Owner = parts[1],
                    Group = parts[2],
                    FileType = parts[3]
                };
            }
            catch { return null; }
        }
    }

    /// <summary>文件权限信息</summary>
    public class FilePermissionInfo
    {
        public int OctalMode { get; set; }
        public string PermissionString { get; set; }
        public string Owner { get; set; }
        public string Group { get; set; }
        public string FileType { get; set; }
    }
}
