using System;
using System.Collections.Generic;
using System.IO;
using Gdterm.AI;
using Gdterm.AI.Models;

namespace Gdterm.Tests.Ai
{
    /// <summary>
    /// AiAssistantService 对话键控单测（F13）——不发网络请求，只测历史键管理：
    /// 切换/隔离/清空只动活跃键/加载硬顶。
    /// </summary>
    public static class AiAssistantServiceTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] AiAssistantService (conversation keys)");
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ai-chat-history.json");

            try
            {
                // 预置两键历史
                var store = new ChatHistoryStore(path);
                store.Save("conn-a", new List<ChatMessage>
                {
                    new ChatMessage("user", "a1"), new ChatMessage("assistant", "a2"),
                    new ChatMessage("user", "a3"), new ChatMessage("assistant", "a4")
                });
                store.Save("conn-b", new List<ChatMessage>
                {
                    new ChatMessage("user", "b1")
                });

                var svc = new AiAssistantService(new AiConfiguration { ApiEndpoint = "http://127.0.0.1:1/v1" }, store);

                // 默认活跃键 = global（盘上无 → 空）
                Assert.Equal(0, svc.GetHistory().Count, "default key global empty");

                // 切到 conn-a → 懒加载 4 条
                svc.SetActiveConversation("conn-a");
                Assert.Equal(4, svc.GetHistory().Count, "switch to conn-a loads 4");

                // 再切 conn-b → 隔离，只有 1 条
                svc.SetActiveConversation("conn-b");
                Assert.Equal(1, svc.GetHistory().Count, "switch to conn-b isolated (1)");
                Assert.True(string.Equals(svc.GetHistory()[0].Content, "b1", StringComparison.Ordinal),
                    "conn-b content correct");

                // 空键 = global
                svc.SetActiveConversation(null);
                Assert.Equal(0, svc.GetHistory().Count, "null key → global");

                // 同键幂等（重复切换不丢数据）
                svc.SetActiveConversation("conn-a");
                svc.SetActiveConversation("conn-a");
                Assert.Equal(4, svc.GetHistory().Count, "idempotent switch keeps data");

                // ClearHistory 只清活跃键
                svc.SetActiveConversation("conn-a");
                svc.ClearHistory();
                Assert.Equal(0, svc.GetHistory().Count, "active key cleared");
                Assert.Equal(0, store.Load("conn-a").Count, "cleared persisted (key removed)");
                Assert.Equal(1, store.Load("conn-b").Count, "other key untouched by clear");

                // 加载硬顶：预置 50 条，MaxHistoryMessages=40 → 截到 40
                var big = new List<ChatMessage>();
                for (int i = 0; i < 50; i++)
                    big.Add(new ChatMessage(i % 2 == 0 ? "user" : "assistant", "m" + i));
                store.Save("conn-big", big);
                svc.MaxHistoryMessages = 40;
                svc.SetActiveConversation("conn-big");
                Assert.Equal(40, svc.GetHistory().Count, "load capped at MaxHistoryMessages");
                Assert.True(string.Equals(svc.GetHistory()[0].Content, "m10", StringComparison.Ordinal),
                    "cap keeps most recent (drops oldest 10)");

                // 纯内存模式（store = null）：键切换不抛、不持久化
                var mem = new AiAssistantService(new AiConfiguration());
                mem.SetActiveConversation("x");
                mem.SetActiveConversation("y");
                Assert.Equal(0, mem.GetHistory().Count, "memory-only mode no store no throw");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
