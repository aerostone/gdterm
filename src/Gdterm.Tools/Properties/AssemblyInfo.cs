using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Gdterm.Tools")]
[assembly: AssemblyDescription("运维工具箱核心框架")]
[assembly: AssemblyProduct("gdterm")]
[assembly: ComVisible(false)]
[assembly: Guid("a1b2c3d4-e5f6-7890-abcd-f12345678901")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// 测试工程可见 internal（StripFindingLines/NormalizeSeverity/FileSha256Hex 等纯函数回归）
[assembly: InternalsVisibleTo("Gdterm.Tests")]
