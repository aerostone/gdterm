using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gdterm.Core.Models;
using Gdterm.Logging;

namespace Gdterm.Tests.Logging
{
    /// <summary>
    /// CommandHistoryStore.Query 的过滤语义测试。
    ///
    /// 背景（真实缺陷）：MatchesQuery 里主机/命令内容/标签三条条件都写成
    /// `entry.X != null &amp;&amp; 不匹配才排除`，于是「该字段为空」的条目永远通过过滤——
    /// 选了主机 web01 之后，本地终端（Hostname 为 null）跑的命令照样混进结果。
    /// 过滤看着生效，实际只筛掉了一半。这组用例把三个字段的「空值必须判不匹配」钉死。
    /// </summary>
    public static class CommandHistoryQueryTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] CommandHistoryStore.Query filter semantics");
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-qtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var store = new CommandHistoryStore(dir);

                store.RecordCommand(new CommandHistoryEntry { Command = "ls -l", Hostname = "web01", Tags = "db, prod" });
                store.RecordCommand(new CommandHistoryEntry { Command = "tail -f app.log", Hostname = "db01", Tags = "db-prod" });
                // 主机为空（本地终端）：这是最容易被“过滤器放行”漏进来的那类记录
                store.RecordCommand(new CommandHistoryEntry { Command = "ipconfig", Hostname = null });
                // 命令为空（理论上不该有，但存储不校验；过滤时必须判不匹配而不是放行）
                store.RecordCommand(new CommandHistoryEntry { Command = null, Hostname = "web01" });

                // ── 主机过滤：空主机条目必须被排除 ──
                var byHost = store.Query(new CommandHistoryQuery { Hostname = "web01", Limit = 100 });
                Assert.Equal(2, byHost.Count, "host=web01 → 2 条（含命令为空那条，都是 web01）");
                Assert.True(byHost.All(e => e.Hostname == "web01"),
                    "host=web01 的结果里不许出现空主机条目（原缺陷：本地终端记录混入）");

                var byDb = store.Query(new CommandHistoryQuery { Hostname = "db01", Limit = 100 });
                Assert.Equal(1, byDb.Count, "host=db01 → 1 条");
                Assert.True(byDb[0].Hostname == "db01", "host=db01 命中 db01");

                // 主机名不分大小写
                Assert.Equal(1, store.Query(new CommandHistoryQuery { Hostname = "DB01", Limit = 100 }).Count,
                    "host 匹配不分大小写");

                // ── 命令内容过滤：空命令条目必须被排除 ──
                var byCmd = store.Query(new CommandHistoryQuery { CommandContains = "log", Limit = 100 });
                Assert.Equal(1, byCmd.Count, "CommandContains=log → 1 条");
                Assert.Equal("tail -f app.log", byCmd[0].Command, "CommandContains 命中预期命令");
                Assert.True(byCmd.All(e => e.Command != null && e.Command.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0),
                    "CommandContains 的结果里不许出现空命令条目");

                // 大小写不敏感
                Assert.Equal(1, store.Query(new CommandHistoryQuery { CommandContains = "IPCONFIG", Limit = 100 }).Count,
                    "CommandContains 不分大小写");

                // ── 标签过滤：按「任一标签精确相等」，不是整串子串 ──
                // Tags="db, prod" 里应有 db；Tags="db-prod" 不该被 tag=db 命中
                var byTag = store.Query(new CommandHistoryQuery { Tag = "db", Limit = 100 });
                Assert.Equal(1, byTag.Count, "tag=db → 1 条（db,prod 那条）");
                Assert.True(byTag[0].Tags == "db, prod",
                    "tag=db 不许命中 db-prod（整串 Contains 的经典误伤）");

                var byTagProd = store.Query(new CommandHistoryQuery { Tag = "prod", Limit = 100 });
                Assert.Equal(1, byTagProd.Count, "tag=prod → 1 条");
                Assert.True(byTagProd[0].Tags == "db, prod", "tag=prod 命中 db, prod");

                // 空标签条目必须被排除
                Assert.Equal(0, store.Query(new CommandHistoryQuery { Tag = "nosuchtag", Limit = 100 }).Count,
                    "tag 无命中 → 0 条（不许把无标签条目放进来）");

                // ── 组合条件 ──
                var combo = store.Query(new CommandHistoryQuery { Hostname = "web01", CommandContains = "ls", Limit = 100 });
                Assert.Equal(1, combo.Count, "host+cmd 组合 → 1 条");
                Assert.Equal("ls -l", combo[0].Command, "组合条件命中 ls -l");

                // ── 无条件查询仍是全量 ──
                Assert.Equal(4, store.Query(new CommandHistoryQuery { Limit = 100 }).Count, "无条件 → 全量 4 条");
                Assert.Equal(4, store.Query().Count, "query 为 null → 全量 4 条");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
