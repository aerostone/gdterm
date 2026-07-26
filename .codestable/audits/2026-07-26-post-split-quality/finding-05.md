---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-03"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 05：分屏后活动终端解析失败

## 速答

`TabSplitService` 把 `session.Control` 换成 `SplitPaneControl`；`TabActiveSessionQuery.GetActiveTerminalControl` 只做 `as TerminalControl` → 恒 null。

## 关键证据

- `TabSplitService.cs`：`session.Control = splitPane`  
- `TabActiveSessionQuery.cs`：`session.Control as TerminalControl`  

## 影响

分屏后：暂停/恢复、QuickBar、AI 门控、重连凭据回填、多通道注册目标终端全部失效。

## 修复方向

从 SplitPane 递归取焦点 `TerminalControl`，或 session 持 `PrimaryTerminal`/`Terminals[]`。

## 建议动作

`cs-issue`。
