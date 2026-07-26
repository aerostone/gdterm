---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-03"
nature: security
severity: P1
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 09：ApiKey gdk1/明文回退

## 速答

`AiModelStore` 无主密码时写 gdk1 固定 XOR；读路径仍接受无前缀明文。

## 关键证据

- `AiModelStore.cs`：`FixedXorKey` / `ProtectXor` / legacy `return stored`  

## 影响

便携 `ai-models.json` 离线可还原 key。

## 修复方向

强制 gdk2；拒绝新写 gdk1/plain；启动时提示升级。

## 建议动作

`cs-issue`。
