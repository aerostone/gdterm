---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "perf-01"
nature: performance
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 07：重连等待阻塞 UI + DoEvents 重入

## 修复状态

- **status**: `resolved`
- **fix**: `WaitForTerminalConnected` 改为线程池 `Task.Run` + `Task.Delay` 轮询，移除 `Thread.Sleep` 与 `Application.DoEvents`，消除消息泵重入。
