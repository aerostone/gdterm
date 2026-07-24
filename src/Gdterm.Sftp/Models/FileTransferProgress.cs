using System;

namespace Gdterm.Sftp.Models
{
    /// <summary>
    /// 文件传输进度信息
    /// </summary>
    public class FileTransferProgress
    {
        /// <summary>
        /// 已传输字节数
        /// </summary>
        public long BytesTransferred { get; set; }

        /// <summary>
        /// 总字节数
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// 传输百分比（0-100）
        /// </summary>
        public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;

        /// <summary>
        /// 已用时间
        /// </summary>
        public TimeSpan Elapsed { get; set; }
    }
}
