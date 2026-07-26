---
doc_type: audit-finding
audit: 2026-07-26-go-live-readiness
finding_id: "bug-04"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 04：同 connectionId 并发 EstablishAsync 非原子

## 速答

`TunnelManager.EstablishAsync` 先查复用再构建再写入字典，无跨步骤锁/refcount。SSH 标签 + SFTP 并行、或重连重叠时，可能双建 hop、后写覆盖前写且未稳定 Dispose，留下孤儿 `SshClient`/端口转发。

## 关键证据

- `TunnelManager.cs` — 活跃复用检查与 `_sessions[connectionId]=session` 分离
- `TunnelSession.IsActive` 仅在 `StartPortForwarding` 置 true；无 hop 存活探测
- 复用只看 `IsActive`，不看 hop `SshClient.IsConnected`
- 多跳 `ConnectHop` 临时 `ForwardedPortLocal` Stop/Remove **未 Dispose**（既有泄漏点）

## 影响

多标签同跳板、SFTP+终端 场景下端口/句柄泄漏或一端持有已释放转发。

## 修复方向

per-connectionId 异步锁 + 引用计数；复用前探测 hop；临时转发 using/Dispose。

## 建议动作

`cs-issue`。
