---
doc_type: audit-finding
audit: 2026-07-25-wiring-runtime
finding_id: "performance-02"
nature: performance
severity: P0
confidence: high
suggested_action: cs-issue
status: resolved
---

# Finding 06：MultiChannel 只 Register 不 Unregister

## 速答

`MainForm` 在活动会话变化时 `Register` 全量连接会话，关标签/关闭窗体路径从不 `Unregister`；字典长期持有已 Dispose 的 `ITerminalSession`。

## 关键证据

- `MainForm.OnActiveSessionChanged` / `CreateMultiChannelPanel`：仅 `Register`。
- `MultiChannelManager.Unregister` 存在但 UI 零调用。
- `CloseTab` 无 multi-channel 清理。

## 影响

长期运行引用堆积；同 SessionId 重注册可能抛异常被空 catch 吞掉，广播列表脏数据。

## 修复方向

关标签时 Unregister；Register 前先同步“当前已连接集合”（差量增删）。

## 建议动作

`cs-issue`。

## 修复状态

- **status**: `resolved`
- **note**: 3784467 SessionClosed Unregister + Register idempotent
