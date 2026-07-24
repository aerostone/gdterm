using System;

namespace Gdterm.Sftp.Models
{
    /// <summary>
    /// 远程文件/目录元数据
    /// </summary>
    public class SftpFileInfo
    {
        /// <summary>
        /// 文件/目录名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 完整远程路径
        /// </summary>
        public string FullPath { get; set; }

        /// <summary>
        /// 是否为目录
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// 文件大小（字节），目录为 0
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastModified { get; set; }

        /// <summary>
        /// 权限字符串（rwxrwxrwx 格式）
        /// </summary>
        public string Permissions { get; set; }

        /// <summary>
        /// 所有者
        /// </summary>
        public string Owner { get; set; }

        /// <summary>
        /// 所属组
        /// </summary>
        public string Group { get; set; }
    }
}
