using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.AI;
using Gdterm.AI.Models;

namespace Gdterm.Tests.Ai
{
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

                // 文件不存在 → 空列表（不抛异常）
                var missing = store.Load();
                Assert.Equal(0, missing.Count, "Load empty when file missing");

                // 空历史保存 → 再读为空
                store.Save(new List<ChatMessage>());
                Assert.Equal(0, store.Load().Count, "Save empty → Load empty");

                // 往返：中文/换行/引号/反斜杠/制表符/控制字符
                var tricky = new List<ChatMessage>
                {
                    new ChatMessage("user", "帮我看看 \"CPU\" 占用\n第二行\t带制表符"),
                    new ChatMessage("assistant", "结果含反斜杠 \\ 与引号 \"，还有控制字符 \u0001 和中文：磁盘 80%")
                };
                store.Save(tricky);

                var loaded = store.Load();
                Assert.Equal(2, loaded.Count, "round-trip count");
                Assert.True(string.Equals(loaded[0].Role, "user", StringComparison.Ordinal), "role user");
                Assert.True(string.Equals(loaded[1].Role, "assistant", StringComparison.Ordinal), "role assistant");
                Assert.True(string.Equals(loaded[0].Content, tricky[0].Content, StringComparison.Ordinal),
                    "content round-trip user (换行/引号/制表)");
                Assert.True(string.Equals(loaded[1].Content, tricky[1].Content, StringComparison.Ordinal),
                    "content round-trip assistant (反斜杠/控制字符/中文)");

                // 损坏文件 → 空列表（不抛异常）
                File.WriteAllText(path, "{ 这不是 JSON [[", new UTF8Encoding(false));
                Assert.Equal(0, store.Load().Count, "corrupt file → empty");

                // 空 role 的对象被跳过
                File.WriteAllText(path,
                    "{\"version\":1,\"messages\":[{\"role\":\"\",\"content\":\"x\"},{\"role\":\"user\",\"content\":\"y\"}]}");
                var partial = store.Load();
                Assert.Equal(1, partial.Count, "empty role skipped");
                Assert.True(string.Equals(partial[0].Content, "y", StringComparison.Ordinal), "kept valid message");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
