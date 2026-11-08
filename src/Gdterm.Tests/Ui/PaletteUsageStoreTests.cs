using System;
using System.IO;
using Gdterm.UI.Services;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 命令面板使用频率 store（F10）：
    ///   无文件→零分 / Record 计数 / 磁盘往返持久化 / 损坏文件→空不抛 / 坏行跳过。
    /// </summary>
    public static class PaletteUsageStoreTests
    {
        public static void Run()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-pal-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "palette-usage.json");
            try
            {
                // 1. 文件不存在 → 零分
                var s = new PaletteUsageStore(path);
                int n;
                long t;
                s.Score("a\u001fX", out n, out t);
                Assert.True(n == 0 && t == 0, "无文件 → 零分");

                // 2. Record 三次 → n=3 且 t>0
                for (int i = 0; i < 3; i++) s.Record("a\u001fX");
                s.Score("a\u001fX", out n, out t);
                Assert.True(n == 3 && t > 0, "Record×3 → n=3 t>0");

                // 3. 重新打开 → 从磁盘恢复
                var s2 = new PaletteUsageStore(path);
                s2.Score("a\u001fX", out n, out t);
                Assert.True(n == 3, "磁盘往返 n=3");

                // 4. 文件损坏 → 空统计，不抛
                File.WriteAllText(path, "{corrupt");
                var s3 = new PaletteUsageStore(path);
                s3.Score("a\u001fX", out n, out t);
                Assert.True(n == 0, "损坏 → 空统计");

                // 5. 坏行跳过、好行照读
                File.WriteAllText(path, "; comment\nbadline\nb\t2\t123\n");
                var s4 = new PaletteUsageStore(path);
                s4.Score("b", out n, out t);
                Assert.True(n == 2 && t == 123, "好行照读");
                s4.Score("badline", out n, out t);
                Assert.True(n == 0, "坏行跳过");

                // 6. null/空 key 不抛
                s4.Record(null);
                s4.Record("");
                s4.Score(null, out n, out t);
                Assert.True(n == 0, "null/空 key 安全");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
