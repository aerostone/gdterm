# gdterm 编译与发布

目标：**.NET Framework 4.6.2** 单文件夹绿色版，Win7 / Server 2008+。

## 前置

- Windows + Visual Studio 2017+ 或 Build Tools
- .NET Framework 4.6.2 Developer Pack
- NuGet CLI（还原 SSH.NET / KeePassLib；项目使用 PackageReference + `RestoreProjectStyle`）

Linux 开发机**不能**编译；只改源码与 `.csproj`，在 Windows 上 MSBuild。

## 编译

```bat
cd /d C:\path\to\gdterm
nuget restore gdterm.sln
msbuild gdterm.sln /t:Restore /p:Configuration=Release /p:Platform="Any CPU"
msbuild gdterm.sln /p:Configuration=Release /p:Platform="Any CPU" /m
```

依赖：
- SSH.NET 2024.1.0（Tunnel / Terminal / Sftp / Tools）
- KeePassLib **2.30.0**（官方 NuGet 最新可用版；代码已按 2.30 API）
- VtNetCore.dll 在仓库 `lib\`（无 NuGet）
- RDP ActiveX **不参与编译**（`RdpClient` 运行时反射加载 `AxMsRdpClient8`）

输出：

| 项目 | 路径 |
|------|------|
| 主程序 | `src\Gdterm.UI\bin\Release\Gdterm.UI.exe` |
| 单元测试 | `src\Gdterm.Tests\bin\Release\Gdterm.Tests.exe` |

## 测试

零 NuGet 控制台 runner（不依赖 NUnit）：

```bat
src\Gdterm.Tests\bin\Release\Gdterm.Tests.exe
```

当前覆盖：`DefaultPorts`、`LogSanitizer` CLI 脱敏、`ConnectionStoreJson` 往返（且断言无 password 字段）、`TerminalProfile` 双轨、`VtTerminalEngine` Phase0（truecolor/256/alt-screen/DA）、`CredentialPayload`/`SecretFinding`/`PBKDF2`。

## AppVeyor CI

仓库根 `appveyor.yml`：VS2022 镜像、Release 编 `gdterm.sln`、跑 `Gdterm.Tests.exe`、打包 `dist/gdterm` artifact（含 `VtNetCore.dll` + LICENSE）。

接入：AppVeyor 绑定本仓库后推送 `master`/`main` 即可；无需本机 MSBuild。

## 打包绿色版

```powershell
powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1
```

产物：`dist\gdterm\`（exe + 依赖 + 空 `data\` 骨架 + `README-PORTABLE.txt`）。

跳过测试：

```powershell
powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -SkipTests
```

## 常见坑

1. **手写 csproj 漏 `Compile Include`** —— 新 `.cs` 必须进对应 csproj，否则 Windows 编译缺类型。
2. **ProjectGuid 必须是合法十六进制** —— 非法字符会导致 sln 加载失败。
3. **`gdterm.sln` 每个项目都要有 `Build.0`** —— 否则 “生成解决方案” 只编到部分工程。
4. **SSH.NET / KeePassLib** 需在 Windows 上 `nuget restore` + `msbuild /t:Restore`；勿提交 packages。
4b. **Gdterm.Tools.csproj** 必须用标准 `Microsoft.Common.props` + 末尾 `Microsoft.CSharp.targets`，禁止 `$(MSBuildExtensionsPath)\$(MSBuildToolsPath)\...` 拼接（会炸 MSBuild）。
4c. **Core 不得引用 `RdpOptions`**（在 `Gdterm.Rdp.Models`）；模板扩展用 `OptionMetadata`。
5. **便携数据** 全在 exe 旁 `data\`；发布包不要带真实 `gdterm.kdbx` / 主密码 hash。
6. **VtNetCore.dll** 必须在 `lib\` 且随 UI 输出/pack 拷贝；缺 DLL 则 cell 终端无法加载。
7. **默认渲染器 VtCell**；极低内存可 Metadata `renderer=lightweight`。

## 诊断日志

- 位置：程序主目录 `logs\`（绿色版便携，随包携带）
- 未处理异常：`logs/crash.jsonl`
- 被吞异常（dispose/关签/关闭）：`swallowed:*` 源写入同一 `crash.jsonl`（`DiagLog`）
- 业务审计：`logs/audit-*.jsonl`
