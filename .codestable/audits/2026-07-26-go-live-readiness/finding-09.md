---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "obs-03"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-issue
status: **resolved** (2026-07-26 go-live fix batch)
---

# Finding 09：关键路径空 catch 仍主导，DiagLog 覆盖偏关签/关机

## 速答

全仓约 **148** 处空 catch / **55** 文件。`DiagLog` 约 29 次调用，集中在 TabClose/Program/AppShutdown/TerminalControl Dispose。重连失败中间态、隧道关闭、锁、凭据解析仍黑暗。

## 关键证据

| 路径 | 行为 |
|------|------|
| TabReconnectService CompleteAfterOpen | catch → false，无日志 |
| TabSessionLifecycle CloseTunnelIfLastUser | catch best-effort |
| CredentialResolver | 空 catch → null |
| LockStateCoordinator | 空 catch |
| ConnectionHealthMonitor OnTick | catch { } |
| TerminalControl.SafeSend | catch → false |

## 影响

「为什么没重连 / 隧道没关 / 锁没清」经常无 crash.jsonl 行。

## 修复方向

关键路径统一 `DiagLog.Swallowed(source, ex)`；禁止新增裸 catch。

## 建议动作

`cs-issue`（可批量小步，不必一次清 148）。

## Resolution

Fixed in go-live fix batch (2026-07-26). See commit message for details.
