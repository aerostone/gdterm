using System;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// SSH 保活统一入口——SSH.NET 的 KeepAliveInterval 默认 -1（禁用），空闲连接会被中间设备掐掉。
    /// 取值：ConnectionConfig.Metadata["ssh_keepalive"] 秒数；缺省/非法 → 30s；"0" → 关闭。
    /// 适用全部建连点：终端直连/隧道、SFTP 直连/隧道、跳板 hop（TunnelSession）。
    /// </summary>
    public static class SshKeepAlive
    {
        public const int DefaultSeconds = 30;

        public static int ResolveSeconds(ConnectionConfig config)
        {
            if (config == null || config.Metadata == null) return DefaultSeconds;
            string raw;
            if (!config.Metadata.TryGetValue("ssh_keepalive", out raw)) return DefaultSeconds;
            int sec;
            if (!int.TryParse((raw ?? "").Trim(), out sec)) return DefaultSeconds;
            if (sec < 0) return DefaultSeconds;
            return sec;
        }

        public static void Apply(BaseClient client, int seconds)
        {
            if (client == null) return;
            int sec = seconds < 0 ? DefaultSeconds : seconds;
            try
            {
                client.KeepAliveInterval = sec <= 0
                    ? System.Threading.Timeout.InfiniteTimeSpan
                    : TimeSpan.FromSeconds(sec);
            }
            catch { /* 保活失败不阻断建连，连接本身仍可用 */ }
        }

        public static void Apply(BaseClient client, ConnectionConfig config)
        {
            Apply(client, ResolveSeconds(config));
        }
    }
}
