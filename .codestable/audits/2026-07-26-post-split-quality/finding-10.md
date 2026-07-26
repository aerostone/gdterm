---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "sec-04"
nature: security
severity: P1
confidence: medium
suggested_action: cs-issue
status: open
---

# Finding 10：SecretScan 详情明文匹配内容

## 速答

列表用脱敏，双击详情对话框拼接 `finding.MatchedContent` 全文。

## 关键证据

- `SecretScanPanel.cs` 详情 MessageBox/文本格式化  

## 影响

肩窥/录屏/截图泄露扫描命中的密钥材料。

## 修复方向

默认脱敏 + “显示明文”需主密码再验证。

## 建议动作

`cs-issue`。
