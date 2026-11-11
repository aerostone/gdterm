using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gdterm.Connections;
using Gdterm.Core;
using Gdterm.Core.Models;

namespace Gdterm.Tests.Connections
{
    /// <summary>
    /// 持久化层「转义 → 落盘 → 读回」往返一致性（2026-11-11 架构审计 F1 回归）。
    ///
    /// 背景：13 个 store 各写一份手写 JSON 转义，转义集合互不相同、有的只转义不还原。
    /// data/config/highlight.json 里的正则在第二次启动后会被逐次加倍转义，内置高亮规则与
    /// 触发器全部静默失效。本测试锁住「往返必须恒等」，任何未来再写第三份转义都会红。
    /// </summary>
    public static class JsonRoundTripTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] JsonRoundTrip");
            JsonTextPrimitivesRoundTrip();
            HighlightRuleRoundTripIsStableAcrossRestarts();
            QuickCommandKeepsControlCharacters();
            ConnectionMetadataRoundTrip();
            BookmarkStoreRoundTrip();
            KeyBindingValueRoundTrip();
            DangerousRulePatternRoundTrip();
            StoreSplittingIgnoresBracketsInsideStrings();
        }

        /// <summary>基座：Escape/Unescape 必须是可往返的一对。</summary>
        private static void JsonTextPrimitivesRoundTrip()
        {
            var cases = new[]
            {
                @"\b(ERROR|WARN)\b",            // ← P1 的直接触发形态
                "C:\\Users\\gdm\\config.ini",    // Windows 路径（反斜杠）
                "say \"hello\"",                 // 引号
                "line1\nline2\r\nline3",         // 换行 / 回车
                "tab\there",                     // 制表符
                "brace{inside}quote\"",          // 括号与引号（配对查找必须跳过字符串）
                "\u0001控制符\u001b",            // 低控制符(\uXXXX)
                "",                              // 空串
            };
            foreach (var original in cases)
            {
                var escaped = JsonText.Escape(original);
                Assert.NotContains(escaped, "\n", "escaped 不含裸换行 [" + escaped + "]");
                Assert.Equal(original, JsonText.Unescape(escaped), "JsonText 往返恒等");
                Assert.Equal(original, JsonText.ExtractString("{\"k\": \"" + escaped + "\"}", "k"),
                    "ExtractString 往返恒等");
            }

            // 键值后面紧跟其他键：结束引号必须是未转义的那个
            var json = "{\"pattern\": \"a\\\\\\\"b\", \"next\": \"x\"}";
            Assert.Equal("a\\\"b", JsonText.ExtractString(json, "pattern"),
                "ExtractString 跳过 \\\" 只认未转义引号");

            // 括号配对必须跳过字符串内容
            Assert.Equal(9, JsonText.FindMatchingBracket("[\"a]b\", 1]", 0), "FindMatchingBracket 跳过字符串里的 ]");
        }

        /// <summary>P1：高亮规则经多次 save/load 后正则必须逐字节不变（原来每次启动都会多一倍反斜杠）。</summary>
        private static void HighlightRuleRoundTripIsStableAcrossRestarts()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "highlight.json");
            try
            {
                var store = new HighlightStore(path);
                var config = new HighlightRuleConfig();
                config.Rules.Add(new HighlightRule
                {
                    Id = "rule-p1",
                    Name = "错误/警告",
                    Pattern = @"\b(ERROR|WARN)\b",
                    IsRegex = true,
                    Enabled = true,
                    ForegroundColor = "#FFFF4444",
                    SortOrder = 1
                });
                store.Save(config);

                for (int restart = 1; restart <= 3; restart++)
                {
                    var loaded = new HighlightStore(path).Load();
                    Assert.Equal(1, loaded.Rules.Count, "第 " + restart + " 次重启仍能读回 1 条规则");
                    Assert.Equal(@"\b(ERROR|WARN)\b", loaded.Rules[0].Pattern,
                        "第 " + restart + " 次重启正则未被加倍转义");
                    Assert.Equal("错误/警告", loaded.Rules[0].Name, "第 " + restart + " 次重启名称完好");
                    // 原实现在 UI 保存时会把读回的规则再存一次，等同用户改一次配置
                    store.Save(loaded);
                }

                // 内置默认规则也必须能安全往返（它们是用户感知「高亮全哑」的现场）
                var defaults = HighlightRuleConfig.GetDefault();
                store.Save(defaults);
                var reread = new HighlightStore(path).Load();
                Assert.Equal(defaults.Rules.Count, reread.Rules.Count, "默认规则条数往返一致");
                for (int i = 0; i < defaults.Rules.Count; i++)
                {
                    Assert.Equal(defaults.Rules[i].Pattern, reread.Rules[i].Pattern,
                        "默认规则[" + i + "] 正则往返一致");
                }
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>QuickCommandStore 原来把 \r 直接丢弃（多行命令被静默改写）。</summary>
        private static void QuickCommandKeepsControlCharacters()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "quick.json");
            try
            {
                var store = new QuickCommandStore(path);
                store.SaveAll(new List<QuickCommand>
                {
                    new QuickCommand
                    {
                        Id = "qc1",
                        Name = "多行脚本",
                        Command = "cd /tmp\r\ntail -n 100 app.log\t| grep ERROR \"quoted\" C:\\path",
                        Group = "运维",
                        Description = "带 \r\n 换行与 \"引号\" 的说明"
                    }
                });

                var loaded = store.LoadAll();
                Assert.Equal(1, loaded.Count, "快速命令读回 1 条");
                Assert.Equal("cd /tmp\r\ntail -n 100 app.log\t| grep ERROR \"quoted\" C:\\path",
                    loaded[0].Command, "命令原文（含 \\r\\n \\t \\\" \\\\) 逐字节往返");
                Assert.Equal("带 \r\n 换行与 \"引号\" 的说明", loaded[0].Description, "说明原文往返");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>connections.json 的 metadata（扁平对象）与备注：含换行/引号/反斜杠必须往返。</summary>
        private static void ConnectionMetadataRoundTrip()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "connections.json");
            try
            {
                var store = new ConnectionStoreJson(path);
                var cfg = new ConnectionConfig
                {
                    Name = "含\"引号\"的\\连接",
                    Host = "10.0.0.9",
                    Port = 22,
                    Username = "root",
                    GroupPath = "生产/含\"引号\"分组"
                };
                cfg.Metadata = new Dictionary<string, string>
                {
                    { "notes", "多行\r\n备注 含 \"引号\" 与 \\ 反斜杠" },
                    { "plain", "v" }
                };
                var added = store.Add(cfg);

                var loaded = store.GetById(added.Id);
                Assert.True(loaded != null, "按 id 读回连接");
                Assert.Equal("含\"引号\"的\\连接", loaded.Name, "连接名往返");
                Assert.Equal("生产/含\"引号\"分组", loaded.GroupPath, "分组路径往返");
                Assert.Equal("多行\r\n备注 含 \"引号\" 与 \\ 反斜杠", loaded.Metadata["notes"], "metadata 往返");
                Assert.Equal("v", loaded.Metadata["plain"], "普通 metadata 不受影响");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>书签/最近连接：裸布尔值（isFavorite/success）必须仍能读回 true。
        /// 审计 F1 把取值统一到 JsonText.ExtractString 后，字符串值走反转义，
        /// 非字符串值仍要回落到裸标量扫描，否则收藏标记会静默变成 false。</summary>
        private static void BookmarkStoreRoundTrip()
        {
            var dir = NewTempDir();
            try
            {
                var store = new BookmarkStoreJson(dir);
                store.Add(new SessionBookmark
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "生产 \"核心\" 库",
                    ConnectionId = "conn-1",
                    Tags = "prod,critical",
                    ConnectCount = 7,
                    IsFavorite = true
                });
                store.AddRecentConnection(new RecentConnection
                {
                    ConnectionId = "conn-1",
                    Host = "10.0.0.9",
                    Protocol = "SSH",
                    ConnectedAt = DateTime.UtcNow,
                    Success = true
                });

                var loaded = store.LoadAll();
                Assert.Equal(1, loaded.Count, "书签读回 1 条");
                Assert.Equal("生产 \"核心\" 库", loaded[0].Name, "书签名里的引号往返");
                Assert.Equal(7, loaded[0].ConnectCount, "裸数字 connectCount 读回");
                Assert.True(loaded[0].IsFavorite, "裸布尔 isFavorite 读回 true");

                var recent = store.GetRecentConnections(10);
                Assert.Equal(1, recent.Count, "最近连接读回 1 条");
                Assert.True(recent[0].Success, "裸布尔 success 读回 true");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>键位绑定的 Value 里含 Esc/Tab 控制符与字面反斜杠：落盘再读回必须逐字节一致。
        /// 原实现写 \u001b 却从不还原 hex，读回后键序变成字面 "\u001b[A" 文本。</summary>
        private static void KeyBindingValueRoundTrip()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "keybindings.json");
            try
            {
                const string value = "\u001b[A\t\u001b[1;5C";
                var store = new TerminalKeyBindingStore(path);
                store.Load();
                store.AddCustomBinding(new TerminalKeyBinding
                {
                    Name = "自定义序 key",
                    Key = "Y",
                    Ctrl = true,
                    Type = SendType.Sequence,
                    Value = value,
                    Enabled = true
                });

                store.Reload();
                var config = store.Load();
                TerminalKeyBinding found = null;
                foreach (var b in config.CustomBindings)
                {
                    if (b.Name == "自定义序 key") { found = b; break; }
                }
                Assert.True(found != null, "自定义绑定读回");
                Assert.Equal(value, found.Value, "控制符序列逐字节往返（Esc/Tab）");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        /// <summary>危险命令规则的 Pattern 全是正则：往返后必须原样且规则依旧生效。
        /// 原实现写侧转义、读侧不反转义：\d 落盘成 \\d，读回仍是两个字面反斜杠，黑名单静默失配。</summary>
        private static void DangerousRulePatternRoundTrip()
        {
            var dir = NewTempDir();
            var path = Path.Combine(dir, "dangerous-commands.json");
            try
            {
                // 故意用默认规则集不会命中的形态，避免内置规则污染断言
                const string pattern = @"^custom-probe\s+\d{3}$";
                var detector = new Gdterm.Security.DangerousCommandDetector(path);
                detector.AddRule(new DangerousCommandRule
                {
                    Id = "custom-probe",
                    Name = "探针规则",
                    Pattern = pattern,
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    Category = "文件",
                    Enabled = true
                });

                var reloaded = new Gdterm.Security.DangerousCommandDetector(path);
                DangerousCommandRule rule = null;
                foreach (var r in reloaded.GetAllRules())
                {
                    if (r.Id == "custom-probe") { rule = r; break; }
                }
                Assert.True(rule != null, "自定义规则读回");
                Assert.Equal(pattern, rule.Pattern, "正则 pattern 原样往返");
                Assert.True(reloaded.Check("custom-probe 123").IsDangerous, "重载后规则仍命中");
                Assert.True(!reloaded.Check("custom-probe abc").IsDangerous, "\\d 边界仍然生效");
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-jsonrt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>store 切对象必须跳过字符串内容：值里有不成对的 '[' 或 '}' 时，自写 depth
        /// 循环会把整个数组切空 → 配置静默回落默认、没有任何报错。真实形态：
        /// 终端快捷键 value = "\u001b[A"（Esc+[）、命令模板里的 "ls [a-z]*"。本测试锁死。</summary>
        private static void StoreSplittingIgnoresBracketsInsideStrings()
        {
            // 1) 对象切分：字符串里的 '[' 不参与计数
            var one = "{\"name\":\"x\",\"value\":\"\u001b[A\"}";
            var objs = JsonText.SplitObjects(one);
            Assert.Equal(1, objs.Count, "SplitObjects 无视字符串内的 '['");
            Assert.Equal("\u001b[A", JsonText.ExtractString(objs[0], "value"), "SplitObjects 保住完整值");

            // 2) 数组终点：同上，且不吞掉后一个兄弟键
            var arr = "[{\"v\":\"\u001b[A\"},{\"v\":\"\u001b[1;5C\"}]";
            Assert.Equal(arr.Length - 1, JsonText.FindMatchingBracket(arr, 0),
                "FindMatchingBracket 跳过字符串内的 '['");

            // 3) 字符串数组：\" 不再被当成结束引号，且读侧反转义
            var tags = "\"a\\\"b\", \"cd\"";
            var items = JsonText.SplitStringArray(tags);
            Assert.Equal(2, items.Count, "SplitStringArray 切出 2 个元素");
            Assert.Equal("a\"b", items[0], "SplitStringArray 反转义引号");
            Assert.Equal("cd", items[1], "SplitStringArray 第二个元素");

            // 4) 端到端：含括号的命令模板 + 含引号的 tag，落盘重载后必须原样
            var dir = NewTempDir();
            try
            {
                var path = Path.Combine(dir, "templates.json");
                var store = new CommandTemplateStore(path);
                store.Load();
                var t = new CommandTemplate();
                t.Id = "t-bracket";
                t.Name = "括号模板";
                t.Command = "ls [a-z]*";
                t.Category = "通用";
                t.Tags.Add("a\"b");
                store.Add(t);
                store.Save();

                var store2 = new CommandTemplateStore(path);
                store2.Load();
                var back = store2.GetById("t-bracket");
                Assert.True(back != null, "含括号的命令模板读回");
                Assert.Equal("ls [a-z]*", back.Command, "command 中的 '[' 原样往返");
                Assert.Equal(1, back.Tags.Count, "tags 数量往返");
                Assert.Equal("a\"b", back.Tags[0], "tag 中的引号往返");
                store.Dispose();
                store2.Dispose();
            }
            finally
            {
                TryDelete(dir);
            }
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
