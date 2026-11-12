using System;
using System.Collections.Generic;
using System.Reflection;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;

namespace Gdterm.Tests.Core
{
    /// <summary>
    /// 连接树「创建副本」的数据层验收：深拷贝必须逐字段等价，且不能与原对象共享引用。
    ///
    /// 反射兜底是刻意的：ConnectionConfig 每加一个字段（历史上跳过 Tunnel / Proxy / Serial），
    /// 手写 Clone 很容易漏。这里要求"样本对象把每个可写属性都填成非默认值"——
    /// 新字段没进样本会立刻失败，逼着同时更新样本与 Clone。
    /// </summary>
    public static class ConnectionConfigCopyTests
    {
        public static void Run()
        {
            Console.WriteLine("-- ConnectionConfigCopy --");
            DeepCopyIsValueEqual();
            DeepCopyDoesNotAlias();
            CopyNameAvoidsCollisions();
            UnnamedFallback();
        }

        /// <summary>把 ConnectionConfig 与全部嵌套类型的所有可写属性都填上非默认值。</summary>
        private static ConnectionConfig MakePopulated()
        {
            return new ConnectionConfig
            {
                Id = "orig-id",
                Name = "Web 生产",
                Protocol = ProtocolType.SSH,
                Host = "10.0.0.7",
                Port = 2222,
                Username = "ops",
                Domain = "CORP",
                GroupPath = "生产/Web",
                CredentialRefId = "kp-entry-1",
                JumpChain = new JumpChainConfig
                {
                    Hops = new List<JumpHop>
                    {
                        new JumpHop { Host = "jump1", Port = 2201, Username = "j1", CredentialRefId = "kp-j1" },
                        new JumpHop { Host = "jump2", Port = 2202, Username = "j2", CredentialRefId = "kp-j2" }
                    }
                },
                Tunnel = new TunnelConfig { Type = TunnelType.DynamicSocks, LocalPort = 13389, RemoteHost = "db.internal", RemotePort = 5432 },
                Proxy = new ProxyConfig { Kind = ProxyKind.Socks5, Host = "proxy.local", Port = 1080, Username = "puser", Password = "ppass" },
                Serial = new SerialConfig
                {
                    PortName = "COM7", BaudRate = 115200, DataBits = 7,
                    StopBits = System.IO.Ports.StopBits.Two, Parity = System.IO.Ports.Parity.Odd,
                    Handshake = System.IO.Ports.Handshake.XOnXOff,
                    ReadTimeout = 900, WriteTimeout = 700, DtrEnable = true, RtsEnable = true, NewLine = "\n"
                },
                Metadata = new Dictionary<string, string> { ["team"] = "sre", ["tier"] = "1" }
            };
        }

        private static void DeepCopyIsValueEqual()
        {
            var src = MakePopulated();
            AssertCoverage(src);

            var copy = ConnectionConfigCopy.Clone(src, "Web 生产 (副本)");

            Assert.Equal(string.Empty, copy.Id, "副本必须清空 Id（由存储层重新生成）");
            Assert.Equal("Web 生产 (副本)", copy.Name, "副本名由调用方给");
            AssertPropsEqual(src, copy, "ConnectionConfig", new[] { "Id", "Name" });

            Assert.True(src.JumpChain != null && copy.JumpChain != null, "JumpChain 拷贝");
            AssertPropsEqual(src.JumpChain, copy.JumpChain, "JumpChainConfig");
            Assert.Equal(src.JumpChain.Hops.Count, copy.JumpChain.Hops.Count, "跳板 hop 数量");
            for (int i = 0; i < src.JumpChain.Hops.Count; i++)
                AssertPropsEqual(src.JumpChain.Hops[i], copy.JumpChain.Hops[i], "JumpHop#" + i);

            AssertPropsEqual(src.Tunnel, copy.Tunnel, "TunnelConfig");
            AssertPropsEqual(src.Proxy, copy.Proxy, "ProxyConfig");
            AssertPropsEqual(src.Serial, copy.Serial, "SerialConfig");

            Assert.Equal("sre", copy.Metadata["team"], "Metadata team 拷贝");
            Assert.Equal("1", copy.Metadata["tier"], "Metadata tier 拷贝");
        }

