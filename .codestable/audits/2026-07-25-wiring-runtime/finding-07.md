---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "bug-04"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 07：自动重连假成功 + 健康监控绑旧 session

## 速答

`ReconnectByIdSync` Close+Open 后立刻 `return true`；SSH 懒连接尚未真正 Connect，Watchdog 停止退避；HealthMonitor 可能仍指向已 Dispose 会话。

## 关键证据

- `TabContainerControl` 重连路径：关 tab → `OpenConnection` → 立即 true。
- `TerminalControl.ResumeRendering` 才 `Connect`。
- `AutoReconnectWatchdog` 以 ReconnectFunc 布尔结果结束重试。

## 影响

断线后 UI 显示“已重连”但会话仍断；运维误判。

## 修复方向

等 `SessionConnected` 或 Connect 完成再回报成功；失败继续退避。

## 建议动作

`cs-issue`。

## 修复状态

- **status**: `resolved`
- **note**: 0cc0c8d ResumeRendering + wait IsConnected 20s
