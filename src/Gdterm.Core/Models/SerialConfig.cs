using System.IO.Ports;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 串口连接配置
    /// </summary>
    public class SerialConfig
    {
        /// <summary>
        /// 串口名称（COM1, COM2, /dev/ttyS0 等）
        /// </summary>
        public string PortName { get; set; }

        /// <summary>
        /// 波特率（默认 9600）
        /// </summary>
        public int BaudRate { get; set; } = 9600;

        /// <summary>
        /// 数据位（5-8，默认 8）
        /// </summary>
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// 停止位
        /// </summary>
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// 校验位
        /// </summary>
        public Parity Parity { get; set; } = Parity.None;

        /// <summary>
        /// 流控制
        /// </summary>
        public Handshake Handshake { get; set; } = Handshake.None;

        /// <summary>
        /// 读取超时（毫秒）
        /// </summary>
        public int ReadTimeout { get; set; } = 500;

        /// <summary>
        /// 写入超时（毫秒）
        /// </summary>
        public int WriteTimeout { get; set; } = 500;

        /// <summary>
        /// DTR 启用
        /// </summary>
        public bool DtrEnable { get; set; }

        /// <summary>
        /// RTS 启用
        /// </summary>
        public bool RtsEnable { get; set; }

        /// <summary>
        /// 新行字符（用于 ReadLine）
        /// </summary>
        public string NewLine { get; set; } = "\r\n";
    }
}
