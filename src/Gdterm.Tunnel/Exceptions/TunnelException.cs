using System;

namespace Gdterm.Tunnel.Exceptions
{
    /// <summary>
    /// 隧道建立或使用过程中的异常
    /// </summary>
    public class TunnelException : Exception
    {
        /// <summary>
        /// 失败发生的 Hop 索引（0-based）。-1 表示非跳板相关错误
        /// </summary>
        public int HopIndex { get; set; }

        /// <summary>
        /// 失败时正在连接的主机
        /// </summary>
        public string Host { get; set; }

        /// <summary>
        /// 失败时正在连接的端口
        /// </summary>
        public int Port { get; set; }

        public TunnelException()
        {
            HopIndex = -1;
        }

        public TunnelException(string message) : base(message)
        {
            HopIndex = -1;
        }

        public TunnelException(string message, Exception innerException) : base(message, innerException)
        {
            HopIndex = -1;
        }

        public TunnelException(string message, int hopIndex, string host, int port, Exception innerException = null)
            : base(message, innerException)
        {
            HopIndex = hopIndex;
            Host = host;
            Port = port;
        }

        public override string ToString()
        {
            var hopInfo = HopIndex >= 0 ? $" [Hop {HopIndex}: {Host}:{Port}]" : "";
            return $"{Message}{hopInfo}{(InnerException != null ? $" -> {InnerException.Message}" : "")}";
        }
    }
}
