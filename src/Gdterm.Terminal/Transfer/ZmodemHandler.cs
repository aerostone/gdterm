using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gdterm.Terminal.Transfer
{
    /// <summary>
    /// Zmodem 文件传输处理器——检测并处理 rz/sz 文件传输
    /// Zmodem 协议使用特定的转义序列来标识传输开始/结束
    /// </summary>
    public class ZmodemHandler
    {
        // Zmodem 协议常量
        private const byte ZPAD = 0x2A;     // '*'
        private const byte ZDLE = 0x18;     // CAN
        private const byte ZBIN = 0x41;     // 'A'
        private const byte ZHEX = 0x42;     // 'B'
        private const byte ZBIN32 = 0x43;   // 'C'

        // Zmodem 帧类型
        private const byte ZRQINIT = 0;      // 请求初始化
        private const byte ZRINIT = 1;       // 接收端就绪
        private const byte ZSINIT = 2;       // 发送端初始化
        private const byte ZACK = 3;         // 确认
        private const byte ZFILE = 4;        // 文件信息
        private const byte ZSKIP = 5;        // 跳过文件
        private const byte ZNAK = 6;         // 否定确认
        private const byte ZABORT = 7;       // 中止
        private const byte ZFIN = 8;         // 完成
        private const byte ZRPOS = 9;        // 重传位置
        private const byte ZDATA = 10;       // 数据
        private const byte ZEOF = 11;        // 文件结束

        /// <summary>
        /// 文件传输开始事件
        /// </summary>
        public event EventHandler<ZmodemFileEventArgs> TransferStarted;

        /// <summary>
        /// 文件传输进度事件
        /// </summary>
        public event EventHandler<ZmodemProgressEventArgs> TransferProgress;

        /// <summary>
        /// 文件传输完成事件
        /// </summary>
        public event EventHandler<ZmodemFileEventArgs> TransferCompleted;

        /// <summary>
        /// 文件传输错误事件
        /// </summary>
        public event EventHandler<ZmodemErrorEventArgs> TransferError;

        /// <summary>
        /// 是否正在传输
        /// </summary>
        public bool IsTransferring { get; private set; }

        private string _currentFileName;
        private long _currentFileSize;
        private long _bytesTransferred;
        private string _downloadDirectory;

        /// <summary>
        /// 设置下载目录
        /// </summary>
        public void SetDownloadDirectory(string directory)
        {
            _downloadDirectory = directory;
        }

        /// <summary>
        /// 检测数据中是否包含 Zmodem 启动序列
        /// Zmodem 启动序列：**\x18B00
        /// </summary>
        public bool DetectZmodemStart(byte[] data, int offset, int count)
        {
            if (count < 5) return false;

            for (int i = offset; i < offset + count - 4; i++)
            {
                // 检测 **\x18B0 (Zmodem ZRQINIT)
                if (data[i] == ZPAD && data[i + 1] == ZPAD &&
                    data[i + 2] == ZDLE && data[i + 3] == ZHEX)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 从终端输出中提取 Zmodem 文件信息
        /// sz 命令输出格式：**\x04B09000000000000000000000000000\r\n**\x18B04...
        /// </summary>
        public ZmodemFileInfo ParseFileInfo(byte[] data, int offset, int count)
        {
            try
            {
                // 查找 ZFILE 帧
                for (int i = offset; i < offset + count - 20; i++)
                {
                    if (data[i] == ZPAD && data[i + 1] == ZPAD &&
                        data[i + 2] == ZDLE && data[i + 3] == ZHEX &&
                        i + 10 < offset + count)
                    {
                        // 解析帧类型
                        byte frameType = data[i + 9];
                        if (frameType == ZFILE)
                        {
                            return ExtractFileMetadata(data, i + 10, offset + count);
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 开始接收文件（调用 rz 命令后）
        /// </summary>
        public void BeginReceive(string fileName, long fileSize)
        {
            _currentFileName = fileName;
            _currentFileSize = fileSize;
            _bytesTransferred = 0;
            IsTransferring = true;

            TransferStarted?.Invoke(this, new ZmodemFileEventArgs
            {
                FileName = fileName,
                FileSize = fileSize
            });
        }

        /// <summary>
        /// 更新传输进度
        /// </summary>
        public void UpdateProgress(long bytesTransferred)
        {
            _bytesTransferred = bytesTransferred;

            TransferProgress?.Invoke(this, new ZmodemProgressEventArgs
            {
                FileName = _currentFileName,
                FileSize = _currentFileSize,
                BytesTransferred = bytesTransferred,
                Percentage = _currentFileSize > 0 ? (double)bytesTransferred / _currentFileSize * 100 : 0
            });
        }

        /// <summary>
        /// 完成传输
        /// </summary>
        public void CompleteTransfer()
        {
            IsTransferring = false;

            TransferCompleted?.Invoke(this, new ZmodemFileEventArgs
            {
                FileName = _currentFileName,
                FileSize = _currentFileSize
            });

            _currentFileName = null;
            _currentFileSize = 0;
            _bytesTransferred = 0;
        }

        /// <summary>
        /// 传输错误
        /// </summary>
        public void ReportError(string error)
        {
            IsTransferring = false;

            TransferError?.Invoke(this, new ZmodemErrorEventArgs
            {
                FileName = _currentFileName,
                Error = error
            });

            _currentFileName = null;
            _currentFileSize = 0;
            _bytesTransferred = 0;
        }

        /// <summary>
        /// 生成 sz 命令（发送文件到远程）
        /// </summary>
        public static string GenerateSendCommand(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            var fileName = Path.GetFileName(filePath);
            // sz 命令：发送文件到远程终端
            return $"sz \"{fileName}\"";
        }

        /// <summary>
        /// 生成 rz 命令（从远程接收文件）
        /// </summary>
        public static string GenerateReceiveCommand()
        {
            return "rz";
        }

        private ZmodemFileInfo ExtractFileMetadata(byte[] data, int start, int end)
        {
            var info = new ZmodemFileInfo();

            // 文件名以 null 结尾
            var nameEnd = Array.IndexOf(data, (byte)0, start, end - start);
            if (nameEnd > start)
            {
                info.FileName = Encoding.UTF8.GetString(data, start, nameEnd - start);

                // 文件大小在文件名之后
                var sizeStart = nameEnd + 1;
                if (sizeStart < end)
                {
                    var sizeEnd = Array.IndexOf(data, (byte)0, sizeStart, end - sizeStart);
                    if (sizeEnd < 0) sizeEnd = end;
                    var sizeStr = Encoding.UTF8.GetString(data, sizeStart, sizeEnd - sizeStart);
                    long.TryParse(sizeStr.Split(' ')[0], out long size);
                    info.FileSize = size;
                }
            }

            return info;
        }
    }

    /// <summary>
    /// Zmodem 文件信息
    /// </summary>
    public class ZmodemFileInfo
    {
        public string FileName { get; set; }
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Zmodem 文件传输事件参数
    /// </summary>
    public class ZmodemFileEventArgs : EventArgs
    {
        public string FileName { get; set; }
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Zmodem 传输进度事件参数
    /// </summary>
    public class ZmodemProgressEventArgs : EventArgs
    {
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public long BytesTransferred { get; set; }
        public double Percentage { get; set; }
    }

    /// <summary>
    /// Zmodem 错误事件参数
    /// </summary>
    public class ZmodemErrorEventArgs : EventArgs
    {
        public string FileName { get; set; }
        public string Error { get; set; }
    }
}
