---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "perf-01"
nature: performance
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 06：重连阻塞 UI + 无多标签资源闸

## 速答

Watchdog 重连经 UI 同步路径，单次等待至 20s，MaxRetries=5 可造成数分钟级卡顿（再叠加 finding-01 死锁）。会话恢复会批量 `OpenConnection` 全部历史标签，代码侧无 max-tabs / 内存预算闸。

## 关键证据

- `TabReconnectService` DefaultTimeoutSeconds=20, PollIntervalMs=200 + GetResult
- `AutoReconnectWatchdog.MaxRetries` 默认 5
- `SessionStateCoordinator.Restore` 循环 OpenConnection，外层 `catch { }`
- 内存目标 30–80MB 仅靠 buffer/pause 约定，无硬上限

## 影响

弱网多标签时整个客户端不可交互；恢复风暴。

## 修复方向

全异步重连队列；并发重连上限 1–2；标签软上限与恢复限流。

## 建议动作

`cs-issue`（可与 finding-01 同 issue）。
