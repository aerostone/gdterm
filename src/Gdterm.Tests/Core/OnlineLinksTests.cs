using Gdterm.Core;

namespace Gdterm.Tests.Core
{
    /// <summary>
    /// OnlineLinks 行为验证（A4 在线搜索/翻译/IP 查询的 URL 构造与 IPv4 提取）。
    /// 纯逻辑，无 UI 依赖。
    /// </summary>
    public static class OnlineLinksTests
    {
        public static void Run()
        {
            // URL 构造
            Assert.Equal("https://www.bing.com/search?q=ssh", OnlineLinks.BuildSearchUrl("ssh"), "search url plain");
            Assert.Equal(
                "https://www.bing.com/search?q=a%20b%20c%20%26%20d",
                OnlineLinks.BuildSearchUrl("a b c & d"),
                "search url escapes space/&");
            Assert.Equal(
                "https://www.bing.com/translator?from=auto&to=zh-Hans&q=",
                OnlineLinks.BuildTranslateUrl(""),
                "translate url empty query");

            // IPv4 提取
            Assert.Equal("10.0.0.5", OnlineLinks.ExtractFirstIpv4("ping 10.0.0.5 failed"), "ipv4 plain");
            Assert.Equal("192.168.1.1", OnlineLinks.ExtractFirstIpv4("connect 192.168.1.1:8080"), "ipv4 with port");
            Assert.Equal(null, OnlineLinks.ExtractFirstIpv4("10.0.0.256"), "ipv4 invalid octet 256");
            Assert.Equal(null, OnlineLinks.ExtractFirstIpv4("no ip here"), "ipv4 none");
            Assert.Equal(null, OnlineLinks.ExtractFirstIpv4(""), "ipv4 empty");
            Assert.Equal(null, OnlineLinks.ExtractFirstIpv4(null), "ipv4 null");
            Assert.Equal("1.2.3.4", OnlineLinks.ExtractFirstIpv4("999.1.1.1 then 1.2.3.4"), "ipv4 first valid only");
            // 已知限制：五段点串（如版本 v1.2.3.4.5）会匹配到后四段——IP 查询是用户主动动作，误报无害
            Assert.Equal("1.2.3.4", OnlineLinks.ExtractFirstIpv4("1.2.3.4.5"), "ipv4 in dotted version");
            // 句尾句号不影响匹配（\b 边界）
            Assert.Equal("10.0.0.1", OnlineLinks.ExtractFirstIpv4("ping 10.0.0.1. OK"), "ipv4 sentence dot");

            // IP 查询 URL
            Assert.Equal(
                "https://www.ip138.com/ipslookup/ipWhoisSave?ip=10.0.0.5",
                OnlineLinks.BuildIpLookupUrl("10.0.0.5"),
                "ip lookup url");

            // host:port 提取（A3-lite）
            var hp1 = OnlineLinks.ExtractFirstHostPort("connect prod-web:22 now");
            Assert.True(hp1 != null && hp1.Host == "prod-web" && hp1.Port == 22, "hostport hostname");
            var hp2 = OnlineLinks.ExtractFirstHostPort("ssh 192.168.1.1:8080");
            Assert.True(hp2 != null && hp2.Host == "192.168.1.1" && hp2.Port == 8080, "hostport ip");
            Assert.Equal(null, OnlineLinks.ExtractFirstHostPort("abc:99999"), "hostport port out of range");
            Assert.Equal(null, OnlineLinks.ExtractFirstHostPort("no port here"), "hostport none");
            Assert.Equal(null, OnlineLinks.ExtractFirstHostPort(null), "hostport null");
            // 已知限制：时间串会命中（port=30）——用户主动点击才触发，误报无害
            var hp3 = OnlineLinks.ExtractFirstHostPort("time 12:30");
            Assert.True(hp3 != null && hp3.Host == "12" && hp3.Port == 30, "hostport time literal");
        }
    }
}
