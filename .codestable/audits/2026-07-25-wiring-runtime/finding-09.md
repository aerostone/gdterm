---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "bug-05"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 09：无全局异常钩子 + 关键路径空 catch

## 速答

`Program.Main` 未注册 `Application.ThreadException` / `AppDomain.UnhandledException`；连接/凭据/重连/隧道大量 `catch { }`，故障既不进审计也不进诊断文件。

## 关键证据

- `Program.cs` Main 仅 `EnableVisualStyles`，无全局钩子。
- `TabContainerControl.ResolveCredential` catch 返回 null。
- `AutoReconnectWatchdog` catch 继续循环无日志。
- RDP 失败仅 MessageBox。

## 影响

现场故障不可复盘；与 finding-04 叠加后可观察性接近零。

## 修复方向

注册全局钩子写 `data/logs/crash-*.log` + `LogSecurityEvent`/`Error`；关键 catch 至少记一行。

## 建议动作

`cs-issue`（observability）。

## 修复状态

- **status**: `resolved`
- **note**: 09a9700 CrashLog + ThreadException/UnhandledException
