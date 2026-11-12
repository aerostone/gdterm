using System;
using System.Linq;
using Gdterm.Core.Models;
using Gdterm.Tunnel;
using Renci.SshNet;

namespace Gdterm.Tests.Tunnel
{
    /// <summary>
    /// SSH 连接信息工厂：**不配代理**（Proxy=null）必须是通畅路径。
    ///
    /// 2026-11-11 端到端实证（假 SSH 服务器 + 真实 TerminalSession）：代理特性
    /// 引入后工厂里写了裸 `proxy.Password ?? ""`，而 ConnectionConfig.Proxy 的文档
    /// 明确「null 表示直连」、ConnectionDialog 在未启用代理时也正是把它置 null ——
    /// 于是**每一台没配代理的 SSH 连接**在 Connect() 里抛 NullReferenceException，
    /// 直连 SSH 全线连不上。UI 冒烟只连本地终端、CI 从不真连服务器，所以一直没红。
    ///
    /// 本测试只碰纯构造逻辑（不发包），把「Proxy 为空/未启用/配了」三种形态固定住。
    /// </summary>
    public static class SshConnectionInfoFactoryTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] SshConnectionInfoFactory");
            NullProxyIsDirectAndDoesNotThrow();
            NullProxyPasswordIsTolerated();
            DisabledProxyKindIsDirect();
            ConfiguredProxyCarriesEndpoint();
        }

        private static CredentialPayload Cred()
        {
            return new CredentialPayload { Username = "demo", Password = "S3cret!" };
        }

        /// <summary>回归核心：proxy=null（直连）不得抛异常，且不得带代理。</summary>
        private static void NullProxyIsDirectAndDoesNotThrow()
        {
            ConnectionInfo info = null;
            try
            {
                info = SshConnectionInfoFactory.Create("127.0.0.1", 2223, null, Cred(), 15, null);
            }
            catch (Exception ex)
            {
                Assert.True(false, "Proxy=null 不得抛异常，实际 " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            Assert.True(info != null, "Proxy=null 应返回 ConnectionInfo");
            Assert.Equal("127.0.0.1", info.Host, "host 透传");
            Assert.Equal(2223, info.Port, "port 透传");
            Assert.Equal("demo", info.Username, "username 缺省回落 credential");
            Assert.True(info.ProxyType == ProxyTypes.None, "Proxy=null 必须是直连（ProxyType=None）");
            Assert.True(info.AuthenticationMethods != null && info.AuthenticationMethods.Count == 1,
                "应只有一种认证方式");
            Assert.True(info.AuthenticationMethods[0] is PasswordAuthenticationMethod,
                "有密码时走 PasswordAuthenticationMethod");
        }

        /// <summary>代理对象存在但密码为 null（例如用用户名+空密码的 SOCKS5）不得抛。</summary>
        private static void NullProxyPasswordIsTolerated()
        {
            var proxy = new ProxyConfig { Kind = ProxyKind.Http, Host = "10.0.0.1", Port = 8080, Username = "pu", Password = null };
            ConnectionInfo info = null;
            try
            {
                info = SshConnectionInfoFactory.Create("10.1.1.1", 22, null, Cred(), 15, proxy);
            }
            catch (Exception ex)
            {
                Assert.True(false, "代理密码为 null 不得抛异常，实际 " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            Assert.True(info.ProxyType == ProxyTypes.Http, "HTTP 代理应映射为 ProxyTypes.Http");
            Assert.Equal("10.0.0.1", info.ProxyHost, "代理主机透传");
            Assert.Equal(8080, info.ProxyPort, "代理端口透传");
        }

        /// <summary>Kind=None 是「有对象但未启用」，等价直连。</summary>
        private static void DisabledProxyKindIsDirect()
        {
            var proxy = new ProxyConfig { Kind = ProxyKind.None, Host = "", Port = 0 };
            var info = SshConnectionInfoFactory.Create("h.example", 22, "u", Cred(), 15, proxy);
            Assert.True(info.ProxyType == ProxyTypes.None, "Kind=None 视为直连");
        }

        private static void ConfiguredProxyCarriesEndpoint()
        {
            var proxy = new ProxyConfig { Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = 1080, Username = "su", Password = "sp" };
            var info = SshConnectionInfoFactory.Create("h.example", 22, "u", Cred(), 15, proxy);
            Assert.True(info.ProxyType == ProxyTypes.Socks5, "SOCKS5 映射正确");
            Assert.Equal(1080, info.ProxyPort, "SOCKS5 端口透传");
        }
    }
}
