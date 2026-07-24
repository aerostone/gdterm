using System;

namespace Gdterm.Rdp.Models
{
    /// <summary>
    /// RDP 状态变化事件参数
    /// </summary>
    public class RdpStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// 状态变化原因："connected" / "disconnected" / "error"
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// 错误信息（Reason="error" 时有值）
        /// </summary>
        public string ErrorMessage { get; set; }

        public RdpStateChangedEventArgs() { }

        public RdpStateChangedEventArgs(bool isConnected, string reason, string errorMessage = null)
        {
            IsConnected = isConnected;
            Reason = reason;
            ErrorMessage = errorMessage;
        }
    }
}
