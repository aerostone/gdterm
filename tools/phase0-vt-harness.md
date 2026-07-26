# Phase 0 VT 引擎 Harness 手册

## 已落地

| 产物 | 路径 |
|------|------|
| VtNetCore 1.0.30 net46 DLL | `lib/VtNetCore.dll` |
| MIT 许可 | `lib/LICENSE.VtNetCore.txt` |
| 源码参考树（只读） | `third_party/VtNetCore-src/` |
| 引擎封装 | `src/Gdterm.Terminal/Rendering/Vt/VtTerminalEngine.cs` |
| GDI cell 渲染器 | `src/Gdterm.Terminal/Rendering/CellGdiRenderer.cs` |
| 自动化验收 | `src/Gdterm.Tests/Terminal/VtTerminalEngineTests.cs` |

## Windows 构建与测试

```powershell
# 在仓库根
msbuild gdterm.sln /p:Configuration=Release /t:Gdterm_Tests
.\src\Gdterm.Tests\bin\Release\Gdterm.Tests.exe
```

期望输出包含：

```
-- VtTerminalEngine (Phase 0) --
   truecolor spans: ...
   256-color X fg=...
   DA response: ...
```

以及 `ALL OK`。

## 手工 TUI 验收（Phase 0.5 / Phase 1）

自动化覆盖解析与 cell 颜色；下列需 **真实 SSH 会话 + CellGdiRenderer 接线** 后在 UI 验收：

| # | 命令 / 操作 | 期望 |
|---|-------------|------|
| 1 | `echo -e '\e[38;2;255;0;0mRED\e[0m'` | 真红字 |
| 2 | `echo -e '\e[38;5;196m256\e[0m'` | 256 色红 |
| 3 | `vim` 打开/`:q` | 无残影、光标正确 |
| 4 | `htop` 或 `top` | 全屏刷新不花屏 |
| 5 | `tmux` 分屏 | 框线/面板正确 |
| 6 | 远端 `codex` / `opencode` | 可进入 TUI（Phase 3 目标） |

## 内存门禁（Phase 0 默认）

- `MaximumHistoryLines` / `TerminalProfile.ScrollbackLines` 默认 **300**（低配友好），硬顶 **2000**
- 默认渲染 **VtCell**；低配机靠 scrollback + 非活动 tab Pause 控内存，**不要**默认切 Lightweight
- `CellGdiRenderer.Pause()` 停 16ms Timer
- 禁止 RichTextBox 主路径

## 架构接线（下一阶段）

```
ShellStream bytes
  → VtTerminalEngine.Feed
  → SnapshotVisible / CellGdiRenderer.Write
  → SendToHost → session.SendInput
```

`IRenderer` 已由 `CellGdiRenderer` 实现；UI `TerminalControl` 仍用 `LightweightRenderer`，Phase 1 用 `TerminalProfile` 切换。

## Linux 说明

本机无 MSBuild / .NET 4.6.2 runtime 时无法执行测试；静态审查 + Windows CI/本机 pack 为权威结果。
