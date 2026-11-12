---
doc_type: change
kind: audit
slug: 2026-11-11-architecture-health-audit
status: closed
mode: standard
risk:
  level: low
  reasons: []
roadmap: architecture-health
contract:
  include:
    - .codestable/changes/2026-11-11-architecture-health-audit/**
  exclude:
    - src/**
  preexisting_changes: []
  baseline:
    git_head: 6db7c1c
    dirty_hashes: {}
  architecture_impact: not-applicable
  architecture_reason: 审计只读，不修改架构
  requirement_impact: not-applicable
  requirement_reason: 审计只读，不修改需求
  context_refs: []
  artifacts: []
  evidence_ledger: false
---

# 架构健康度审计（2026-11-11）

## 1. 范围

- **范围**：`src/` 13 个生产项目（304 个 `.cs`，排除 `Gdterm.Tests`/obj/bin）；重点取样依赖方向、持久化边界、组合根注入、终端渲染栈
- **模式**：Architecture Health（Standard 预算），4 维：依赖方向 / 职责内聚 / 接口负担 / 变更扩散
- **基线**：HEAD `6db7c1c`（2026-11-08）
- **前置**：`2026-08-11-architecture-audit`（Standard，全 5 维）。本报告不重报其已记录条目（MAINT-01 除外，见 F1 关系说明）

## 2. Findings

### F1 [P1 bug, high] 高亮规则持久化往返不对称 — 从第二次启动起内置 10 条规则全部失效

- **位置**
  - 写入：`src/Gdterm.Connections/HighlightStore.cs:71` → `Escape(r.Pattern)`
  - 转义：`HighlightStore.cs:179-183` — `.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n")`
  - 反转义（**不成对**）：`HighlightStore.cs:143` — 只还原 `\"` 与 `\n`，**从不还原 `\\`**；且用 `IndexOf("\"", start+1)` 取结束引号，不跳过转义字符
- **触发条件**：目录 `data/config/highlight.json` 已存在（即第二次启动以后）。首次启动走 `GetDefault()` 直接返回内存对象，正则正确，同时 `Save()` 把 `\` 转成 `\\` 落盘
- **证据链**（复刻两个方法的 .NET `String.Replace` 语义，输入取内置默认规则 `src/Gdterm.Core/Models/HighlightRule.cs:86`）：

  ```
  写入文件行 :       "pattern": "(?i)\\b(ERROR|FATAL)\\b",
  读回内存   : (?i)\\b(ERROR|FATAL)\\b      ← 双反斜杠，已非原值
  等于原值?  : False
  正则仍匹配 "an ERROR occurred"? False
  ```
- **消费路径可达**：`src/Gdterm.UI/Controls/TabContainerControl.cs:313-314` 每个标签连上即 `_highlightStore.Load()` → `TerminalControl.SetHighlightRules` → 同时 `SetTriggerRules`。高亮规则面板同样以 `Load()/Save()` 为唯一入口（`HighlightRulePanel.cs:32/41/144/165/187/199`）
- **影响**
  - 10 条内置规则（`HighlightRule.cs:84-110`，全部含 `\b`/`\d`/`\s`）与用户自定义正则，在第二次启动后静默不匹配；
  - P1-7 触发器（通知 / 发命令）共用同一份 Pattern，一并静默失效；
  - 每在面板保存一次，反斜杠再翻一倍，配置串逐次膨胀（`\\b` → `\\\\b` …）。
- **同根因的其它实例**（同一"手写序列化裸写"根因，未逐个取证坏结果，不单列 finding）
  - `src/Gdterm.Connections/ConnectionStoreJson.cs:290-294`：`Escape` 不转义 `\n`，含换行的 Notes/metadata 写出即非法 JSON（当前仅靠自家宽松扫描器读回）
  - `src/Gdterm.Connections/QuickCommandStore.cs:212`：`EscapeJson` 直接丢弃 `\r`
  - `src/Gdterm.Connections/ConnectionStoreJson.cs:493-497`：`Unescape` 先替 `\"` 再替 `\\`，字面 `\` 结尾的值边界会错
  - 11 个 store 共 12 份手写 Escape/Extract 实现，转义集合 5 种互不相同；`src/Gdterm.Tests/Connections/` 只覆盖 `ConnectionStoreJson`，其余 10 个 store 无往返测试
- **与既有记录关系**：`2026-08-11-architecture-audit` MAINT-01 已把"手写 JSON ×4"记为 P2 且未修；本次首次取证出可触发坏结果，故升 P1。性质按 `bug` 计（正确性缺陷），结构根因不变
- **建议路由**：cs-issue（先补 1 条 HighlightStore 往返单测 + 修正反转义）→ 长期交 cs-refactor 收拢为单一序列化层
- **置信**：high（写入/读取两端口径差异与消费路径均由代码确证；坏结果由等价复刻复现）

### F2 [P2 arch-debt, high] `SshConnectionInfoFactory` 94 行逐字重复（Terminal + Sftp 各一份）

- **位置**：`src/Gdterm.Terminal/SshConnectionInfoFactory.cs:1-94` 与 `src/Gdterm.Sftp/SshConnectionInfoFactory.cs:1-94`
- **证据**：`diff` 两份文件，唯一差异是第 6 行 `namespace Gdterm.Terminal` / `Gdterm.Sftp`
- **触发面**：该文件承载凭据/代理决策矩阵——私钥 + passphrase 分支、代理认证（`ProxyTypes` + proxy 用户名/密码重载）、`info.Timeout`。任一处改动（新增 `ProxyKind`、改超时来源、修私钥边界）都必须同步两份
- **影响**：漏改不会编译报错，只会表现为"终端连同一主机成功、SFTP 失败"或代理行为分叉。当前两份尚无漂移，属潜在分叉点
- **反证**：`Gdterm.Sftp` 不引用 `Gdterm.Terminal`（避免拖入渲染栈），所以重复是分层约束下的取舍，不是随手复制；但 12 个 store 之外再把凭据矩阵写两遍，维护成本与分叉风险仍成立
- **建议路由**：cs-refactor（抽出共享 SSH 连接构造单元，或由 Terminal 暴露 `internal` + `InternalsVisibleTo` 之外的正式接缝）
- **置信**：high（重复为逐字事实；分叉尚未发生，影响为预测）

### F3 [P2 arch-drift, medium] `Gdterm.Connections` 反向依赖 `Gdterm.Security`，架构依赖表未记录

- **位置**：`src/Gdterm.Connections/Gdterm.Connections.csproj` 的 `ProjectReference` 含 `Gdterm.Security`；唯一使用点 `src/Gdterm.Connections/OtpStoreJson.cs:6`（`using Gdterm.Security.Otp`）
- **文档约束**：`.codestable/architecture/ARCHITECTURE.md:60`「依赖方向：**只允许向下**」；同文件 §3 表格把 Connections 依赖写为「Core」，依赖图中 `Gdterm.Security` 只连 `CORE`。`docs/architecture.md` 的 mermaid 同结论
- **坏结果**：(1) L1 基础设施向上依赖 L2 功能模块——`Security`（12 文件，含主密码、闲锁、危险命令、密文扫描、OTP 算法）任一改动都会牵连 Connections 重建；(2) 单独复用/测试 Connections 必须拖入 Security；(3) 按依赖表新增依赖的人不会知道这条边存在
- **反证**：`OtpEntry` 不是纯 POCO——`src/Gdterm.Security/Otp/OtpEntry.cs:38` 有 `CodeAt()`，含 TOTP/HOTP 计算，所以"模型放错位置"不能简单判定；但正因如此，才需要么下移到 Core（模型）/ 独立项目（算法），么在依赖表中显式记录并说明理由
- **建议路由**：cs-refactor（优先显式记录；若下移则 Core 增加 OTP 模型、算法留 Security）
- **置信**：medium（依赖事实与文档冲突为 high；实际故障路径不存在，影响限于维护成本）

### F4 [P2 arch-debt, medium] 组合根火炬传递 + 双轨注入（23 参 → 14 参，后加依赖改走 setter）

- **位置**
  - `src/Gdterm.UI/Forms/MainForm.cs:91-114`（23 个构造参数）、`:130-153`（逐字段赋值）
  - `src/Gdterm.UI/Controls/TabContainerControl.cs:75-89`（14 个构造参数），其中约 14 项由 MainForm 原样透传
  - 旁路注入：`src/Gdterm.UI/Services/ToolsDialogsLauncher.cs:69/76`（`SetConnectionIntegration` / `SetOtpStore`），调用点 `MainForm.cs:473/477`
- **坏结果**：新增一个 Store 需改 3-4 处（Program 构造、MainForm 参数 + 字段 + 赋值、TabContainer 参数 + 字段）；两套注入形态并存且无规则，后来者无法判断"新依赖走构造还是 setter"（Otp 就是撞到构造上限后改走 setter）；MainForm 对十余个依赖只是搬运，却承担了由此产生的耦合与签名变更成本
- **反证**：调用者只有 `Program.cs` 一处，成本集中、尚未外溢到多模块，故不升 P1
- **建议路由**：cs-refactor（参数对象或容器内构造）
- **置信**：medium（参数数量与透传路径为 high；"新增依赖需改几处"由 Otp 一路可核）

## 3. 已核查但未计入 finding

| 候选 | 结论 | 理由 |
|---|---|---|
| `src/Gdterm.Terminal/TerminalControl.cs`（272）+ `Rendering/TerminalRenderer.cs`（257） | 死栈：除彼此外无任何引用（`csproj:86,102` 仍参与编译），与 UI 侧同名 `TerminalControl` 并存 | 属 Deep 模式单列项；本次 Standard 不计入，建议单独确认后删除 |
| `docs/architecture.md` 各项目文件数（UI 40+ / Terminal 18 / Connections 10 …实为 118 / 46 / 17）；`.codestable/architecture/ARCHITECTURE.md` 写"12 个 csproj"（实为 13） | 文档过期 | 文档一致性归 cs-arch，本模式不改文档、不据此判代码缺陷 |
| `IToolModule.CreatePanel()` 反向耦合 WinForms | 已记录（ARCHITECTURE.md D3 / 2026-08-25 F11） | 不重报 |
| `SshNetRemoteSession` 双职责（既有 ARCH-01） | 仍存在 | 不重报；本次未复核其是否已拆分 |
| Terminal/UI 各 40+ 文件、MainForm 1192 行、TerminalControl 1701 行 | 仅规模线索 | 行数不足以定罪；UI Services 解构仍在，未观察到由此产生的具体改动路径 |

## 4. 总评（范围限定）

**覆盖范围内结构基本可控，属"局部结构债务"，不是屎山。** 13 个 csproj 的引用图只有 F3 一条越层边，无循环；组合根集中、协议工厂隔离、异常统一走 `DiagLog` 管道，与 2026-08-11 结论一致。

- **最高风险**：F1。它把"没有共享序列化契约"从维护性问题变成可触发的功能失效（高亮 + 触发器从第二个会话起全哑），且配置会自我膨胀。这是唯一 P1
- **已观察的变更成本**：新增 Store 需跨 3-4 处签名（F4 有 Otp 实证）；凭据/代理语义改动需同步两份文件（F2 为预测，尚未漂移）
- **未核查维度**：启动/发布耦合（未读 appveyor/打包脚本）、AI/RDP/扫描中心插件链内部规则、并发与运行时故障传播
- **判断边界**：本结论只对上述 4 维、上述取样边界成立；覆盖 ~30% 文件深查（依赖/持久化/组合根/渲染栈），其余为表面扫描

### 覆盖简表

| 关键行为 / 边界 | 深度 |
|---|---|
| 项目引用与依赖方向（13 csproj 全量） | 深查 |
| 持久化边界（11 store、手写序列化、往返） | 深查 |
| 组合根与 UI 容器注入 | 深查 |
| 终端渲染双栈 | 深查（结论：库内一份死代码） |
| 会话/标签生命周期、UI Services | 表面扫描 |
| Security / KeePass / Logging 内部规则 | 表面扫描 |
| 启动/发布、AI/RDP/插件链 | 未覆盖 |

## 5. 验证证据

- **实际文件**：13 个生产项目共 304 个 `.cs`（`find src -name '*.cs'` 排除 Tests/obj/bin；`Gdterm.Tests` 34 个单独计入但未审）
- **维度**：依赖方向 / 职责内聚 / 接口负担 / 变更扩散（Architecture Health 取样）
- **agent 使用**：0（主 agent 完成，未派子 agent）
- **候选 → finding**：候选 9 条 → 复核淘汰 5 条（死栈、文档过期、规模线索、已记录 D3/ARCH-01）→ 去重后 4 条（F1 合并 5 个同根因序列化实例）
- **主 agent 回读文件**：`HighlightStore.cs`(1-200)、`HighlightRule.cs`(60-140)、`ConnectionStoreJson.cs`(275-310,380-520)、`QuickCommandStore.cs`(160-230)、`HighlightRuleConfig` 默认规则、`TabContainerControl.cs`(75-150,300-320)、`MainForm.cs`(90-185,460-480)、`TabContainerControl`/`MainForm` ctor 参数表、两份 `SshConnectionInfoFactory.cs`（diff）、`Connections.csproj`/各 csproj `ProjectReference`、`OtpEntry.cs`(1-40)、`ToolsDialogsLauncher.cs`(60-80)、`Gdterm.Terminal.csproj`(80-105)
- **读取的架构文档**：`.codestable/attention.md`（启动契约 + workflow_mode）、`.codestable/architecture/ARCHITECTURE.md`(1-130)、`docs/architecture.md`(全文 80 行)、`2026-08-11-architecture-audit/change.md`
- **动态验证**：**未执行**。本机无 Windows/net462 构建环境；F1 用 Python 严格复刻 `String.Replace` 语义复现（输出见 F1）。未跑 `Gdterm.Tests`、未启动 UI
- **未修改代码**：是

## 6. 建议动作

| Finding | 级别 | 路由 |
|---|---|---|
| F1 高亮规则往返不对称（手写序列化无契约） | P1 / high | cs-issue（往返单测 + 修反转义） |
| F2 SshConnectionInfoFactory 双份 | P2 / high | cs-refactor |
| F3 Connections → Security 越层依赖 | P2 / medium | cs-refactor（或 cs-arch 记录该边） |
| F4 组合根火炬传递 + 双轨注入 | P2 / medium | cs-refactor |

## 7. 修复记录（同日执行，F1-F4 全量闭环）

> 审计只读；本节记录随后按建议动作实施的修复。基线 `6db7c1c`，改动未提交。

### F1[P1] 手写 JSON 转义/反转义不对称 —— 已修（新建共享契约）

- **新基座** `src/Gdterm.Core/Serialization/JsonText.cs`（`namespace Gdterm.Core`，零依赖）：
  `Escape` / `Unescape` / `SkipString` / `FindMatchingBracket` / `ExtractString`，契约
  `Escape(Unescape(x)) == x`。
  - `ExtractString` 的硬约束：冒号后必须紧跟（允许空白）一个引号，否则返回 **null**——
    调用方普遍写 `JsonText.ExtractString(...) ?? 本地裸标量扫描`，若对 `"k":true` 顺手返回后面
    那个字符串的值，裸布尔/数字会被读成别的字段（修复过程中发现并堵住的姊妹坑）。
- **23 个手写 JSON 的生产文件**全部改走该契约（原各自一份 Escape/Extract，转义集合互不相同）：
  - 12 个 `Gdterm.Connections` store：`HighlightStore` / `ConnectionStoreJson` / `BookmarkStoreJson` /
    `SessionStateStore` / `QuickCommandStore` / `CommandTemplateStore` / `TemplateStore` /
    `LogonScriptStore` / `FolderCredentialStoreJson` / `TerminalKeyBindingStore` /
    `ExternalFormatImporter`（`x[e-1] != '\\'` 结束引号误判 + 反转义顺序）
  - `Gdterm.AI`：`AiModelStore` / `AiSettingsFile` / `ChatHistoryStore` / `AiAssistantService`
  - `Gdterm.Logging/AuditLogger`、`Gdterm.Security/DangerousCommandDetector`（原**完全不反转义**，
    `\d\s` 读回变 `\\d\\s`，黑名单正则静默失配）、`Gdterm.Security/Otp/OtpStoreJson`
  - `Gdterm.Terminal/MacroRecorder`（原不转 `\b`/`\f`/控制符）、`MultiChannelRecorder`
  - `Gdterm.Tools/ToolConfigBase`（原只转 `\ \" \n`，多行值读回是字面 `\n`；`ExtractStringList`
    元素不反转义）
  - `Gdterm.UI/ImportExport/ConnectionImporterExporter`（导出转 `\\`、导入只还原 `\"` →
    连接名 `C:\temp` 往返变 `C:\\temp`；`FindMatchingBrace` 数到字符串里的 `{}`）
- **回归测试** `src/Gdterm.Tests/Connections/JsonRoundTripTests.cs`（与 `JsonRoundTrip` 用例同组）：
  基座往返、高亮规则跨重启、快捷命令控制符、连接元数据，新增
  `BookmarkStoreRoundTrip`（裸 `isFavorite`/`success`/`connectCount` 仍读回）、
  `KeyBindingValueRoundTrip`（`\u001b[A\t…` 逐字节往返）、
  `DangerousRulePatternRoundTrip`（正则原样且规则仍命中）。
- **残留**：已被旧版本加倍转义写入的 `data/config/*.json` 不会自愈（读回稳定但内容仍是双反斜杠）；
  需要时按"删除后用默认重建"处理，未自动改写用户文件。

### F2[P2] `SshConnectionInfoFactory` 三份实现 —— 已收敛为一份

- `git mv` 到 `src/Gdterm.Tunnel/SshConnectionInfoFactory.cs`（Tunnel 是 Terminal/Sftp 的共同下游，
  不反向依赖），两份逐字节副本删除，5 处调用点改为 `Gdterm.Tunnel.SshConnectionInfoFactory.Create`。
- 顺带收掉第三份：`Gdterm.Tunnel/Models/TunnelSession.cs` 的 `ConnectDirect` 与 hop 连接原先各手写
  一遍同样的私钥/密码/代理分支（并自带一份 `TryMapProxy`），现两处都走工厂，本地副本删除。
  直连语义等价（`credential` 为空时传空 `CredentialPayload`，与原先 `credential?.` 行为一致）。

### F3[P2] `Gdterm.Connections → Gdterm.Security` 越层依赖 —— 已断

- `OtpStoreJson.cs` 迁到 `src/Gdterm.Security/Otp/`（与 `OtpEntry`/`OtpGenerator` 同包，
  原位置是"持久化层依赖安全算法"的倒挂），`Connections` 的 `ProjectReference` 与 `Compile` 项删除。
- 引用方改 `using Gdterm.Security.Otp;`（`Program.cs`、`ToolsDialogsLauncher`、`DialogsSmoke`）。
  复核：去掉该边后无任何项目通过传递引用丢失 `Gdterm.Security`（逐 csproj 扫 `Gdterm.Security` 用法）。

### F4[P2] 组合根火炬传递 + 双轨注入 —— 已统一

- 新增 `src/Gdterm.UI/Services/UiDependencies.cs`：`UiDependencies`（23 项，`RequireCore()` 给出缺参
  明细而不是 NRE）与 `TabDependencies`（13 项，只含容器真正消费的服务；僵尸参数 `AiService` 删除）。
- `MainForm(UiDependencies)`、`TabContainerControl(TabDependencies)` 取代 23/14 个位置参数；
  `ToolsDialogsLauncher` 的 `SetConnectionIntegration` / `SetOtpStore` 旁路删除，改构造函数注入。
  新增一个服务 = `UiDependencies` 加一属性 + `Program.cs` 一行赋值。

### 已按用户确认清除的死栈

- `git rm src/Gdterm.Terminal/TerminalControl.cs`(272) + `src/Gdterm.Terminal/Rendering/TerminalRenderer.cs`(257)
  及两行 `Compile` 项。复核：两文件只互相引用（`TerminalControl:37 new TerminalRenderer()`），
  无反射/字符串引用、无 `.Designer.cs`、无文档引用方；`IRenderer` 接口与
  `TerminalKeyBindingResolver`、`LightweightRenderer`、`CellGdiRenderer` 均有活跃使用方，保留。

### 未处理（需单独决策）
- `docs/architecture.md` 文件数全表过期、`ARCHITECTURE.md` 写"12 个 csproj"实为 13 → 归 cs-arch。

### 本次修复的验证边界

- **未编译、未跑测试**：本机无 Windows/net462 构建环境（无 `dotnet`/`msbuild`/`mono`）。已做的静态核对：
  改动文件括号/引号配平、`Gdterm.Core` 引用在 8 个相关 csproj 均存在、移动后的类型与 `using` 全量 grep 对齐、
  无残留 `SetOtpStore`/`SetConnectionIntegration` 调用、无残留同根因 `Replace("\\"…` 链。
- 往返用例是按真实序列化/反序列化路径写的（`LoadAll`/`Reload` 会重新读盘，不是内存缓存），
  但仍需在 Windows 上 `Gdterm.Tests` 实跑确认。

### F5[P2] 历史 change 驱动腐烂（假红/崩溃）—— 已修

审计在收尾时把仓库里所有可回放的 change 夹具驱动跑了一遍（8 个），发现**没有一个能按文档口径跑绿**。
分成两类，处置不同：

| 驱动 | 症状 | 性质 | 处置 |
|---|---|---|---|
| `2026-09-25-ux-empty-focus/check-ux-empty.py` | FAIL 3/27 | 写法漂移 | 按语义重写 3 条断言 |
| `2026-09-25-ui-cancel-height-unify/check-cancel-height.py` | FAIL 2/18 | 文案已走 `L10n.T()` | 正则接受 `L10n.T("…")` |
| `2026-09-25-ui-fix-audit-findings/check-ui-fix.py` | FAIL 1/18 | `fieldH` 已收到 `FormFontPolicy.FieldHeight` SSOT | 接受 SSOT 写法 |
| `2026-09-25-dead-ui-removal/check-dead-ui.py` | **FileNotFoundError 崩溃** | 断言写死 `SharedMenuTip.cs`（包 G 已改名 `MenuTip.cs`）+ 注释文案被当引用 | 按存在者解析文件名 + 判引用时剔注释行 |
| `2026-09-25-ci-tree-gate/check-ci-gate.py` | FAIL 3 | 请求那份 CI 327 dump + 工作区 diff 恰为契约内文件 | 缺 dump → `SKIP` exit 2（不假装绿/红）+ 头部标注适用范围 |
| `2026-09-25-utf8-encoding-normalization/check-utf8-normalization.py` | FAIL 5/42 | 断言"18 文件正文 == 那次提交的 HEAD blob[3:]" | 头部标注适用范围；**不改判定**（改判定等于伪造当时证据） |
| `2026-09-25-hotkey-ssot-unlock-guide/check-hotkey-ssot.py` | 裸跑 FAIL 3 | 有 `green` 阶段开关，裸跑=RED 预期 | 按文档口径 `green` 跑 → ALL OK |
| `2026-09-25-dangerous-cmd-dock-overlap/check-dock-gate.py` | ALL OK | — | 本轮前一节已修死规则 |

逐条修之前都先确认"当前代码事实是否仍满足断言的原意"，确认满足才改断言（否则就是把回归改成绿）。复核方式：
`SharedMenuTip`→`MenuTip` 改名、`SetEmpty(bool)` 内部即 `Visible = empty`、`AssertNoDockOverlap` 由 `private`
升为 `internal static`（`DialogsSmoke` 复用）、R5 的前缀豁免已被 `2026-09-25-dangerous-cmd-dock-overlap` 主动撤销
（所以原断言守的是一个**故意删掉**的注释串，注定恒红）。

复跑结果：dock-gate / dead-ui(green) / hotkey-ssot(green) / cancel-height / ui-fix / ux-empty 六项 `ALL OK`；
ci-gate `SKIP`(exit 2)；utf8-norm 按标注为"提交绑定、后续提交后预期红"。

### F6[P1 bug 派生] 各 store 自写的"数括号切对象"——字符串里的括号把整块配置切空 —— 已修

F1 只修了转义的对称性，没管**切分**：十来个 store 各自复制了同一段
`for(...) { if (c == '{') depth++; else if (c == '}') depth--; }`，
数括号时**不跳字符串**。值里出现不成对的 `[` / `'{'` 就切坏或整段切空。

实证（本轮用可执行宿主复现，非静态推测）：

| 场景 | 旧代码 | 修复后 |
|---|---|---|
| 终端快捷键 `customBindings[].value = "\u001b[A"`（Esc+[，**产品默认就有这种序列**） | `FAIL 快捷键自定义绑定读回`：`ExtractArrayBlock` 找不到配对 → 返回 null → store 静默回落默认配置，用户自定义绑定全丢、零报错 | `ok` |
| 命令模板 tag 含引号 `a"b` | `FAIL tag 中引号原样 (act=[a\])`：`SplitJsonStrings` 用 `IndexOf('"')` 找结束引号，`\"` 被当成结尾 | `ok` |

复现/验证方式：net8.0 最小宿主（只编 `Gdterm.Core` + `TerminalKeyBindingStore` + `CommandTemplateStore`，
与 WinForms 无关）跑同一份断言 —— `git stash` 旧代码 → 2 红；修完 → `ALL OK`。

处置（单一实现原则）：

- `JsonText` 增 `SplitStringArray`（转义感知 + 反转义，与 `Escape` 配对）。
- 本地切分/配对全部改为调用 `JsonText.SplitObjects` / `JsonText.FindMatchingBracket`
  （原有"返回内层内容、不含首尾花括号"的约定在包装层保留，不改调用方语义）：
  `TerminalKeyBindingStore`(3)、`CommandTemplateStore`(3)、`TemplateStore`、`QuickCommandStore`、
  `ConnectionStoreJson`、`OtpStoreJson`、`MacroRecorder`、`MultiChannelRecorder`、`AiModelStore`、
  `AiSettingsFile`、`LogonScriptStore`(2)、`ExternalFormatImporter`、`ChatHistoryStore`(3)。
- 全仓 `grep depth++` 现在只剩 2 处**非 JSON** 用途（控件树遍历、异常链遍历）。
- 回归锁：`src/Gdterm.Tests/Connections/JsonRoundTripTests.cs` 增
  `StoreSplittingIgnoresBracketsInsideStrings`（JsonText 原语 + 快捷键端到端 + 命令模板端到端）。

验证：`tools/csc-check.sh` 全绿（13 工程 csproj 清单一致）；主门 encoding / csproj-files / ui-ux-lint /
gen-tokens / feature-map 全绿；change 驱动 dock-gate / dead-ui(green) / hotkey-ssot(green) / cancel-height /
ui-fix / ux-empty 全绿（utf8-norm 按标注为提交绑定、ci-gate 缺 dump 故 SKIP）。
本机无 WinForms 运行时，UI 行为仍未验证，需 CI 那一轮 dump。

### F7[P0 安全] 危险命令拦截的 4 条旁路 + 3 个设计缺陷 —— 已修

F1–F6 是"结构债"。这一条不是结构债，是**功能只在一条路上有效**。

#### 审计发现（全部有代码位置）

| # | 问题 | 位置 | 后果 |
|---|---|---|---|
| B1 | 粘贴整行不发 Enter 判定 | `TerminalControl.cs` 粘贴路径直接 `TrySendInput(text)` | **手敲 `rm -rf /` 要点 3 下确认，粘贴进终端直接执行** |
| B2 | 宏回放不过闸门 | `MacroPanel.ReplaySelected` → `rec.ReplayAsync(sess)` | 录制里有什么就跑什么，跨机回放更危险 |
| B3 | 快捷键绑定送出的序列不过闸门 | `TerminalControl.Keyboard.cs:144` | 绑一条 `rm -rf /` 的命令序列即可静默触发 |
| B4 | 登录脚本每次连接自动 Send，完全不判 | `TabSessionLifecycle.TryRunLogonScript` | 脚本里留一条破坏命令 = 每次连上都自动执行一次 |
| B5 | 白名单只有"整条命令精确匹配" | `DangerousCommandDetector.AddToWhitelist` | 对带参数命令形同虚设：`rm -rf /tmp/a` 豁免了，`/tmp/b` 照样弹；用户被逼着点"记住"→ 迅速学会无脑确认 |
| B6 | 内置规则被整份冻进用户配置文件 | 保存时全量落盘，加载时以文件为准 | 之后**任何规则精度修复都到不了老用户机器上**（改代码只影响新装用户） |
| B7 | `rm -rf /` 规则 `\s+/` 把 `/tmp/build` 也算删根 | `DangerousCommandConfig` | 每天误报一次 → 用户学会闭眼点确认 → 整套闸门归零 |
| B8 | **两条内置规则是死的** | `mkfs-format` = `Contains` + `mkfs\.`、`killall-9` = `Contains` + `killall\s+-9` | `Contains` 拿模式去 `IndexOf` 字面量，带反斜杠的串在真命令里不存在 → 界面显示“启用着”，从不触发（`mkfs.ext4 /dev/sdb1` 实测 Safe） |

#### 定性（决定了所有取舍）

这套东西**防手滑，不防绕过**。黑名单正则天然可绕（`base64 -d | sh`、`sudo bash -c`、heredoc、alias）。
所以 KPI 不是"拦住多少攻击"，而是**日常不打扰、关键时刻拦一次**：误报率就是它的生死线。
B5/B7 正是把用户训练成"确认机器"的两个机制，比 B1–B4 更隐蔽也更致命。

#### 处置

1. **入口收敛（消灭旁路）**：新增 `Gdterm.Security.CommandGate`（唯一判定点）+
   `Gdterm.UI.Services.DangerousCommandPrompt`（唯一弹窗/审计/豁免出口）。
   8 个入口全部接入并标 `InputSource`：键盘 / 粘贴 / 宏 / 快捷键绑定 / 登录脚本 / 批量面板 / 广播 / AI。
   `TrySendInput(isCommandLine: true)` 仍是公共收口，模板、快捷命令、AI 面板、侧栏发送因此一次性覆盖。
2. **整批判定**：`GateDecision` 按行切开（`\r\n` / `\n` / `\r` 都算）逐行判，
   弹窗显示"命中 1/20 行"；确认次数取命中行的最高等级而**不是按行累加**
   （粘贴 5 行 `rm -rf` 让用户点 15 次 == 训练他闭眼点）。
3. **豁免分层**：规则级（`rule:<id>` 存配置文件 + 会话级内存 `ExemptRuleForSession`）+ 命令级（保留旧行为）。
   粒度从"整条命令"改成"规则"：豁免 `rm -rf` 这条规则，其它写法也就不再骚扰。
4. **配置架构 v2**：`schemaVersion: 2` = 内置规则只读 + 用户 delta。
   内置规则的 pattern/level/文案以**代码**为准，只继承用户的 `enabled`；用户自建规则原样保留。
   界面侧同步：内置规则禁止编辑/删除（提示改用启用禁用 + 自定义规则）。
5. **死规则复活**：`mkfs-format`（`mkfs\.`）与 `killall-9`（`killall\s+-9`）原本是 `Contains` 配正则转义，
   等于找带反斜杠的字面量，从不命中。改回 `Regex` 语义（`\bmkfs\.` / `\bkillall\s+-9`）。
   回归锁里加了一条“非 Regex 规则不得含反斜杠”的形状断言，防止再写出同一个坑。
6. **规则精度**：`rm-rf-root` 改为 `[-{1,2}\S+\s+]*/(\s|$)`（`/` 后必须空白或行尾）；
   `chmod/chown -R` 同理要求目标就是根；新增 `rm-system-dirs`（`/etc /usr /bin /boot /lib /opt /srv /sys /proc /dev`）。
   `DangerousCommandRule.Exclude` 支持否决模式（把已知安全形态从宽规则里摘出来，不必整条关）。
7. **fail-closed**：判定抛异常 / 弹窗弹不出来 → **阻止发送** + 审计，不再默默放行。
8. **审计补全**：`DangerousCommandConfirmed`（确认执行也留痕，否则事后查不出"这条 rm -rf 是谁点过去的"）、
   `DangerousCommandBlocked`、会话豁免放行 —— 三条都带 `InputSource` 和目标主机。

#### 回归锁

- `src/Gdterm.Tests/Security/CommandGateTests.cs`（9 组）：多行切分/整批判定、精度不误报 `/tmp`、
  真删根仍拦、会话规则豁免、永久 `rule:` 豁免持久化、内置只读、老配置 delta 合并、`Exclude` 否决。
- `tools/check-sec-gate.py`（新增静态门，已进 CI `Static gates`）：
  S01 `new DangerousCommandDialog(` 只准在统一出口；S02 `.Check(` 只准在 Security 层；
  S03 运行时白名单写入只准在设置表单；S04 `InputSource` 每个成员必须有调用点
  —— 防的是**收敛之后重新发散**：加一个"一键跑命令"入口，最省事的写法就是直接构造弹窗。

#### 未做的边界（明确留下）

- **它是防手滑层，不是安全边界**：`echo … | base64 -d | sh`、`sudo bash -c '…'`、heredoc、alias 一律可以绕。
  真要防恶意输入得做 shell 解析（引号/管道/子命令/变量展开），成本远超收益，且仍可绕。
- 多行粘贴目前是"整批拒绝或整批放行"，**没有逐行勾选**（预演清单可读，但不能只执行一半）。
  真要"选择性执行"得改终端写入模型，不在本次范围。
