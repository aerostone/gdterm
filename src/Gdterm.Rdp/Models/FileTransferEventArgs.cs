using System;

namespace Gdterm.Rdp.Models
{
    /// <summary>
    /// 文件传输事件参数
    /// </summary>
    public class FileTransferEventArgs : EventArgs
    {
        /// <summary>
        /// 源文件路径
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// 目标文件路径
        /// </summary>
        public string DestinationPath { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSizeBytes { get; set; }

        /// <summary>
        /// 是否成功
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// 错误信息
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
