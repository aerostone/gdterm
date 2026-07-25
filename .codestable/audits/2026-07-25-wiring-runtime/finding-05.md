---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "performance-01"
nature: performance
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 05：关标签不 Close 隧道，hop 会话堆积

## 速答

`CloseTab` 释放控件/Health/Watchdog，但从不 `_tunnelManager.CloseAsync(config.Id)`；跳板 SSH 与本地转发常驻到进程退出或同 Id 被覆盖 Dispose。

## 关键证据

- `TabContainerControl.CloseTab`：无 `CloseAsync`。
- `TunnelManager.EstablishAsync`：同 `connectionId` 会 Dispose 旧会话（与 SFTP/重连抢槽叠加，见 bug 隧道单槽）。
- UI 建立隧道：`TerminalControl.Connect`、`SftpBrowserPanel`、RDP `PendingConnect`。

## 影响

8h+ 多开多关标签 → 端口占用 + hop 连接堆积 → 内存/句柄上升，低配机“内存爆炸”。

## 修复方向

关标签/关 SFTP/关 RDP 时按 connectionId 引用计数或明确 Close；SFTP 与终端共享隧道时不要互杀。

## 建议动作

`cs-issue`。

## 修复状态

- **status**: `resolved`
- **note**: 3784467 CloseAsync last-tab + tunnel reuse
