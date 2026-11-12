using System;
using System.Collections.Generic;
using Gdterm.Core;

namespace Gdterm.Tests.Core
{
    /// <summary>
    /// L10n 词典自检。
    ///
    /// 为什么单测要管这个：词典是 C# 集合初始化器，**重复键会在静态构造时抛
    /// ArgumentException**——一次手滑的重复词条足以让整个程序起不来，编译器完全不管。
    /// 补词条是高频手工动作（本仓库一轮补过 160+ 条），没有运行时自检就只能等用户
    /// 报"打开就崩"。所以这里第一件事就是把 Strings.En 构造出来。
    ///
    /// 另外两条例外清单（不是"允许偷懒"，是"这些条目本来就该这样"）：
    ///   EmptyValues   拼接式文案的英文侧不需要前缀（中文"正在"+"上传" → 英文 "Uploading"）；
    ///   SameAsKey     标点片段、语言名、字体预览样例，翻译反而错。
    /// 例外必须显式登记——新出现一个空译文时这条断言要能报警。
    /// </summary>
    public static class L10nDictionaryTests
    {
        /// <summary>译文可以合法为空的条目（拼接文案的英文侧无需前缀）。</summary>
        private static readonly HashSet<string> EmptyAllowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "正在",          // "正在"+方向+"文件名" → 英文用 direction 自带词形（Uploading …）
            " 的 OTP 条目"   // "未找到 Id 为 "+id+" 的 OTP 条目" → 英文由前缀句收尾
        };

        /// <summary>译文可以与原文相同的条目（标点片段 / 语言名 / 字体样例）。</summary>
        private static readonly HashSet<string> SameAsKeyAllowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Ping", "Top", "OK", "URL", "ssh", "scp", "sftp", "rsync", "nohup", "s",
            "English", "  (", " ?", "AaBbCc 0123 预览 Preview"
        };

        public static void Run()
        {
            Dictionary<string, string> dict;
            try
            {
                dict = Strings.En; // 重复键在这里抛 ArgumentException
            }
            catch (Exception ex)
            {
                Assert.True(false, "Strings.En 静态构造失败（很可能有重复键）: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            Assert.True(dict != null && dict.Count > 500, "词典条目数异常（应 >500）: " + (dict == null ? "null" : dict.Count.ToString()));

            var unexpectedEmpty = new List<string>();
            var untranslated = new List<string>();
            foreach (var kvp in dict)
            {
                if (string.IsNullOrEmpty(kvp.Value))
                {
                    if (!EmptyAllowed.Contains(kvp.Key)) unexpectedEmpty.Add(kvp.Key);
                    continue;
                }
                if (string.Equals(kvp.Key, kvp.Value, StringComparison.Ordinal) && !SameAsKeyAllowed.Contains(kvp.Key))
                    untranslated.Add(kvp.Key);
            }

            Assert.Equal(0, unexpectedEmpty.Count,
                "有新出现的空译文（要么补英文，要么登记进 EmptyAllowed）: " + string.Join(" | ", unexpectedEmpty.GetRange(0, Math.Min(5, unexpectedEmpty.Count))));
            Assert.Equal(0, untranslated.Count,
                "有译文与原文相同的条目（疑似复制粘贴未改）: " + string.Join(" | ", untranslated.GetRange(0, Math.Min(5, untranslated.Count))));

            // 中文模式下 T() 原样返回（不做任何替换）
            Assert.Equal("确定", L10n.T("确定"), "中文模式下 L10n.T 不该改写原文");
        }
    }
}
