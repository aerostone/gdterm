# gdterm 扫描插件开发指南

扫描中心（工具 → 扫描中心（插件））的插件体系是一套**零依赖脚本协议**：任何能往标准输出
打印文本的脚本都可以成为插件。本文说明插件目录结构、清单格式、输出契约与签名信任机制。

## 1. 插件 = 一个目录

放到用户插件根（扫描中心 → 打开插件目录 即 `data\plugins\scanner\`）：

```
data\plugins\scanner\my-plugin\
├── manifest.json    清单（必需，UTF-8 无 BOM）
├── scan.ps1         脚本本体（文件名随意，manifest.scriptFile 指向它）
└── plugin.sig       官方签名（可选；用户自建插件通常没有）
```

目录名即插件 `id`（约定：小写中划线，如 `check-smbv1`）。内置插件在
`plugins\scanner\`（安装目录下），升级时自动刷新，用户改动会先备份 `.bak`。

## 2. manifest.json

```json
{
  "id": "check-smbv1",
  "name": "SMBv1 启用检测",
  "description": "检测本机是否启用 SMBv1 协议",
  "category": "安全",
  "targets": ["windows"],
  "scriptFile": "scan.ps1",
  "timeoutSeconds": 60,
  "version": "1.0.0",
  "enabled": true
}
```

| 字段 | 必需 | 说明 |
|---|---|---|
| `id` | ✅ | 唯一标识，建议与目录名一致 |
| `name` | ✅ | 列表显示名 |
| `targets` | ✅ | `"windows"`（本机/远端 Windows 同源 ps1）或 `"linux"`（sh）|
| `scriptFile` | ✅ | 相对本清单目录的脚本文件名，**禁止 `..\` 越出插件目录** |
| `description` / `category` / `version` | – | 展示用 |
| `timeoutSeconds` | – | 默认 60 |
| `enabled` | – | false 永久停用 |

## 3. 脚本输出契约（唯一协议）

脚本只做一件事：向 stdout 打印。每行一个发现：

```
FINDING|<severity>|<title>|<detail>
```

- `severity` ∈ `info / low / medium / high / critical`（`warn→medium`、`error→high` 自动归一）
- 其余行原样作为日志显示
- 最多 200 条发现、128KB 日志，超出截断

PowerShell 示例（windows）：

```powershell
$count = 0
if ((Get-SmbServerConfiguration).EnableSMB1Protocol) {
    Write-Output "FINDING|high|SMBv1 已启用|建议禁用 SMBv1（Get-SmbServerConfiguration）"
    $count++
}
Write-Output ('完成: 命中 ' + $count + ' 处')
```

Shell 示例（linux）：

```sh
hits=0
if grep -q "PermitRootLogin yes" /etc/ssh/sshd_config 2>/dev/null; then
  echo "FINDING|medium|SSH 允许 root 直接登录|/etc/ssh/sshd_config PermitRootLogin yes"
  hits=$((hits+1))
fi
echo "done: $hits findings"
```

## 4. 执行通道

同一份脚本按 `targets` 落地到不同目标（扫描中心顶部选择）：

| 通道 | windows 脚本 | linux 脚本 |
|---|---|---|
| 本机 | PowerShell EncodedCommand（免落盘，不受 ExecutionPolicy 限制） | –（开发机自测） |
| SSH 远端 | 远端 powershell -EncodedCommand（需 OpenSSH Server） | base64 内联 → `sh`（≤200KB 零 SFTP 依赖） |
| WMI（备用） | Win32_Process + ADMIN$ 取回（远端无 OpenSSH 时） | – |

注意：`[Console]::OutputEncoding=UTF8` 会由宿主自动前置，无需脚本自带；中文输出不会乱码。

## 5. 签名与信任

| 状态 | 判定 | 行为 |
|---|---|---|
| Trusted | 官方 RSA-3072 签名验签通过 | 静默加载执行 |
| Unsigned | 无 plugin.sig 或非官方 keyId | 首次运行弹确认，批准按 id+内容哈希记账（`data\plugins\config\scanner-approved.json`）；**内容一改重新确认** |
| Invalid | 官方签名但内容哈希不符 | 疑似篡改，**硬拒绝执行** |

另有执行期 TOCTOU 双保险：加载后脚本或 manifest 被替换的，执行前复验哈希失败即拒绝。
内置四插件走真实签名流程；用户自建插件保持 Unsigned 即可，批准一次即可重复运行。

## 6. 调试建议

1. 扫描中心 → 打开插件目录，手工建目录 + manifest + 脚本
2. 保存后插件列表 **800ms 内自动热加载**（无需重启）
3. 先用「本机」+ `targets:["windows"]` 调通，再改 SSH/WMI 远端跑
4. 加载失败（红字）会精确说明原因：缺 manifest / targets 空 / scriptFile 越界 / 脚本不存在

## 7. 安全红线

- 脚本只能放在插件目录内（`..\` 逃逸在加载期即拒绝）
- 不要在脚本里硬编码凭据——通道会以当前连接/会话身份执行
- 输出可能被展示/记录，不要打印敏感值（gdterm 自身日志有脱敏，但插件输出直达 UI）
