using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Gdterm.Logging;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 诊断包导出（nyaterm 对比吸收 A7）：一键 zip，报障时发给维护者即可。
    /// 内容：版本/环境信息 + 脱敏日志（diag + 近期 terminal）+ 脱敏配置（data/config 下 JSON）。
    /// 全部文本经 LogSanitizer 脱敏；密码明文、凭据库（.kdbx）、命令历史目录（可能含粘贴的密钥）
    /// 一律不收集。logs/commands 与 .kdbx 被有意排除，README.txt 会说明。
    /// </summary>
    public static class DiagnosticsPackageExporter
    {
        /// <summary>单个日志文件最多收多少字节（取尾部——最新的内容在最下面）。</summary>
        public const int MaxFileBytes = 512 * 1024;

        /// <summary>zip 内条目总数上限（防御：异常目录膨胀）。</summary>
        public const int MaxEntryCount = 100;

        /// <summary>生成诊断包；用户取消返回 null。成功返回 zip 路径。</summary>
        public static string BuildPackage(IWin32Window owner)
        {
            string target = null;
            using (var dlg = new SaveFileDialog
            {
                Title = "导出诊断包",
                FileName = "gdterm-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                Filter = "ZIP 压缩包 (*.zip)|*.zip"
            })
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                target = dlg.FileName;
            }

            var sanitizer = new LogSanitizer();
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logsDir = Program.LogsDir;
            var dataDir = Path.Combine(baseDir, "data");

            try
            {
                if (File.Exists(target)) File.Delete(target);
                var written = new List<string>();

                using (var fs = File.Create(target))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddTextEntry(zip, "meta/version.txt", BuildVersionText());
                    AddTextEntry(zip, "meta/README.txt", BuildReadmeText());
                    written.Add("meta/version.txt");
                    written.Add("meta/README.txt");

                    AddSanitizedFile(zip, sanitizer,
                        Path.Combine(logsDir, "diag.log"), "logs/diag.log", written);
                    AddRecentLogDir(zip, sanitizer,
                        Path.Combine(logsDir, "terminal"), "logs/terminal", 10, written);
                    AddConfigDir(zip, sanitizer,
                        Path.Combine(dataDir, "config"), "config", written);

                    if (written.Count <= 2)
                        throw new Exception("没有可收集的内容（logs/data 目录为空？）");
                }
                return target;
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("DiagnosticsPackageExporter", ex); } catch { /* 诊断路径不二次抛 */ }
                MessageBox.Show(owner, "导出诊断包失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        // ========== 内容生成 ==========

        private static string BuildVersionText()
        {
            var asm = typeof(DiagnosticsPackageExporter).Assembly;
            var v = asm.GetName().Version;
            var info = Attribute.GetCustomAttribute(asm,
                typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                as System.Reflection.AssemblyInformationalVersionAttribute;

            var sb = new StringBuilder();
            sb.AppendLine("gdterm 诊断包");
            sb.AppendLine("生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("版本: " + (v != null ? v.ToString() : "?"));
            if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                sb.AppendLine("构建: " + info.InformationalVersion);
            sb.AppendLine("系统: " + Environment.OSVersion.VersionString);
            sb.AppendLine("位数: " + (Environment.Is64BitProcess ? "x64" : "x86"));
            sb.AppendLine("CLR: " + Environment.Version);
            sb.AppendLine("CPU: " + Environment.ProcessorCount + " 核");
            sb.AppendLine("区域: " + (System.Globalization.CultureInfo.CurrentUICulture.Name));
            return sb.ToString();
        }

        private static string BuildReadmeText()
        {
            return
                "gdterm 诊断包说明\r\n" +
                "=================\r\n" +
                "本包由 gdterm「帮助 → 导出诊断包」生成，所有文本均已自动脱敏\r\n" +
                "（密码/token/密钥类模式替换为 ***）。\r\n\r\n" +
                "包含：\r\n" +
                "  meta/version.txt   版本与环境信息\r\n" +
                "  logs/diag.log      主诊断日志（尾部 " + MaxFileBytes / 1024 + "KB）\r\n" +
                "  logs/terminal/     近期终端会话日志（最新 10 个，各尾部 " + MaxFileBytes / 1024 + "KB）\r\n" +
                "  config/            data/config 下的 JSON 配置（脱敏后）\r\n\r\n" +
                "有意排除（敏感）：\r\n" +
                "  - 密码/凭据库文件（.kdbx 等）\r\n" +
                "  - logs/commands（命令历史，可能含粘贴的密钥）\r\n" +
                "  - 连接配置中的密码字段（gdterm 本身不落盘密码）\r\n";
        }

        // ========== 收集 ==========

        private static void AddTextEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(content);
        }

        /// <summary>加一个脱敏后的文件（超长取尾部）；文件不存在则静默跳过。</summary>
        private static void AddSanitizedFile(ZipArchive zip, LogSanitizer sanitizer,
            string filePath, string entryName, List<string> written)
        {
            if (written.Count >= MaxEntryCount) return;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                var text = ReadTailText(filePath);
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    w.Write(sanitizer.Sanitize(text));
                written.Add(entryName);
            }
            catch (Exception ex)
            {
                try { DiagLog.Swallowed("DiagnosticsPackageExporter", ex); } catch { }
            }
        }

        /// <summary>目录下最新 n 个 .log，逐个收。</summary>
        private static void AddRecentLogDir(ZipArchive zip, LogSanitizer sanitizer,
            string dir, string entryPrefix, int maxCount, List<string> written)
        {
            if (!Directory.Exists(dir) || maxCount <= 0) return;
            IEnumerable<FileInfo> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(maxCount);
            }
            catch { return; }

            foreach (var f in files)
                AddSanitizedFile(zip, sanitizer, f.FullName,
                    entryPrefix + "/" + f.Name, written);
        }

        /// <summary>
        /// data/config 下的 JSON 配置（递归，脱敏）。跳过凭据/密钥类文件名与超大文件。
        /// </summary>
        private static void AddConfigDir(ZipArchive zip, LogSanitizer sanitizer,
            string dir, string entryPrefix, List<string> written)
        {
            if (!Directory.Exists(dir)) return;
            IEnumerable<FileInfo> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                    .Select(f => new FileInfo(f));
            }
            catch { return; }

            foreach (var f in files)
            {
                if (written.Count >= MaxEntryCount) return;
                var name = f.Name.ToLowerInvariant();
                if (name.Contains("credential") || name.Contains("secret") || name.Contains("key"))
                    continue; // 文件名带敏感词的一律不收
                if (f.Length > 1024 * 1024) continue;
                try
                {
                    var rel = f.FullName.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    var text = File.ReadAllText(f.FullName);
                    var entry = zip.CreateEntry(entryPrefix + "/" + rel, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        w.Write(sanitizer.Sanitize(text));
                    written.Add(entryPrefix + "/" + rel);
                }
                catch (Exception ex)
                {
                    try { DiagLog.Swallowed("DiagnosticsPackageExporter", ex); } catch { }
                }
            }
        }

        /// <summary>读文件尾部（UTF-8，失败回退 GBK）；超长只取最后 MaxFileBytes 字节。</summary>
        internal static string ReadTailText(string filePath)
        {
            var len = new FileInfo(filePath).Length;
            byte[] bytes;
            using (var fs = File.OpenRead(filePath))
            {
                long take = Math.Min(len, MaxFileBytes);
                if (take < len) fs.Seek(len - take, SeekOrigin.Begin);
                bytes = new byte[take];
                int read = 0;
                while (read < take)
                {
                    int n = fs.Read(bytes, read, (int)(take - read));
                    if (n <= 0) break;
                    read += n;
                }
                Array.Resize(ref bytes, read);
            }
            try
            {
                var strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes);
            }
            catch (ArgumentException)
            {
                try { return Encoding.GetEncoding(936).GetString(bytes); }
                catch { return Encoding.Default.GetString(bytes); }
            }
        }
    }
}
