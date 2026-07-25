---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "arch-03"
nature: arch-drift
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 13：端口转发 / 远程工具会话注入未闭环

## 速答

菜单可打开 PortForward / Toolbox，但 `SetSshClient` / `SetSshSession` 从未调用；运行时数据面空转。与 review「入口已接通」声明冲突。

## 关键证据

- `MainForm.CreatePortForwardPanel`：`new PortForwardManager()` + `PortForwardPanel`，无 `SetSshClient`。
- `PortForwardPanel.StartSelected`：`_client == null` 直接 return。
- `ToolboxPanel` 仅 `CreatePanel()`，全 UI 无 `SetSshSession` 引用。
- 登录脚本引擎、EnableAutoLog 同属半接线（见 Dead Code 清单）。

## 影响

功能完整度虚假：用户点开面板无效果，运维工具远程模式不可用。

## 修复方向

从活动 SSH 会话取底层客户端或统一 `IRemoteCommandExecutor` 注入侧栏；连接成功时刷新工具会话。

## 建议动作

`cs-issue`（接线闭环）+ 修正 review 声明。

## 修复状态

- **status**: `resolved`
- **note**: 0355592 PortForward SetSshClient + Toolbox SetSshSession
