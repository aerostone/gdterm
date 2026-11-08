using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Gdterm.Core.Models;
using Gdterm.Logging;

namespace Gdterm.Tests.Logging
{
    /// <summary>CommandHistoryStore.DeleteByIds 行为测试（F6 命令历史多选批量删除）。</summary>
    public static class CommandHistoryStoreTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] CommandHistoryStore.DeleteByIds");
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var store = new CommandHistoryStore(dir);

                // 记录 3 条
                for (int i = 1; i <= 3; i++)
                {
                    store.RecordCommand(new CommandHistoryEntry
                    {
                        Command = "cmd-" + i,
                        Hostname = "h" + i
                    });
                }

                var all = store.Query(new CommandHistoryQuery { Limit = 100 });
                if (all.Count != 3)
                {
                    // 失败诊断：静默吞异常是 0.1.452 的红线根因——把被吞的信息打到 CI 日志
                    try
                    {
                        Console.WriteLine("[diag] LastDeserializationError=" + CommandHistoryStore.LastDeserializationError);
                        var files = Directory.GetFiles(dir, "commands-*.jsonl");
                        Console.WriteLine("[diag] files=" + files.Length);
                        foreach (var f in files)
                        {
                            // 探针 1：File.ReadAllLines（FileShare.Read）
                            try
                            {
                                var l1 = File.ReadAllLines(f, Encoding.UTF8);
                                Console.WriteLine("[probe] ReadAllLines OK lines=" + l1.Length);
                            }
                            catch (Exception e1) { Console.WriteLine("[probe] ReadAllLines FAIL: " + e1.Message); }
                            // 探针 2：显式 Read + FileShare.ReadWrite
                            try
                            {
                                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var sr = new StreamReader(fs, Encoding.UTF8))
                                {
                                    int n = 0;
                                    while (sr.ReadLine() != null) n++;
                                    Console.WriteLine("[probe] explicit Read(ReadWrite share) OK lines=" + n);
                                }
                            }
                            catch (Exception e2) { Console.WriteLine("[probe] explicit Read FAIL: " + e2.Message); }
                            // 探针 3：等 500ms 再读——区分「瞬时外部句柄（AV 扫描窗）」vs「结构性锁」
                            System.Threading.Thread.Sleep(500);
                            try
                            {
                                var l3 = File.ReadAllLines(f, Encoding.UTF8);
                                Console.WriteLine("[probe] after-500ms ReadAllLines OK lines=" + l3.Length);
                            }
                            catch (Exception e3) { Console.WriteLine("[probe] after-500ms ReadAllLines FAIL: " + e3.Message); }
                        }
                    }
                    catch (Exception dex) { Console.WriteLine("[diag] diag failed: " + dex.Message); }
                }
                Assert.Equal(3, all.Count, "initial 3 entries");

                // 删除其中 2 条
                var toDelete = all.Take(2).Select(e => e.Id).ToList();
                var removed = store.DeleteByIds(toDelete);
                Assert.Equal(2, removed, "removed count");

                var rest = store.Query(new CommandHistoryQuery { Limit = 100 });
                Assert.Equal(1, rest.Count, "1 remains");
                Assert.True(!toDelete.Contains(rest[0].Id), "survivor not in deleted set");

                // 删不存在的 Id → 0（文件未被破坏）
                Assert.Equal(0, store.DeleteByIds(new[] { "no-such-id" }), "unknown id → 0");
                Assert.Equal(1, store.Query(new CommandHistoryQuery { Limit = 100 }).Count, "file intact");

                // null / 空集合 → 0
                Assert.Equal(0, store.DeleteByIds(null), "null → 0");
                Assert.Equal(0, store.DeleteByIds(new List<string>()), "empty → 0");

                // 删除后仍可继续写入
                store.RecordCommand(new CommandHistoryEntry { Command = "after-delete" });
                var after = store.Query(new CommandHistoryQuery { Limit = 100 });
                Assert.Equal(2, after.Count, "write after delete");

                // 删最后一条 + 全部
                Assert.Equal(2, store.DeleteByIds(after.Select(e => e.Id)), "delete all remaining");
                Assert.Equal(0, store.Query(new CommandHistoryQuery { Limit = 100 }).Count, "empty at end");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
