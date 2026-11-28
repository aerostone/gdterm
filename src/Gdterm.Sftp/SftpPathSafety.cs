using System;
using System.IO;
using Gdterm.Core;

namespace Gdterm.Sftp
{
    /// <summary>远端名称按 Windows 单个文件名解释，拒绝路径穿越、设备名与重解析点。</summary>
    public static class SftpPathSafety
    {
        public static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.Length > 255
                || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                throw new IOException(L10n.T("文件名不能安全地保存到本地：") + name);
            foreach (char ch in name)
                if (ch < 32 || "<>:\"/\\|?*".IndexOf(ch) >= 0)
                    throw new IOException(L10n.T("文件名不能安全地保存到本地：") + name);
            var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".IndexOf(stem[3]) >= 0))
                throw new IOException(L10n.T("文件名不能安全地保存到本地：") + name);
        }

        public static string LocalChild(string root, string parent, string name)
        {
            ValidateName(name);
            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var child = Path.GetFullPath(Path.Combine(parent, name));
            if (!child.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L10n.T("下载路径超出目标目录。"));
            EnsureNoReparsePoints(child);
            return child;
        }

        public static void EnsureNoReparsePoints(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException(L10n.T("传输路径包含符号链接或重解析点：") + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
