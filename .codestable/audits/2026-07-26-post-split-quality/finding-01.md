---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-01"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 01：TerminalControl.Connect async void 关签竞态

## 速答

`Connect()` 为 `async void`，`await` 之后不检查 `_disposed`。关标签可与连接完成竞态，导致已 dispose 的控件仍持有活 SSH 会话。

## 关键证据

- `src/Gdterm.UI/Controls/TerminalControl.cs`：`Connect` 开头检查 `_disposed`，但 `await Task.Run(...Connect...)` 之后直接 `_session = session` 并订阅 `OutputReceived`、触发 `SessionConnected`。
- 关签路径 `Dispose` 会清 `_session`；若 Connect 在 Dispose 之后完成，会重新赋值并泄漏 hop/会话。

## 影响

僵尸 SSH 连接、健康监控绑到已关标签、隧道引用计数错误。

## 修复方向

await 后若 `_disposed` 则立刻 `session.Dispose()` 并 return；或改 `async Task` + CancellationToken 与关签联动。

## 建议动作

`cs-issue`。
