using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 双栏文件面板的统一条目模型——本地 (FileSystemInfo) 与远端 (SftpFileInfo) 归一。
    /// </summary>
    internal sealed class FileEntry
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
        public long SizeBytes;
        public DateTime LastModified;
        public string Permissions;
    }

    /// <summary>
    /// 文件面板数据源抽象——本地文件系统与 SFTP 远端各一个实现，
    /// 使 FilePaneControl 的浏览/管理 UI 与方向无关。
    /// 全部方法在调用方 Task.Run 中执行（实现体是同步 IO），不要在 UI 线程直接调。
    /// </summary>
    internal interface IFilePaneProvider
    {
        /// <summary>面板标题（"本地" / 主机名）</summary>
        string Title { get; }

        /// <summary>主目录（本地=用户目录，远端=/）</summary>
        string HomePath { get; }

        /// <summary>列出目录（目录优先 + 名称排序；空 path=本地盘符伪根）</summary>
        List<FileEntry> List(string path);

        /// <summary>父目录；null=已在根</summary>
        string ParentOf(string path);

        /// <summary>删除（目录递归）</summary>
        void Delete(string path, bool isDirectory);

        /// <summary>新建目录</summary>
        void Mkdir(string path);

        /// <summary>重命名/移动（同侧）</summary>
        void Rename(string oldPath, string newPath, bool isDirectory);

        /// <summary>拼接子路径（处理两侧分隔符差异）</summary>
        string Combine(string basePath, string name);
    }

    /// <summary>本地文件系统面板数据源。path=="" 表示"此电脑"盘符伪根。</summary>
    internal sealed class LocalFilePaneProvider : IFilePaneProvider
    {
        public string Title { get { return "本地"; } }

        public string HomePath
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home) ? "C:\\" : home;
            }
        }

        public List<FileEntry> List(string path)
        {
            var result = new List<FileEntry>();
            if (string.IsNullOrEmpty(path))
            {
                // 盘符伪根
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    result.Add(new FileEntry
                    {
                        Name = string.IsNullOrEmpty(drive.VolumeLabel)
                            ? drive.Name
                            : drive.VolumeLabel + " (" + drive.RootDirectory.FullName.TrimEnd('\\') + ")",
                        FullPath = drive.RootDirectory.FullName,
                        IsDirectory = true,
                        SizeBytes = 0,
                        LastModified = drive.RootDirectory.LastWriteTime,
                        Permissions = ""
                    });
                }
                return result;
            }

            var dir = new DirectoryInfo(path);
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                try
                {
                    var isDir = (e.Attributes & FileAttributes.Directory) != 0;
                    result.Add(new FileEntry
                    {
                        Name = e.Name,
                        FullPath = e.FullName,
                        IsDirectory = isDir,
                        SizeBytes = isDir ? 0 : ((FileInfo)e).Length,
                        LastModified = e.LastWriteTime,
                        Permissions = ""
                    });
                }
                catch { /* 单项不可读跳过（权限/句柄占用） */ }
            }

            result.Sort(CompareEntries);
            return result;
        }

        public string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;      // 伪根之上无父
            var parent = Directory.GetParent(path);
            return parent == null ? "" : parent.FullName;    // 盘符根的父=伪根
        }

        public void Delete(string path, bool isDirectory)
        {
            if (isDirectory) Directory.Delete(path, true);
            else File.Delete(path);
        }

        public void Mkdir(string path)
        {
            Directory.CreateDirectory(path);
        }

        public void Rename(string oldPath, string newPath, bool isDirectory)
        {
            if (isDirectory) Directory.Move(oldPath, newPath);
            else File.Move(oldPath, newPath);
        }

        public string Combine(string basePath, string name)
        {
            return Path.Combine(basePath, name);
        }

        internal static int CompareEntries(FileEntry a, FileEntry b)
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>SFTP 远端面板数据源——薄封装 ISftpService（连接由双栏宿主负责）。</summary>
    internal sealed class SftpFilePaneProvider : IFilePaneProvider
    {
        private readonly Gdterm.Sftp.ISftpService _sftp;
        private readonly string _title;

        public SftpFilePaneProvider(Gdterm.Sftp.ISftpService sftp, string title)
        {
            _sftp = sftp;
            _title = string.IsNullOrEmpty(title) ? "远程" : title;
        }

        public string Title { get { return _title; } }

        public string HomePath { get { return "/"; } }

        public List<FileEntry> List(string path)
        {
            var items = _sftp.ListDirectoryAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            var result = new List<FileEntry>();
            foreach (var f in items)
            {
                if (f.Name == "." || f.Name == "..") continue;
                result.Add(new FileEntry
                {
                    Name = f.Name,
                    FullPath = f.FullPath,
                    IsDirectory = f.IsDirectory,
                    SizeBytes = f.SizeBytes,
                    LastModified = f.LastModified,
                    Permissions = f.Permissions ?? ""
                });
            }
            result.Sort(LocalFilePaneProvider.CompareEntries);
            return result;
        }

        public string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return null;
            var p = path.TrimEnd('/');
            var idx = p.LastIndexOf('/');
            return idx <= 0 ? "/" : p.Substring(0, idx);
        }

        public void Delete(string path, bool isDirectory)
        {
            _sftp.DeleteAsync(path, isDirectory, CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Mkdir(string path)
        {
            _sftp.CreateDirectoryAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        }

        public void Rename(string oldPath, string newPath, bool isDirectory)
        {
            _sftp.RenameAsync(oldPath, newPath, CancellationToken.None).GetAwaiter().GetResult();
        }

        public string Combine(string basePath, string name)
        {
            if (string.IsNullOrEmpty(basePath) || basePath == "/") return "/" + name.TrimStart('/');
            return basePath.TrimEnd('/') + "/" + name.TrimStart('/');
        }
    }
}
