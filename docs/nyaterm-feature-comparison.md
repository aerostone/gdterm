# gdterm vs nyaterm 功能对比与吸收清单

> 对比对象：[nyaterm](https://github.com/nyakang/nyaterm)（MIT，.NET 8 + Avalonia，活跃，2026-10/11 仍在发版；2026-07 从 SSH 工具转型 AI 协作终端）
> 结论先行：nyaterm 的 AI 能力、加密同步、诊断包值得吸收；**Win7 是 gdterm 独有护城河**（nyaterm 明确不支持，其依赖 Avalonia/FreeRDP/SQLite 全要 Win10+）。
> 本文件为研究落档（docs/，未提交跟踪前曾丢失——2026-11-02 重建并入库）。

## 1. 总览对比

| 维度 | gdterm（本项目） | nyaterm |
|---|---|---|
| 定位 | Windows 终端/远程管理工具（SSH/RDP/SFTP/会话管理） | AI 协作终端（nyai CLI + LSP 插件 + 远程监控） |
| 目标 OS | **Windows 7 SP1**（net462，WinForms + AntdUI 自绘） | Windows 10/11 / macOS / Linux（Avalonia 11，需 .NET 8） |
| 架构 | 多模块 C#（Core/Connections/SSH/RDP/Security/UI），手写 JSON 存储 | Avalonia UI + FreeRDP（vendor/freerdp 完整源码内嵌，~10k 文件） |
| 凭据 | 主密码派生 AES 加密存储（gdo1: 前缀） | KDF 加密数据库 + **端到端加密同步**（多设备） |
| 会话 | 标签页会话 + 分屏 + 命令历史/模板 + 自动重连 | 多工作区 + 会话树 + 剪贴板/终端快照历史（30 天）+ 会话回传 |
| 扩展 | 插件扫描中心 + 工具注册表 | **LSP 插件生态**（终端内语言服务）+ AI Agent 任务（nyai task） |
| AI | AI 助手面板（对话/诊断辅助） | nyai CLI 深度集成（对话+命令+任务生命周期）+ LLM 设置 + 会话历史 |
| SFTP | 文件面板 + 传输（SftpServiceFactory） | 远程文件浏览/编辑 + 文件回传 + 远程监控 + 预览升级 |
| 安全 | 危险命令审批 + 主密码 + 密钥管理 | 危险命令闸 + 远程审批 + **OTP 动态码管理**（2026-11 新增） |
| 配置同步 | 无（单机） | **加密同步**（GitHub 后端，E2E，2026-10 新增） |
| 诊断 | 诊断日志（DiagLog） | **一键诊断包导出**（2026-11 新增） |
| 开源协议 | 私有 | MIT |

## 2. nyaterm 可吸收的优点（按价值/成本排序）

### A 组：低成本高价值（1 个迭代内可落地）

| # | 特性 | nyaterm 做法 | gdterm 吸收方案 | 备注 |
|---|---|---|---|---|
| A1 | 命令面板（Ctrl+Shift+O 式） | 全局快速命令入口 | 工具→命令面板：聚合现有全部对话框/菜单动作，模糊搜索直达 | 复用 ToolRegistry，UI 薄壳 |
| A2 | 外部格式导入 | Xshell/MobaXterm/WindTerm 连接导入 | ConnectionImporterExporter 扩展新格式解析 | 需 fixture 回归 |
| A3-lite | 选中文本 host:port 打开连接 | 终端内动作链接 | 选中检测 host:port → 悬浮按钮「用此地址新建连接」 | 完整动作链接引擎成本过高，只做 lite |
| A4 | 在线搜索/翻译/IP 查询 | 选中→右键→在线服务 | 右键菜单加「搜索/翻译/IP 归属」，走系统默认浏览器 | 零后端依赖 |
| A7 | 诊断包一键导出 | 打包日志/配置/环境信息 | DiagLog 落盘 + 连接清单（脱敏）+ 环境信息 zip | 报障效率直接提升 |

### B 组：中等成本差异化（按批排期）

| # | 特性 | 说明 | 依赖/风险 |
|---|---|---|---|
| B1 | **OTP 动态码管理** | TOTP/HOTP 生成器 + otpauth:// URI 导入 + 管理器界面；密钥走主密码 AES | RFC 4226/6238 向量测试必须；Win7 上无扫码（剪贴板 URI 粘贴即可） |
| B2 | **加密同步**（多设备） | 配置/连接清单 E2E 加密同步（GitHub gist 或用户自有后端） | 主密码基础设施已就绪；需协议设计与冲突处理，走 cs-roadmap |
| B3 | AI Agent 任务集成 | nyai 式任务生命周期（提交/跟踪/回传） | 依赖 AI 助手面板扩面；差异化卖点 |
| B4 | 远程会话监控 | 多会话状态一览 + 告警 | 需后台心跳体系 |
| B5 | 会话回传/快照 | 终端内容快照 30 天可检索 | 存储增长控制 |
| B8 | SFTP 预览升级 | 图片/文本/Markdown 预览 | 复用文件面板 |

### C 组：不做（gdterm 语境下无价值）

- LSP 插件生态（终端工具非 IDE，成本高）
- FreeRDP 内嵌源码策略（gdterm 已有 RDP 客户端方案，vendor 10k 文件不值）
- macOS/Linux 移植（与 Win7 定位冲突）

## 3. gdterm 独有优势（nyaterm 无，保持）

1. **Win7 SP1 支持**（net462 + 自绘 UI 无现代依赖）——nyaterm 明确不做
2. RDP 原生支持（nyaterm 的 FreeRDP 仅 Win10+/跨平台）
3. 危险命令审批闸（nyaterm 2026-10 才加，且无主密码体系）
4. 手写 JSON 零依赖存储（部署简单，无 SQLite 运行时要求）

## 5. 监测体系（UI/UX 质量门禁）

> 详细规范见 `docs/UI-UX-VERIFICATION.md`。本节为对比视角的补强清单。

### 5.1 现有 L0/L1 门禁（已在 CI）

| 层 | 工具 | 检查内容 |
|---|---|---|
| L0 静态 | `tools/ui-ux-lint.py` | 硬编码色值/魔法尺寸/控件命名/字体常量——新增违规即红 |
| L0 静态 | `tools/check-encoding.py` | 文件编码/行尾 |
| L1 冒烟 | `UiSmokeRunner`（CI 无头跑） | 对话框开关、空态、倒计时、脱敏断言 |
| L1 结构 | `tools/ui-tree-check.py` + `UiTreeDumper` | 控件树结构断言（布局回归） |
| 覆盖登记 | `.codestable/tools/ui-coverage-registry.txt` | 每个对话框/表单必须登记自动化或 manual 豁免理由 |

### 5.2 对比 nyaterm 可补强的监测手段

1. **诊断包里的 UI 状态快照**（配 A7）：导出时附当前控件树 dump（UiTreeDumper 复用），报障时 UI 问题可离线复现
2. **smoke 增加「视觉不变量」**：关键窗口截图哈希（CI 环境字体差异大，只断言关键区域非空/非全黑，不做全图比对）
3. **交互路径覆盖矩阵**：每个菜单项/右键项 → 对应 smoke 断言的映射表（防「菜单有、功能无、测试无」三脱节）
4. **RFC/密码学向量测试本地化**：纯逻辑代码（Otp/Base32）提交前用便携 dotnet（`~/dotnet/dotnet` + Roslyn csc）编到 .NET 8 本地跑向量，对照 Python/openssl 交叉验证——不裸奔 CI（2026-11-02 0.1.357 教训）

## 7. 执行记录

### 7.1 状态（2026-11-02 更新）

✅ **第一批 A 组 4 项全部完成**（A1 `26ffb96` / A2 `84e9409` / A3-lite+A4 `20e938b` / A7 `51c9abd`），CI 0.1.354 全绿（ui-ux-lint 门禁还捕获了 3 类新违规并清零，`3ccc511`/`6f2fdea` 为配套修复）。

- **A7**：`DiagnosticsPackageExporter` 一键 zip（诊断报告/脱敏连接清单/终端配置/命令历史/会话元数据 + manifest），敏感字段全掩码、零明文落盘；入口状态栏右键 + 工具菜单；`DiagExport` smoke 断言已入 `DialogsSmoke`
- **B1 OTP 管理器**（`1f192c9`，2026-11-02 第二批第一刀）：
  - `Gdterm.Security/Otp/`：OtpGenerator（RFC 4226 HOTP / RFC 6238 TOTP，SHA1/256/512）+ Base32（RFC 4648 无填充）+ OtpAuthUri（otpauth:// 解析/格式化）+ OtpEntry（CodeAt/Title/gdo1 锁定态）
  - `Gdterm.Connections/OtpStoreJson`：与 ConnectionStoreJson 同构（原子写/版本/备份）+ protector 钩子接主密码 AES
  - UI：`OtpManagerForm`（列表 + 1s 倒计时 + 复制/添加/删除 + 空态引导 + 锁定态）+ `AddOtpForm`（URI 粘贴预填 + 即时校验）+ 工具→「OTP 动态码管理…」
  - 测试：`OtpTests`（RFC 4226 附录 D 全 10 向量 + RFC 6238 附录 B SHA1/256/512×8 位 + Base32 向量 + URI roundtrip + 存储/protector roundtrip）

### 7.2 CI 0.1.356/357 两轮失败复盘（教训已固化）

**0.1.356（编译失败 CS1729）**：OTP 接线 4 文件（MainForm 构造参数/ToolsDialogsLauncher/两个 csproj 登记）漏在工作区，Program.cs 调用端已入库 → CI 参数数不匹配。
教训：**跨文件接线必须整体提交**；本地 csc-check 编译工作树所以全绿。提交前跑
`grep -rln 新符号 src --include='*.cs' | xargs -I{} git status --short {}` 确认无遗漏。

**0.1.357（测试 18 失败）**：三根因，全部本地用便携 dotnet 复现后修复（`7e51b20`）：
1. 测试 key 生成错误（16 个向量失败的根因）：`('1'+i%10)` 在 i=9 生成 `':'`(0x3a) 而非 `'0'`(0x30)，RFC 密钥变 `123456789:123456789:`。期望值是 RFC 字面量、密钥生成却用独立代码路径——**两边必须独立才能互验**
2. **生产 bug**：`OtpStoreJson.Deserialize` 用 ExtractString 读布尔 `totp` 字段 → 错抓下一键 `"counter"` 的字符串 → HOTP 条目重载后恒当 TOTP。新增 ExtractBool（裸/引号布尔均支持）
3. protector 测试断言无效：前缀式假密文必含明文子串。改 base64 假密文

**流程沉淀**：RFC/密码学类纯逻辑代码，提交前本地 `~/dotnet/dotnet` + Roslyn csc 编到 .NET 8 运行时段跑向量（对照 Python/openssl），不再裸奔 CI。

### 7.3 排期（后续）

1. **第二批剩余**：B2 加密同步（用户换机刚需）→ B3 AI Agent
2. **第三批**：B4 远程监控 → B5 回传 → B8 预览升级
3. **架构债**：C1 helper 进程随 RDP 稳定性问题暴露时再做
4. 每批走 cs-feat（安全/数据变更走 standard）；导入/解析类补 fixture 回归（.codestable/changes 惯例）

---

## 附：nyaterm 版本时间线参考（截至 2026-10）

- 2026-07：定位转型 AI 协作终端（nyai CLI 集成）
- 2026-10：加密同步（E2E，GitHub 后端）+ 危险命令审批
- 2026-11：OTP 动态码管理 + 一键诊断包导出
