using System;
using System.Linq;
using Gdterm.UI.Services;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// 回收站 store（F16 加分项「撤销/回收站」）：
    ///   入站倒序 / 恢复分发 / 无 restorer 放回 / restorer 抛异常放回 / 批量删 /
    ///   上限 300 FIFO / null payload 拒收 / ItemAdded 事件。
    /// 用 new 实例测（Default 是应用级单例，测它会把 CI 状态搞脏）。
    /// </summary>
    public static class TrashStoreTests
    {
        public static void Run()
        {
            var t = new TrashStore();

            // 1. null/空 payload 拒收
            t.Add("x", "X", "desc", null);
            t.Add("", "X", "desc", new object());
            Assert.True(t.Count == 0, "null payload / 空分类拒收");

            // 2. 入站 + 时间倒序
            t.Add("cat", "分类", "第一条", new object());
            var second = new object();
            t.Add("cat", "分类", "第二条", second);
            Assert.True(t.Count == 2, "两条入站");
            var list = t.List();
            Assert.True(ReferenceEquals(list[0].Payload, second), "新条目在队首（倒序）");

            // 3. 正常恢复：restorer 被调、条目出栈
            string restored = null;
            t.RegisterRestorer("cat", entry => restored = entry.Description);
            string err;
            bool ok = t.TryRestore(list[0].Id, out err);
            Assert.True(ok && err == null, "恢复成功无错");
            Assert.True(restored == "第二条", "restorer 拿到条目描述");
            Assert.True(t.Count == 1, "恢复后条目出栈");

            // 4. 无 restorer 的分类 → false + 放回
            t.Add("nohandler", "无通道", "条目", new object());
            var noh = t.List()[0];
            ok = t.TryRestore(noh.Id, out err);
            Assert.True(!ok, "无 restorer → 失败");
            Assert.True(err != null && err.Length > 0, "失败带原因");
            Assert.True(t.Count == 2 && t.List().Any(x => x.Id == noh.Id), "失败条目放回");

            // 5. restorer 抛异常 → false + 放回
            t.RegisterRestorer("boom", entry => throw new InvalidOperationException("炸"));
            t.Add("boom", "炸", "条目", new object());
            var boom = t.List()[0];
            ok = t.TryRestore(boom.Id, out err);
            Assert.True(!ok && err != null && err.Contains("炸"), "restorer 异常 → 失败带原因");
            Assert.True(t.List().Any(x => x.Id == boom.Id), "异常条目放回");

            // 6. 已不存在的 id
            ok = t.TryRestore("tr-00000000", out err);
            Assert.True(!ok && err != null, "不存在的 id → 失败");

            // 7. RemoveMany / Clear
            t.Clear();
            Assert.True(t.Count == 0, "Clear 清空");
            t.Add("a", "A", "1", new object());
            t.Add("b", "B", "2", new object());
            var ids = t.List().Select(x => x.Id).ToList();
            t.RemoveMany(new[] { ids[0] });
            Assert.True(t.Count == 1, "RemoveMany 删一条");
            t.Clear();

            // 8. 上限 300：加 305 条，最旧 5 条被 FIFO 淘汰
            for (int i = 0; i < 305; i++) t.Add("c", "C", "e" + i, i);
            Assert.True(t.Count == 300, "上限 300");
            var tail = t.List();
            Assert.True(Equals(tail[0].Payload, 304) && Equals(tail[299].Payload, 5),
                "新条目在队首、最旧 5 条被淘汰");

            // 9. ItemAdded 事件
            int events = 0;
            EventHandler<TrashStore.Entry> h = (s, e) => events++;
            t.ItemAdded += h;
            t.Add("d", "D", "ev", new object());
            Assert.True(events == 1, "ItemAdded 触发一次");
            t.ItemAdded -= h;

            // 10. Default 单例存在且 API 可用（不深测，避免污染应用级状态）
            Assert.True(TrashStore.Default != null, "Default 单例存在");
        }
    }
}
