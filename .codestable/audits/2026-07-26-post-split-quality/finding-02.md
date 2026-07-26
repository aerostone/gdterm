---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "bug-02"
nature: bug
severity: P0
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 02：危险命令检测 fail-open

## 速答

`DangerousCommandDetector.Check` 抛异常时 `TerminalControl.ConfirmIfDangerous` 与 `AiCommandGateBinder` 均 `return true`，命令被放行。

## 关键证据

- `TerminalControl.cs`：`catch { return true; }` 于检测路径  
- `AiCommandGateBinder.cs`：同样 fail-open  

## 影响

配置损坏、正则错误或 detector null 半初始化时，安全闸失效且无提示。

## 修复方向

fail-closed：异常 → 拒绝发送并 `LogSecurityEvent`；或弹不可跳过错误框。

## 建议动作

`cs-issue`。
