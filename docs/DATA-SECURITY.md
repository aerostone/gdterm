# gdterm data/ 安全说明（试运行）

## 已保护
- 密码 / SSH 私钥：仅存 KeePass `data/gdterm.kdbx`（主密码）
- AI ApiKey：`gdk2:` 主密码 AES（`data/config/ai-models.json`）
- 主密码哈希：PBKDF2-HMAC-SHA256 100k（`data/master-password.json`）
- 连接主机/用户名：有主密码时写 `gdh2:` 保护字段（`data/connections.json`）；旧明文仍可读

## 仍需注意
- 便携目录整夹拷贝等于密钥材料一起带走——请加密磁盘或限制 ACL
- 审计日志默认试运行 debug 开启，可能含主机名与命令（已脱敏密码类 CLI）
- `logs/crash.jsonl` 含诊断信息，分享前请检查

## 推荐
- 不要把 `data/` 提交到 git 或公开网盘
- 锁屏后缓存凭据会清除；重连需重新解析 KeePass


## master-password.ini
主密码校验哈希存 INI（`passwordHash`/`salt`/`algorithm`/`iterations`），兼容旧 `master-password.json`。
诊断日志：`logs/diag.log` 人可读文本；审计过程仍可写 jsonl。
