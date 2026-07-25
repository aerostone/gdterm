---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "bug-06"
nature: bug
severity: P1
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 14：ProtocolType 枚举命名与 UI 调用不一致

## 速答

枚举定义为 `RDP`/`SSH`/`Serial`，`ConnectionDialog` / `ConnectionTreeControl` 使用 `ProtocolType.Rdp` / `.Ssh` — 编译期错误，连接编辑与树图标映射路径断裂。

## 关键证据

- `src/Gdterm.Core/Enums/ProtocolType.cs`：`RDP = 0`, `SSH = 1`, `Serial = 2`。
- `ConnectionDialog.cs:243-244,290`：`ProtocolType.Rdp` / `ProtocolType.Ssh`。
- `ConnectionTreeControl.cs:164-165`：`case ProtocolType.Ssh` / `ProtocolType.Rdp`。

## 影响

Windows MSBuild 失败；协议选择与图标映射不可用。与 finding-04 同属“接线后契约未对齐”簇。

## 修复方向

统一为枚举真实名称（或全局 rename 为 Pascal 风格并改枚举定义一处）。

## 建议动作

`cs-issue`（与 finding-04 可同一修复 PR）。

## 修复状态

- **status**: `resolved`
- **note**: 08b913d ProtocolType.SSH/RDP naming
