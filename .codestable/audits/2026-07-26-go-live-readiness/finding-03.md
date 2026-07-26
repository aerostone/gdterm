---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "bug-03"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 03：锁屏 Pause 吞断线，解锁不 re-arm

## 速答

闲锁正确 `PauseAll` 并忽略 `OnConnectionLost`；但 `ResumeAll` **只清 `_paused` 标志**，不重探测、不重放丢失事件。叠加 finding-02 的一次闩锁 → 锁期间掉线的标签解锁后长期僵尸。

## 关键证据

- `AutoReconnectWatchdog.cs:104-114` — PauseAll 取消 CTS、清 IsReconnecting
- `AutoReconnectWatchdog.cs:173-178` — `_paused` 时 `OnConnectionLost` 直接 return
- `AutoReconnectWatchdog.cs:118-123` — ResumeAll 仅 `_paused = false`
- `LockStateCoordinator.cs` — 解锁 KeePass + ResumeAll，无会话探活

## 影响

运维常见：锁屏去开会 → 网络切换 → 回来会话全死且不自动救。

## 修复方向

`ResumeAll` 后对每个 watched session 读 `IsConnected`，失连则 `NotifyConnectionLost`；健康 monitor 同步 MarkConnected/清闩。

## 建议动作

`cs-issue`。
