---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "arch-01"
nature: arch-drift
severity: P1
confidence: high
suggested_action: cs-refactor
status: open
---

# Finding 11：UI 下转 TerminalSession.UnderlyingClient

## 速答

`TabActiveSessionQuery` 将 `ITerminalSession` 转为 `TerminalSession` 取 `UnderlyingClient` 再 Wrap，基础设施类型进入 UI Services。

## 关键证据

- `TabActiveSessionQuery.cs`：`as TerminalSession` + `UnderlyingClient`  
- 对照 attention：运维工具走 ISshRemoteSession，端口转发走 ISshPortForwardHost  

## 影响

Terminal 内部改 SSH.NET 适配会 ripple 到 UI；分层回潮。

## 修复方向

`ITerminalSession` 暴露 `ISshRemoteSession`/`ISshPortForwardHost` 工厂方法，UI 只碰接口。

## 建议动作

`cs-refactor`。
