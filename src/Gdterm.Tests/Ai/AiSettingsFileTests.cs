using System;
using System.IO;
using Gdterm.AI;

namespace Gdterm.Tests.Ai
{
    /// <summary>
    /// AI 设置导入/导出（F11）：往返一致 / 掩码导出 Key 留空 / 数组格式兼容 /
    /// 缺字段拒绝 / 特殊字符转义往返。
    /// </summary>
    public static class AiSettingsFileTests
    {
        public static void Run()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-aicfg-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "ai.json");
            try
            {
                // 1. 含 Key 往返
                AiSettingsFile.ExportTo(path, "my-model", "https://api.example.com/v1", "gpt-4", 4096, 0.7, "sk-abc123", includeKey: true);
                var r1 = AiSettingsFile.ImportFrom(path);
                Assert.True(r1.Name == "my-model", "Name 往返");
                Assert.True(r1.Endpoint == "https://api.example.com/v1", "Endpoint 往返");
                Assert.True(r1.Model == "gpt-4", "Model 往返");
                Assert.True(r1.MaxTokens == 4096, "MaxTokens 往返");
                Assert.True(r1.Temperature.HasValue && Math.Abs(r1.Temperature.Value - 0.7) < 1e-9, "Temperature 往返");
                Assert.True(r1.KeyPresent && r1.ApiKey == "sk-abc123", "明文 Key 往返");

                // 2. 掩码导出 → KeyPresent=false
                AiSettingsFile.ExportTo(path, "n", "https://e/v1", "m", 100, 1.0, "sk-secret", includeKey: false);
                var r2 = AiSettingsFile.ImportFrom(path);
                Assert.True(!r2.KeyPresent && r2.ApiKey == "", "掩码导出 Key 留空");
                Assert.True(!File.ReadAllText(path).Contains("sk-secret"), "掩码文件不含明文 Key");

                // 3. 特殊字符转义往返（引号/换行/反斜杠）
                AiSettingsFile.ExportTo(path, "a\"b\nc", "https://e/v1", "m\\x", 1, 0.1, "", true);
                var r3 = AiSettingsFile.ImportFrom(path);
                Assert.True(r3.Name == "a\"b\nc", "引号/换名转义往返");
                Assert.True(r3.Model == "m\\x", "反斜杠转义往返");

                // 4. AiModelStore 数组格式兼容（取第一个对象）
                File.WriteAllText(path,
                    "[{\"Id\":\"x\",\"Name\":\"arr\",\"Endpoint\":\"https://arr/v1\",\"ApiKey\":\"\",\"Model\":\"m1\",\"MaxTokens\":2,\"Temperature\":0.5,\"IsDefault\":true}]",
                    new System.Text.UTF8Encoding(false));
                var r4 = AiSettingsFile.ImportFrom(path);
                Assert.True(r4.Name == "arr" && r4.Model == "m1" && r4.MaxTokens == 2, "数组格式取首对象");

                // 5. 缺 Endpoint/Model → 拒绝
                File.WriteAllText(path, "{\"Name\":\"x\",\"Model\":\"m\"}");
                bool threw = false;
                try { AiSettingsFile.ImportFrom(path); }
                catch (FormatException) { threw = true; }
                Assert.True(threw, "缺 Endpoint 拒绝");

                // 6. 非 JSON → 拒绝
                File.WriteAllText(path, "not json");
                threw = false;
                try { AiSettingsFile.ImportFrom(path); }
                catch (FormatException) { threw = true; }
                Assert.True(threw, "非 JSON 拒绝");

                // 7. 空 endpoint 导出 → 拒绝
                threw = false;
                try { AiSettingsFile.ExportTo(path, "n", "  ", "m", 1, 0.1, "", true); }
                catch (ArgumentException) { threw = true; }
                Assert.True(threw, "空 endpoint 导出拒绝");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
