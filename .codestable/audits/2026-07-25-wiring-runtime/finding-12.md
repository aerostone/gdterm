---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "arch-02"
nature: arch-drift
severity: P1
confidence: high
suggested_action: cs-refactor
status: partial
---

# Finding 12：UI/Tools 分层泄漏 SSH.NET 与 new 具体类

## 速答

UI 直接 `new RdpClient` / `new SerialSession` / `TerminalSessionFactory.CreateLocal`；`PortForwardPanel` 与 `IRemoteToolModule` 暴露 `Renci.SshNet.SshClient`；无 `ITunnelManager`。

## 关键证据

- `TabContainerControl`：`var rdp = new RdpClient()`。
- `TerminalControl`：Serial 直 `new SerialSession()`，工厂可为 null 时 `new TerminalSession()`。
- `PortForwardPanel.SetSshClient(SshClient)`；`IRemoteToolModule.SetSshSession(SshClient)`。
- `TunnelManager` 具体类贯穿 UI，接口注释中的 ITunnelManager 不存在。

## 影响

无法替换隧道/RDP 实现；Tools 与 UI 绑定 SSH.NET 类型，升级库成本高。

## 修复方向

引入 `ITunnelManager` / `IRdpClientFactory`；Tools 用会话抽象或命令执行接口替代 `SshClient`。

## 建议动作

`cs-refactor`。

## 修复状态

- **status**: `partial`
- **note**: d4bd629 ITunnelManager; Rdp/Serial factories and SshClient leak remain
