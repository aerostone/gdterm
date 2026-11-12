# gdterm data/ 安全说明（试运行）

## 已保护
- 密码 / SSH 私钥：仅存 KeePass `data/gdterm.kdbx`（主密码）
- AI ApiKey：`gdk2:` 主密码 AES（`data/config/ai-models.json`）
- AI 对话历史：明文 JSON（`data/ai-chat-history.json`），只存用户消息与 AI 回复，不含连接上下文注入/凭据；AI 面板「清空」即时落盘清空
- 主密码哈希：PBKDF2-HMAC-SHA256 100k（`data/master-password.json`）
- 连接主机/用户名：有主密码时写 `gdh2:` 保护字段（`data/connections.json`）；旧明文仍可读

## 仍需注意
- 便携目录整夹拷贝等于密钥材料一起带走——请加密磁盘或限制 ACL
- 审计日志默认试运行 debug 开启，可能含主机名与命令（已脱敏密码类 CLI）
- `logs/crash.jsonl` 含诊断信息，分享前请检查
- **会话录制 `data/recordings/*.gtr` 是原始字节流，必然含屏幕上出现过的一切**（命令、输出、偶发的密钥/口令回显）。
  它是显式动作（终端 → 开始录制会话）产出的，默认不录、断开即停；但一旦落盘就是明文证据文件：
  分享前按密钥材料同级对待。诊断包**不包含**该目录（只打 `logs/` 与 `data/config/`）。

## 推荐
- 不要把 `data/` 提交到 git 或公开网盘
- 锁屏后缓存凭据会清除；重连需重新解析 KeePass


## master-password.ini
主密码校验哈希存 INI（`passwordHash`/`salt`/`algorithm`/`iterations`），兼容旧 `master-password.json`。
诊断日志：`logs/diag.log` 人可读文本；审计过程仍可写 jsonl。
