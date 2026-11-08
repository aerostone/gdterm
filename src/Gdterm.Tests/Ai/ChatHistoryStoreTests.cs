using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.AI;
using Gdterm.AI.Models;

namespace Gdterm.Tests.Ai
{
    /// <summary>
    /// ChatHistoryStore 单测（F13 按连接键控 v2 格式）。
    /// 覆盖：缺失/往返/多键隔离/空保存删键/损坏容错/v1 迁移/键转义/空 role 跳过。
    /// </summary>
    public static class ChatHistoryStoreTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] ChatHistoryStore");
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ai-chat-history.json");

            try
            {
                var store = new ChatHistoryStore(path);

                // 文件不存在 → 任意键读空（不抛异常）
                Assert.Equal(0, store.Load("conn-a").Count, "Load empty when file missing (keyed)");
                Assert.Equal(0, store.Load(null).Count, "Load empty when file missing (null key → global)");

                // 多键隔离：两连接各自存读互不干扰
                store.Save("conn-a", new List<ChatMessage> { new ChatMessage("user", "A 的问题") });
                store.Save("conn-b", new List<ChatMessage> { new ChatMessage("user", "B 的问题") });
                Assert.Equal(1, store.Load("conn-a").Count, "conn-a isolated count");
                Assert.Equal(1, store.Load("conn-b").Count, "conn-b isolated count");
                Assert.True(string.Equals(store.Load("conn-a")[0].Content, "A 的问题", StringComparison.Ordinal),
                    "conn-a content not crossed");
                Assert.True(string.Equals(store.Load("conn-b")[0].Content, "B 的问题", StringComparison.Ordinal),
                    "conn-b content not crossed");
                Assert.Equal(0, store.Load("conn-c").Count, "unknown key → empty");

                // 空历史保存 = 删除该键（不残留空数组）
                store.Save("conn-a", new List<ChatMessage>());
                Assert.Equal(0, store.Load("conn-a").Count, "Save empty removes key");
                Assert.Equal(1, store.Load("conn-b").Count, "other key untouched");
                Assert.True(!File.ReadAllText(path, new UTF8Encoding(false)).Contains("conn-a"),
                    "removed key not serialized");

                // 往返：中文/换行/引号/反斜杠/制表符/控制字符
                var tricky = new List<ChatMessage>
                {
                    new ChatMessage("user", "帮我看看 \"CPU\" 占用\n第二行\t带制表符"),
                    new ChatMessage("assistant", "结果含反斜杠 \\ 与引号 \"，还有控制字符 \u0001 和中文：磁盘 80%")
                };
                store.Save("conn-b", tricky);

                var loaded = store.Load("conn-b");
                Assert.Equal(2, loaded.Count, "round-trip count");
                Assert.True(string.Equals(loaded[0].Role, "user", StringComparison.Ordinal), "role user");
                Assert.True(string.Equals(loaded[1].Role, "assistant", StringComparison.Ordinal), "role assistant");
                Assert.True(string.Equals(loaded[0].Content, tricky[0].Content, StringComparison.Ordinal),
                    "content round-trip user (换行/引号/制表)");
                Assert.True(string.Equals(loaded[1].Content, tricky[1].Content, StringComparison.Ordinal),
                    "content round-trip assistant (反斜杠/控制字符/中文)");

                // 键本身含特殊字符（引号/反斜杠/中文）往返
                store.Save("wei\"rd\\conn-名", new List<ChatMessage> { new ChatMessage("user", "x") });
                Assert.Equal(1, store.Load("wei\"rd\\conn-名").Count, "tricky key round-trip");

                // 损坏文件 → 空（不抛异常）
                File.WriteAllText(path, "{ 这不是 JSON [[", new UTF8Encoding(false));
                Assert.Equal(0, new ChatHistoryStore(path).Load("conn-b").Count, "corrupt file → empty");

                // v1 旧格式（全局 messages 数组）→ 迁入 global 键，不丢历史
                File.WriteAllText(path,
                    "{\"version\":1,\"messages\":[{\"role\":\"\",\"content\":\"x\"},{\"role\":\"user\",\"content\":\"y\"}]}");
                var v1store = new ChatHistoryStore(path);
                var migrated = v1store.Load(ChatHistoryStore.DefaultKey);
                Assert.Equal(1, migrated.Count, "v1 migrated: empty role skipped");
                Assert.True(string.Equals(migrated[0].Content, "y", StringComparison.Ordinal), "v1 kept valid message");
                Assert.Equal(0, v1store.Load("conn-b").Count, "v1 file has no conn-b");
                // v1 迁移后的 store 再保存 → 写 v2 且 global 内容保留
                v1store.Save("conn-x", new List<ChatMessage> { new ChatMessage("user", "new") });
                var reloaded = new ChatHistoryStore(path);
                Assert.Equal(1, reloaded.Load(ChatHistoryStore.DefaultKey).Count, "v1→v2 rewrite keeps global");
                Assert.Equal(1, reloaded.Load("conn-x").Count, "v1→v2 rewrite adds new key");

                // 聊天文本里恰好含 "conversations" 字面量不误判格式（按 v1 解析）
                File.WriteAllText(path,
                    "{\"version\":1,\"messages\":[{\"role\":\"user\",\"content\":\"说说 \\\"conversations\\\" 是什么\"}]}");
                var literal = new ChatHistoryStore(path).Load(ChatHistoryStore.DefaultKey);
                Assert.Equal(1, literal.Count, "conversations literal in v1 text not misparsed");
                Assert.True(string.Equals(literal[0].Content, "说说 \"conversations\" 是什么", StringComparison.Ordinal),
                    "literal content intact");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
