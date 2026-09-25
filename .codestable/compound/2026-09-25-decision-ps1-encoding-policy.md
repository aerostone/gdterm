---
doc_type: decision
category: convention
date: 2026-09-25
slug: ps1-encoding-policy
status: active
area: tools/*.ps1 与全仓文本编码口径（承接 2026-09-25-utf8-encoding-normalization）
tags: [encoding, utf8, bom, powershell, convention, tooling]
---

# `.ps1` 保留 BOM，不为了"全仓无 BOM"把中文注释改成 ASCII

## 背景

`2026-09-25-utf8-encoding-normalization` 把全仓文本归一为纯 UTF-8，只留三类 BOM 白名单，
其中一条是「含非 ASCII 的 `.ps1` 必须带 BOM」——依据是 Windows PowerShell 5.1 对
**无 BOM** 脚本按系统 ANSI 码页解释，会把 UTF-8 中文读成乱码甚至直接解码报错
（`'gbk' codec can't decode byte 0x94`），这与 Roslyn（无 BOM 先按 UTF-8 严格解码）和
pwsh 7 的行为都不同。

归一完成后还剩 2 个带 BOM 的 `.ps1`，于是有人（我自己）提出"要不要连它们也清掉，
做到仓库里一个 BOM 都没有"。我最初给出的成本估计是"只是注释里的 `—`/`·`/`→` 和几条
`Write-Host` 中文"，**这个估计是错的**，实测后结论反转。

实测非 ASCII 分布（`git ls-files` 口径）：

| 文件 | 非 ASCII 总数 | 分布 | 代码行 |
|---|---|---|---|
| `tools/build-freerdp.ps1` | **1948** | 36 个注释块、151 行中文注释（另有 1 处在 `throw` 消息里） | **0** |
| `tools/pack-release.ps1` | 33 | 16 行注释 + 4 条 `Write-Host` 字符串 + 1 行 here-string 内容 | **0** |

关键事实：**这两个文件的非 ASCII 全在注释与消息文本里，代码行一处都没有。**
所以"改成纯 ASCII"在语法上绝对安全，但它根本不是一个编码问题——它是一个
**"要不要把这段中文说明删掉 / 翻译掉"** 的问题。

## 决定

**`.ps1` 保留 BOM。不为"仓库里没有 BOM"这个形式目标去清它的非 ASCII。**

WPS 5.1 的无 BOM 按 ANSI 读是**运行时约束**，改不掉；`.ps1` 只可能落在两个状态之一：
带 BOM 且可以用非 ASCII，或纯 ASCII。没有第三个状态，所以真正要选的不是
"要不要 BOM"，而是"要不要保住这些中文说明"。

`tools/build-freerdp.ps1` 的注释不是普通注释，它是这个仓库里**唯一**记录了 FreeRDP
补丁依据的地方，而且是**逐条绑在它描述的 `Replace` 代码旁边**的：

- 抓包实证：`mstsc golden reconnect c57179: SEC_EXCHANGE = 94B, PER len = 0x50 (1B)`
  vs `gdterm redirect c64267: 95B, PER len = 0x8050 (2B)`，以及
  `v0.1.161 黄金样本（被接受的 token 重连 c57179）：80B CR = 19B 头 + 61B token + 8B RDP_NEG_REQ`
- 替换锚点：`锚A: send_pdu/send_data_pdu 共 2 处`、`锚B: rdp_send(channel_id 变量名唯一)`、
  `锚C: rdp_send_message_channel_pdu(messageChannelId 唯一)`
- 供应链判据理由：主判据必须是 yml 内钉死的指纹，"若只比远端 `.sha256`，攻击者把 zip
  和 `.sha256` 一起换掉校验照样通过"，以及"升级 FreeRDP 版本时必须人工核对官方发布页"
- 版本回归坐标：`v0.1.183 首连回归`（PER 长度核算）、`v0.1.149`（凭据残留被踢）、
  `v0.1.92–0.1.107 全部版本 PE 导入表已验证`、issue `#12227`

这些注释是脚本可维护性的前提：改补丁时必须照着锚点原文做字符串替换。

## 评估过的替代方案与被否掉的原因

| | 做法 | 否掉的原因 |
|---|---|---|
| **B** | 把这 151 行中文**翻译成英文 ASCII**，留在原地 | ① 全仓其它代码注释（含 260 个含中文的 `.cs`）都是中文，只有这 2 个 `.ps1` 变英文，是**自己造一个新的不一致**去换掉一个已有的不一致；② 要赌翻译不弄错抓包帧号、字节数、锚点原文——一旦弄错，脚本的替换逻辑就失去依据，而这种错误不会在 CI 里报错 |
| **C** | 中文搬到 `docs/` 新文档，`.ps1` 只留英文 + 指针 | 知识**离开代码现场**。这些注释的价值恰恰在于它和它描述的 `Replace` 行相邻；改脚本的人不会先跳出去读文档再回来核对锚点，结果是"文档里有、实际没人看"，等价于慢性丢失 |
| **A（选定）** | `.ps1` 带 BOM，其余全仓纯 UTF-8 | 代价只是"仓库里有 2 个带 BOM 的文本文件"。而带 BOM 的 UTF-8 **本身就是 UTF-8**（BOM 是 UTF-8 的合法前缀，Python 里就叫 `utf-8-sig`），所以"全仓是 UTF-8"这个目标并未被破坏 |

判断准则记下来，避免下次重算：**形式统一的价值必须与它牺牲掉的信息量相称。**
2 个文件的 BOM 是形式问题；151 行抓包依据是不可再生资产。二者不等价。

## 机检落地（口径不靠自觉）

- `tools/check-encoding.py` **R3**：`ext == ".ps1" and nonascii and not has_bom` → FAIL
  `non-ASCII .ps1 without BOM`；纯 ASCII 的 `.ps1`（如 `tools/gen-version.ps1`）
  带不带 BOM 都不判错。
- `bom_allowed()` 是 BOM 白名单的**唯一事实来源**：`third_party/`·`lib/`·`vendor/`
  前缀、`/fixtures/` 路径段、`.ps1` 扩展名。
- `.editorconfig` 里 `[*.ps1] charset = utf-8-bom` 是**声明**（编辑器保存即正确），
  守卫是**强制**（CI 提交即拦下），两者同口径。
- 该守卫已在 AppVeyor `test_script` 里接线，顺序是
  **跑测试 → 传产物 → 树检门 → 编码门**。

## 复核触发条件

出现下列情况之一时，才值得重新评估本决定（而不是重新估算一遍成本）：

1. `tools/build-freerdp.ps1` 退役，或 FreeRDP 升级到不再需要这些补丁的版本。
2. 这些抓包依据与锚点被**完整**吸收进 `docs/`（目前只有部分重叠：
   `docs/RDP-REDIRECT-INVESTIGATION.md` 覆盖了 FreeRDP 2.11.7 与 WLog 诊断补丁背景，
   但**没有**覆盖 PER 长度编码、凭据清空、GCC 标志位对齐这些锚点）。
3. 全仓明确不再需要支持 Windows PowerShell 5.1 调用（目前 `pack-release.ps1` 的
   文档化用法就是 `powershell -ExecutionPolicy Bypass -File`）。

## 相关文档

- `.codestable/changes/2026-09-25-utf8-encoding-normalization/change.md`（D1 规则来源、D3 声明与强制同口径）
- `.codestable/reference/shared-conventions.md`（知识层命名与归档规则）
