---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "bug-03"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 04：IAuditLogger 调用签名错误，审计整套未接线

## 速答

接口是 `LogConnection(id, host, protocol, ConnectionAction)`，UI 调用 `(id, name, host, bool)`；`LogCommand` / `LogCredentialUse` / `LogSecurityEvent` / `LogAiInteraction` 业务零调用 — 故障与安全事件基本不落盘。

## 关键证据

- `IAuditLogger.cs:14`：`void LogConnection(string connectionId, string host, string protocol, ConnectionAction action);`
- `TerminalControl.cs:186,192`：`LogConnection(..., true/false)`
- `TabContainerControl.cs:140`：同错误四参数 + bool
- 全库 `LogCommand(` / `LogSecurityEvent(` 除接口实现外无业务调用
- 危险命令仅对话框；重连失败/RDP 失败仅 MessageBox 或空 catch

## 影响

可观察性归零；Windows 编译应直接失败（bool ≠ ConnectionAction）；运维无法事后追责。

## 修复方向

调用改为 `ConnectionAction.Open/Error/Close`；在连接/凭据/危险命令/重连/RDP 失败点补齐日志。

## 建议动作

`cs-issue`（契约对齐 + 观测接线），同时属 arch-drift。

## 修复状态

- **status**: `resolved`
- **note**: 08b913d LogConnection(ConnectionAction); LogCommand wired d4bd629
