---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-01"
nature: security
severity: P0
confidence: high
suggested_action: cs-issue
status: open
---

# Finding 03：主密码仅单次 SHA256

## 速答

`SecurityManager.HashPassword` 使用 `SHA256(salt ‖ password)`，无 PBKDF2/scrypt 迭代。便携 `data/master-password.json` 可被离线高速撞库。

## 关键证据

- `src/Gdterm.Security/SecurityManager.cs`：`HashPassword` 单次 `SHA256.Create().ComputeHash`  
- 主密码解锁后即 KeePass + gdk2 ApiKey 材料  

## 影响

U 盘丢失 / data 目录拷贝后，弱主密码被快速破解 → 全库凭据。

## 修复方向

PBKDF2-HMAC-SHA256 ≥10k（与 AiModelStore gdk2 对齐或更高）+ 版本字段迁移旧 hash。

## 建议动作

`cs-issue`。
