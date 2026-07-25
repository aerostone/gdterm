---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "bug-01"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 01：危险命令确认形同虚设 + 多入口旁路

## 速答

键盘路径在确认前已把命令体逐字 `SafeSend` 到远端；快捷栏/片段/批量/登录脚本/AI/`AutoRunCommands` 直接 `SendInput`，完全绕过 `DangerousCommandDetector`。

## 关键证据

- `src/Gdterm.UI/Controls/TerminalControl.cs` KeyPress：可打印字符立刻 `SafeSend(e.KeyChar.ToString())`，Enter 才 `Check` + 对话框；拒确认仅 `SafeSend("\x03")`，竞态下远端可能已执行。
- `MainForm` QuickBar / Snippet：`tc.SendInput` 或 session 直发。
- `BatchCommandExecutor` / `LogonScriptEngine` / `AiAssistantService`：`session.SendInput(command + "\r")`。
- `AutoRunCommands` 循环走 `SafeSend`，无检测。

## 影响

运维误触 `rm -rf`、`mkfs`、防火墙清空等规则在“已启用检测”时仍可执行；安全功能给用户虚假信心。

## 修复方向

统一所有发送入口经闸门；键盘改为本地行缓冲，确认后再整行下发（或仅在可拦截协议上缓冲）。

## 建议动作

`cs-issue`，因为是功能正确性 + 安全控制面缺陷，需回归测试多入口。

## 修复状态

- **status**: `resolved`
- **note**: 1fc04f0 local line buffer; QuickBar/Snippet/Batch/AI gates
