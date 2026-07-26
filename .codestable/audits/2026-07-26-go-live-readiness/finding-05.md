---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "bug-05"
nature: bug
severity: P1
confidence: medium
suggested_action: cs-issue
status: open
---

# Finding 05：会话断线状态机不完整

## 速答

`TabSessionState.IsConnected` 连接成功后置 true，远端掉线不清理；`TerminalSession` 的 `ErrorOccurred` 只写输出文本；`SessionDisconnected` 主要在 Dispose 路径。应用级 HA 几乎只靠 5s 轮询 `ITerminalSession.IsConnected`。

## 关键证据

- `TerminalSession.cs:26` — `IsConnected => _sshClient?.IsConnected && _shellStream != null`
- `TerminalSession.cs:170-179` — ErrorOccurred 仅 OutputReceived 文本
- `TabContainerControl` / lifecycle — 无对 shell EOF 的统一 NotifyConnectionLost
- `AutoReconnectWatchdog` 注释写明依赖外部 Notify；`IsDisconnectMessage` 助手零调用

## 影响

半开连接、假连接、检测延迟；与 finding-01/02 叠加放大误判。

## 修复方向

统一 `ITerminalSession.Disconnected` 事件；UI/Watchdog 订阅；状态位与控件同步。

## 建议动作

`cs-issue`。
