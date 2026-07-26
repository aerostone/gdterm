---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "perf-01"
nature: performance
severity: P0
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 07：重连等待阻塞 UI + DoEvents 重入

## 速答

`TabReconnectService.WaitForTerminalConnected` 在 UI 线程 `Thread.Sleep(200)` + `Application.DoEvents()` 最多约 20s。

## 关键证据

- `TabReconnectService.cs` 轮询循环  
- 同路径亦为 bug（嵌套消息泵可重入关签/重连）  

## 影响

界面冻结；DoEvents 重入导致字典/dispose 竞态。

## 修复方向

async 轮询或 Timer + 完成回调；禁止 Sleep/DoEvents 在 UI 线程。

## 建议动作

`cs-issue`。
