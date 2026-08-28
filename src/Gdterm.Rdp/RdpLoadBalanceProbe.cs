using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Gdterm.Rdp
{
    /// <summary>
    /// RDP 负载均衡路由 token 预协商。
    ///
    /// 背景（MS-RDPBCGR 2.2.1.2 Load Balancing，配合 [MSFT-SDLBTS]）：
    /// RDP 服务常部署在 Citrix NetScaler 等负载均衡网关之后。客户端首次连接时，
    /// 网关在 X.224 Connection Confirm PDU 的「变长部分（variable part）」中返回一个
    /// routing token（形如 "Cookie: msts=NSFVERIFYHASH=…\r\n"）。客户端必须在下一次
    /// X.224 Connection Request 中把该 token 回传（即 wfreerdp 的 /load-balance-info:），
    /// 网关才能把连接粘滞（sticky）到同一台后端服务器；否则会话会被踢掉
    /// （ERRCONNECT_CONNECT_TRANSPORT_FAILED → ERRINFO_LOGOFF_BY_USER 循环）。
    ///
    /// 本类在真正启动 wfreerdp 之前，先以客户端身份完成一次轻量 X.224 预协商，
    /// 把「获取 routing token」当作连接流程的一个步骤（而非去监听 wfreerdp 的调试日志）：
    ///   TCP 连接 → 发送 Class 0 X.224 Connection Request PDU（含 RDP Negotiation Request）
    ///   → 读取 Connection Confirm PDU → 从变长部分解析 routing token → 关闭 socket。
    ///
    /// 拿到 token 后，上层把它写入 RdpOptions.LoadBalanceInfo，wfreerdp 首连即带上
    /// /load-balance-info，用户只看到一次连接成功，没有「首次被踢」的中间状态。
    ///
    /// 若目标无负载均衡（Connection Confirm 的 li==6 无 rdpNegData、或无 routing token）、
    /// 或预协商超时/失败，则返回 null，上层照常走普通 wfreerdp 连接路径，不影响非 LB 环境。
    /// </summary>
    internal static class RdpLoadBalanceProbe
    {
        private const int DefaultTimeoutMs = 3000;
        private const byte TypeRdpNegReq = 0x01;
        // wire requestedProtocols 是「位掩码」而非 FreeRDP 内部 enum 值（见 nego.h）。
        // MS-RDPBCGR 2.2.1.1.1：bit0=RDP(0x01)、bit1=SSL/TLS(0x02)、bit2=HYBRID/NLA(0x04)。
        // 请求 NLA+TLS+RDP 三路兜底，让 NetScaler 网关按标准协商返回带 routing token 的 CC。
        private const uint ProtoRdp = 0x00000001;
        private const uint ProtoSsl = 0x00000002;
        private const uint ProtoHybrid = 0x00000004;

        private static readonly byte[] RoutingTokenPrefix = Encoding.ASCII.GetBytes("Cookie: msts=");
        private static readonly byte[] CookiePrefix = Encoding.ASCII.GetBytes("Cookie: mstshash=");

        /// <summary>
        /// 最近一次 Probe 的失败原因（"ok:token" / "cc:no-token" / "timeout" / …），
        /// 供上层落盘诊断探针时灵时不灵的问题；无诊断意义时不记录。
        /// </summary>
        public static string LastProbeDetail = "not-run";

        /// <summary>
        /// 对 host:port 执行一次 X.224 预协商并返回 routing token；无 token / 失败返回 null。
        /// </summary>
        public static string Probe(string host, int port, int timeoutMs = DefaultTimeoutMs)
        {
            if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535)
            {
                LastProbeDetail = "bad-endpoint";
                return null;
            }
            try
            {
                return ProbeCore(host, port, timeoutMs);
            }
            catch (Exception ex)
            {
                LastProbeDetail = "exception:" + ex.GetType().Name;
                return null; // 预协商失败不阻断正常连接
            }
        }

        private static string ProbeCore(string host, int port, int timeoutMs)
        {
            using (var client = new TcpClient())
            {
                var connectTask = client.ConnectAsync(host, port);
                if (!connectTask.Wait(timeoutMs) || !client.Connected)
                {
                    LastProbeDetail = "connect-timeout";
                    return null;
                }

                using (var stream = client.GetStream())
                {
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;

                    // Class 0 X.224 Connection Request PDU + RDP Negotiation Request（MS-RDPBCGR 2.2.1.1）
                    // 请求 NLA+TLS（含 legacy RDP 兜底），让网关按标准协商返回 CC。
                    var cr = BuildConnectionRequest();
                    stream.Write(cr, 0, cr.Length);

                    // 读 TPKT 头（version + length），再按长度读完整 PDU。
                    var head = new byte[4];
                    if (!ReadExactly(stream, head, 0, 4))
                    {
                        LastProbeDetail = "read-head-eof";
                        return null;
                    }
                    if (head[0] != 0x03)
                    {
                        LastProbeDetail = "not-tpkt:" + head[0];
                        return null;
                    } // TPKT version 3
                    int length = (head[2] << 8) | head[3];
                    if (length < 5 || length > 0x4000)
                    {
                        LastProbeDetail = "bad-length:" + length;
                        return null;
                    } // 合理边界，防畸形长度

                    var pdu = new byte[length];
                    head.CopyTo(pdu, 0);
                    if (!ReadExactly(stream, pdu, 4, length - 4))
                    {
                        LastProbeDetail = "read-pdu-eof";
                        return null;
                    }

                    var token = ExtractRoutingToken(pdu);
                    LastProbeDetail = token != null ? "ok:token-len-" + token.Length : "cc:no-token";
                    return token;
                }
            }
        }

        /// <summary>
        /// 从 Connection Confirm PDU 中提取 routing token。
        /// 布局（MS-RDPBCGR 2.2.1.2）：
        ///   TPKT(4) | LI(1) | CC type 0xD0(1) | DST-REF(2) | SRC-REF(2) | CLASS(1)
        ///   | [可变：routingToken / cookie] | [可变：rdpNegData(8 bytes)]
        /// </summary>
        private static string ExtractRoutingToken(byte[] pdu)        {
            int pos = 4; // 跳过 TPKT 头
            if (pos + 1 > pdu.Length) return null;
            int li = pdu[pos]; pos += 1;

            // Connection Confirm 固定头：type(1) + dst(2) + src(2) + class(1) = 6
            if (li < 6 || pos + 6 > pdu.Length) return null;
            if (pdu[pos] != 0xD0) return null; // 必须是 Connection Confirm
            pos += 6;

            // 变长部分从 pos 开始：先扫描 routing token / cookie，再处理 rdpNegData。
            var token = ExtractTokenFromTail(pdu, pos);
            return token;
        }

        /// <summary>
        /// 在 CC 变长部分（从 offset 开始）扫描 "Cookie: msts=…"/"Cookie: mstshash=…"，
        /// 读取到 CRLF（0x0D 0x0A）为止。routing token 与 cookie 互斥，且都先于 rdpNegData。
        /// </summary>
        private static string ExtractTokenFromTail(byte[] pdu, int offset)
        {
            int i = offset;
            int max = pdu.Length - RoutingTokenPrefix.Length;
            while (i <= max)
            {
                // 找到 Cookie 起点
                if (MatchesPrefix(pdu, i, CookiePrefix.Length, RoutingTokenPrefix, CookiePrefix))
                {
                    int start = i; // Cookie: 开头
                    int end = FindCrlf(pdu, i);
                    if (end > start)
                    {
                        // 排除 CRLF 本身，返回 ANSI 字符串
                        return Encoding.ASCII.GetString(pdu, start, end - start);
                    }
                }
                i++;
            }
            return null;
        }

        /// <summary>判断 pdu[i..] 是否以 "Cookie: msts=" 或 "Cookie: mstshash=" 开头。</summary>
        private static bool MatchesPrefix(byte[] pdu, int i, int maxLen, byte[] tokenPrefix, byte[] cookiePrefix)
        {
            if (i + cookiePrefix.Length <= pdu.Length && StartsWith(pdu, i, cookiePrefix))
                return true;
            return i + tokenPrefix.Length <= pdu.Length && StartsWith(pdu, i, tokenPrefix);
        }

        private static bool StartsWith(byte[] pdu, int i, byte[] prefix)
        {
            for (int k = 0; k < prefix.Length; k++)
                if (pdu[i + k] != prefix[k]) return false;
            return true;
        }

        /// <summary>从 i 起查找 0x0D 0x0A（CRLF），返回 CR 的位置；未找到返回 -1。</summary>
        private static int FindCrlf(byte[] pdu, int i)
        {
            for (int k = i; k + 1 < pdu.Length; k++)
                if (pdu[k] == 0x0D && pdu[k + 1] == 0x0A) return k;
            return -1;
        }

        /// <summary>
        /// 构造 X.224 Connection Request PDU。
        /// 布局同 FreeRDP nego_send_negotiation_request：
        ///   TPKT: 03 00 | len(=19)  → head = [0x03,0x00,0x00,0x13]
        ///   X.224: LI(0x0E=14) | CR type 0xE0 | DST-REF(0) | SRC-REF(0) | CLASS(0)
        ///   RDP Negotiation Request: type 0x01 | flags 0x00 | length 0x0008 | requestedProtocols
        /// </summary>
        private static byte[] BuildConnectionRequest()
        {
            const byte li = 6 + 8;           // 固定头(6) + rdpNegData(8)
            const byte tpktLen = 4 + 1 + li; // TPKT 头(4) + LI(1) + 变长(14) = 19
            // 三路兜底：RDP(0x01) | TLS(0x02) | NLA(0x04) = 0x07。
            // 旧代码误用 FreeRDP 内部 enum 值（Hybrid|Ssl=0x03），在 wire 上丢失了 RDP 位，
            // 且与 NetScaler 预期的位掩码语义不符，导致预协商拿不到 routing token（loadBalanceInfo=<none>）。
            const uint requestedProtocols = ProtoRdp | ProtoSsl | ProtoHybrid;

            var req = new byte[tpktLen];
            req[0] = 0x03;                    // TPKT version
            req[1] = 0x00;                    // reserved
            req[2] = (byte)(tpktLen >> 8);    // length hi
            req[3] = (byte)(tpktLen & 0xFF);  // length lo
            req[4] = li;                      // X.224 LI
            req[5] = 0xE0;                    // X.224 Connection Request
            req[6] = 0x00; req[7] = 0x00;     // DST-REF
            req[8] = 0x00; req[9] = 0x00;     // SRC-REF
            req[10] = 0x00;                   // Class 0
            req[11] = TypeRdpNegReq;          // 0x01
            req[12] = 0x00;                   // flags
            req[13] = 0x00; req[14] = 0x08;   // length = 8
            // requestedProtocols（小端写入，与 FreeRDP Stream_Write_UINT32 一致）
            req[15] = (byte)(requestedProtocols & 0xFF);
            req[16] = (byte)((requestedProtocols >> 8) & 0xFF);
            req[17] = (byte)((requestedProtocols >> 16) & 0xFF);
            req[18] = (byte)((requestedProtocols >> 24) & 0xFF);
            return req;
        }

        private static bool ReadExactly(NetworkStream stream, byte[] buf, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, offset + read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }
}