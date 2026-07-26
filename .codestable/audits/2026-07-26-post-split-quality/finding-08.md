---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "perf-02"
nature: performance
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 08：暂停标签仍向 UI 线程泵输出

## 速答

`TerminalControl.OnTerminalOutput` 在 `_isPaused` 时仍 `BeginInvoke` 每段输出；仅 renderer.Write 被跳过。

## 关键证据

- `TerminalControl.cs`：`InvokeRequired` 分支无判断 pause  

## 影响

多后台会话时 UI 消息队列膨胀、CPU 升高，违背低配/无 GPU 目标。

## 修复方向

pause 时后台合并缓冲，resume 时一次刷；或 pause 直接丢弃非 auto-log 输出。

## 建议动作

`cs-issue`。
