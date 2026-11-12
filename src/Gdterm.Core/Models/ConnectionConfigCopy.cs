using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 连接配置深拷贝 + 副本命名。
    ///
    /// 为什么要单独一个类：连接树右键「创建副本」不能直接用原对象（改副本会连原连接一起改，
    /// 因为 Id/Name 之外全是引用共享的）。这里逐字段显式拷贝，配套的
    /// <c>ConnectionConfigCopyTests</c> 用反射把 ConnectionConfig 及其嵌套类型的全部可写属性
    /// 填上非默认值再比对——新增字段忘了拷贝会直接测试失败，不会静默丢配置。
    /// </summary>
    public static class ConnectionConfigCopy
    {
        /// <summary>深拷贝一份配置；Id 清空（由连接存储 Add 时重新生成），Name 由调用方给。</summary>
        public static ConnectionConfig Clone(ConnectionConfig source, string name)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var copy = new ConnectionConfig
            {
                Id = string.Empty,                       // Add() 会补新 Id
                Name = name ?? source.Name,
                Protocol = source.Protocol,
                Host = source.Host,
                Port = source.Port,
                Username = source.Username,
                Domain = source.Domain,
                GroupPath = source.GroupPath,
                CredentialRefId = source.CredentialRefId,
                JumpChain = CloneJumpChain(source.JumpChain),
                Tunnel = CloneTunnel(source.Tunnel),
                Proxy = CloneProxy(source.Proxy),
                Serial = CloneSerial(source.Serial),
                Metadata = CloneMetadata(source.Metadata)
            };
            return copy;
        }

        private static JumpChainConfig CloneJumpChain(JumpChainConfig src)
        {
            if (src == null) return null;
            var hops = new List<JumpHop>();
            if (src.Hops != null)
            {
                foreach (var hop in src.Hops)
                {
                    if (hop == null) { hops.Add(null); continue; }
                    hops.Add(new JumpHop
                    {
                        Host = hop.Host,
                        Port = hop.Port,
                        Username = hop.Username,
                        CredentialRefId = hop.CredentialRefId
                    });
                }
            }
            return new JumpChainConfig { Hops = hops };
        }

        private static TunnelConfig CloneTunnel(TunnelConfig src)
        {
            if (src == null) return null;
            return new TunnelConfig
            {
                Type = src.Type,
                LocalPort = src.LocalPort,
                RemoteHost = src.RemoteHost,
                RemotePort = src.RemotePort
            };
        }

        private static ProxyConfig CloneProxy(ProxyConfig src)
        {
            if (src == null) return null;
            return new ProxyConfig
            {
                Kind = src.Kind,
                Host = src.Host,
                Port = src.Port,
                Username = src.Username,
                Password = src.Password
            };
        }

        private static SerialConfig CloneSerial(SerialConfig src)
        {
            if (src == null) return null;
            return new SerialConfig
            {
                PortName = src.PortName,
                BaudRate = src.BaudRate,
                DataBits = src.DataBits,
                StopBits = src.StopBits,
                Parity = src.Parity,
                Handshake = src.Handshake,
                ReadTimeout = src.ReadTimeout,
                WriteTimeout = src.WriteTimeout,
                DtrEnable = src.DtrEnable,
                RtsEnable = src.RtsEnable,
                NewLine = src.NewLine
            };
        }

        private static Dictionary<string, string> CloneMetadata(Dictionary<string, string> src)
        {
            if (src == null) return null;
            return new Dictionary<string, string>(src, StringComparer.Ordinal);
        }

        /// <summary>
        /// 生成不与现有连接重名的副本名。后缀文案由调用方（UI 层）用 L10n 给：
        /// `firstSuffix` 形如 " (副本)"，`indexedFormat` 形如 " (副本 {0})"。
        /// </summary>
        public static string SuggestCopyName(string originalName, IEnumerable<string> existingNames,
            string unnamedFallback, string firstSuffix, string indexedFormat)
        {
            string baseName = string.IsNullOrWhiteSpace(originalName)
                ? (string.IsNullOrWhiteSpace(unnamedFallback) ? "Copy" : unnamedFallback.Trim())
                : originalName.Trim();
            string first = baseName + firstSuffix;
            if (existingNames == null) return first;

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in existingNames)
                if (!string.IsNullOrEmpty(n)) taken.Add(n);
            if (!taken.Contains(first)) return first;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + string.Format(indexedFormat, i);
                if (!taken.Contains(candidate)) return candidate;
            }
            return baseName + firstSuffix + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }
}