        /// <summary>深拷贝：改副本的嵌套对象/集合不能影响原件。</summary>
        private static void DeepCopyDoesNotAlias()
        {
            var src = MakePopulated();
            var copy = ConnectionConfigCopy.Clone(src, "copy");

            Assert.True(!ReferenceEquals(src.JumpChain, copy.JumpChain), "JumpChain 不能共享引用");
            Assert.True(!ReferenceEquals(src.JumpChain.Hops, copy.JumpChain.Hops), "Hops 列表不能共享引用");
            Assert.True(!ReferenceEquals(src.JumpChain.Hops[0], copy.JumpChain.Hops[0]), "Hop 元素不能共享引用");
            Assert.True(!ReferenceEquals(src.Tunnel, copy.Tunnel), "Tunnel 不能共享引用");
            Assert.True(!ReferenceEquals(src.Proxy, copy.Proxy), "Proxy 不能共享引用");
            Assert.True(!ReferenceEquals(src.Serial, copy.Serial), "Serial 不能共享引用");
            Assert.True(!ReferenceEquals(src.Metadata, copy.Metadata), "Metadata 字典不能共享引用");

            copy.Proxy.Password = "changed";
            copy.JumpChain.Hops[1].Host = "changed";
            copy.Metadata["team"] = "changed";
            Assert.Equal("ppass", src.Proxy.Password, "改副本代理密码不能改到原件");
            Assert.Equal("jump2", src.JumpChain.Hops[1].Host, "改副本跳板不能改到原件");
            Assert.Equal("sre", src.Metadata["team"], "改副本元数据不能改到原件");
        }

        private static void CopyNameAvoidsCollisions()
        {
            var existing = new List<string> { "A", "A (副本)", "A (副本 2)" };
            string n1 = ConnectionConfigCopy.SuggestCopyName("A", existing, "未命名连接", " (副本)", " (副本 {0})");
            Assert.Equal("A (副本 3)", n1, "副本名自动跳号，不重名");

            string n2 = ConnectionConfigCopy.SuggestCopyName("A", new List<string>(), "未命名连接", " (副本)", " (副本 {0})");
            Assert.Equal("A (副本)", n2, "无冲突时用一级后缀");

            // 大小写不同也算重名（Windows 上进程内比较习惯）
            string n3 = ConnectionConfigCopy.SuggestCopyName("A", new List<string> { "a (副本)" }, "未命名连接", " (副本)", " (副本 {0})");
            Assert.Equal("A (副本 2)", n3, "大小写不同视为冲突");
        }

        private static void UnnamedFallback()
        {
            string n = ConnectionConfigCopy.SuggestCopyName("   ", new List<string>(), "未命名连接", " (副本)", " (副本 {0})");
            Assert.Equal("未命名连接 (副本)", n, "空名字退化成未命名连接");
        }

        // ===== 反射断言 =====

        /// <summary>
        /// 样本必须把每个可写属性填成"非默认值"——否则新字段既不会被 Clone 覆盖，
        /// 也不会被下面的等价断言发现（默认值相等=假绿）。
        /// </summary>
        private static void AssertCoverage(ConnectionConfig sample)
        {
            AssertCoverageType(sample, "ConnectionConfig");
            AssertCoverageType(sample.JumpChain, "JumpChainConfig");
            AssertCoverageType(sample.JumpChain.Hops[0], "JumpHop");
            AssertCoverageType(sample.Tunnel, "TunnelConfig");
            AssertCoverageType(sample.Proxy, "ProxyConfig");
            AssertCoverageType(sample.Serial, "SerialConfig");
        }

        private static void AssertCoverageType(object sample, string label)
        {
            var fresh = Activator.CreateInstance(sample.GetType());
            foreach (var p in WritableProps(sample.GetType()))
            {
                object a = p.GetValue(sample, null);
                object b = p.GetValue(fresh, null);
                bool differs = !Equals(a, b);
                Assert.True(differs, label + "." + p.Name +
                    " 在样本里还是默认值——新增字段请同时更新 MakePopulated 与 ConnectionConfigCopy.Clone");
            }
        }

        private static void AssertPropsEqual(object expected, object actual, string label, string[] skip = null)
        {
            Assert.True(expected != null && actual != null, label + " 不能为 null");
            foreach (var p in WritableProps(expected.GetType()))
            {
                if (skip != null && Array.IndexOf(skip, p.Name) >= 0) continue;   // 由调用方单独断言的字段（Id/Name）
                object a = p.GetValue(expected, null);
                object b = p.GetValue(actual, null);
                // 复合对象/集合类型没有值相等语义，引用比较必然不等——交给调用方逐层断言
                if (a != null && !a.GetType().IsValueType && !(a is string)) continue;
                if (a is System.Collections.IEnumerable && !(a is string))
                    continue;   // 集合单独断言
                Assert.True(Equals(a, b), label + "." + p.Name + " 拷贝不一致（" + a + " vs " + b + "）");
            }
        }

        private static PropertyInfo[] WritableProps(Type t)
        {
            var list = new List<PropertyInfo>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.CanWrite) list.Add(p);
            return list.ToArray();
        }
    }
}
