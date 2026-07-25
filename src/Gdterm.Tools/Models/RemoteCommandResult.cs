using System;
using System.Collections.Generic;

namespace Gdterm.Tools.Models
{
    /// <summary>
    /// 远程命令执行结果
    /// </summary>
    public class RemoteCommandResult
    {
        /// <summary>执行的命令</summary>
        public string Command { get; set; }

        /// <summary>标准输出</summary>
        public string Stdout { get; set; }

        /// <summary>标准错误</summary>
        public string Stderr { get; set; }

        /// <summary>退出码</summary>
        public int ExitCode { get; set; }

        /// <summary>是否成功（ExitCode == 0）</summary>
        public bool IsSuccess { get { return ExitCode == 0; } }

        /// <summary>执行耗时</summary>
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// 远程文件传输接口
    /// </summary>
    public interface IRemoteFileTransfer : IDisposable
    {
        /// <summary>上传本地文件到远程临时目录</summary>
        string UploadToTemp(string localPath);

        /// <summary>上传字节数组到远程临时文件</summary>
        string UploadToTemp(byte[] content, string remoteFileName);

        /// <summary>清理远程临时文件</summary>
        void CleanupTemp(string remotePath);

        /// <summary>下载远程文件到本地</summary>
        void DownloadToFile(string remotePath, string localPath);
    }
}
