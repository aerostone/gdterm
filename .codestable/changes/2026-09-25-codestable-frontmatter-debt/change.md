---
doc_type: change
kind: refactor
slug: 2026-09-25-codestable-frontmatter-debt
status: accepted
mode: standard
summary: 清掉两项仓库级 frontmatter 债（attention.baseline_mode 引号 + ARCHITECTURE.md 补 frontmatter）
tags: [codestable, hygiene, frontmatter]
risk:
  level: low
  reasons:
    - 只改 .codestable 元数据文件，不动任何产品代码与运行时行为
contract:
  include:
    - .codestable/attention.md
    - .codestable/architecture/ARCHITECTURE.md
    - .codestable/changes/2026-09-25-codestable-frontmatter-debt/**
  exclude: []
  preexisting_changes: [.codestable/.runtime/current-package]
  baseline:
    git_head: 43918f0fcfce5896eebe5d9b218f7f3ec04ea234
    dirty_hashes:
      .codestable/.runtime/current-package: 5c74ea8d2246e4392bf216bebbbf2040a756b91f36c7b96bbedc7924e5ba629f
  architecture_impact: unchanged
  architecture_reason: 只补 frontmatter 元数据，不改架构描述内容、模块边界或依赖方向
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 不涉及能力目标与边界
  requirement_refs: []
  context_refs:
    design: []
    impl: []
    accept: []
  model_route:
    route: retain-high
    reason: 2 步低风险元数据修复、无判断点，但需先证实补入 frontmatter 不会激活级联锚点校验
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-codestable-frontmatter-debt/change.md
      depends_on: []
  evidence_ledger: false
---

# .codestable frontmatter 债清理

## 目标与边界

**目标**：把 check-compliance 在**每一个** change 上都会报的两项仓库级失败清到零，使后续所有包的合规输出只剩本次改动相关的结果。

两项失败在已接受的 `2026-09-24-ui-interactability-assertions` 上同样复现（见该包执行证据），属仓库既存债，与本包无因果关系。

| # | 失败项 | 现状 | 修复 |
|---|---|---|---|
| F1 | `attention.baseline_mode` | `.codestable/attention.md:3` 写 `baseline_mode: off`，YAML 1.1 解析为布尔 `False` | 改为 `baseline_mode: "off"` |
| F2 | `architecture.read` | `.codestable/architecture/ARCHITECTURE.md` 缺起始 `---`，frontmatter 无法解析 | 补 frontmatter 块 |

**边界（不做）**：

- 不修改 ARCHITECTURE.md 的正文内容、章节结构或任何描述
- 不改 `attention.md` 除该行以外的任何内容
- 不动产品代码、测试代码、构建脚本

## 行为增量

**ADDED**

- `.codestable/architecture/ARCHITECTURE.md` 顶部 frontmatter：`doc_type: architecture` / `slug: architecture` / `scope` / `summary` / `status: current` / `last_reviewed: 2026-09-25` / `tags` / `depends_on` / `implements`（字段集取自 cs-arch 参考模板）

**MODIFIED**

- `.codestable/attention.md:3`：`baseline_mode: off` → `baseline_mode: "off"`（字符串化，保持语义完全不变，仅规避 YAML 1.1 布尔陷阱）

**REMOVED**：无

## 设计与契约

### 决策与约束

**D1｜F1 用引号而非换词**：`check-compliance.py:891-900` 接受取值集合 `{off, lean, baseline, regulated}`，只对 `isinstance(baseline_mode, bool)` 报 fail。语义值必须仍是 `off`（该仓库确实不启用交付基线），因此只能字符串化——`"off"`。

**D2｜F2 只补 frontmatter，不补正文节**：`check_architecture`（`check-compliance.py:2112`）在 `read_file` 成功后会对正文跑 `` `path:line` `` 锚点正则。实测 ARCHITECTURE.md 该形式锚点数为 **0**（精确正则 `` r"`([^`\n]+):(\d+)`" `` 命中 0；宽松 `file:line` 裸引用亦为 0），因此补入 frontmatter 后 `architecture.anchors` 仍为 pass，不会级联出新的锚点失败。正文 255 行结构不动——它不是本次债的一部分。

**D3｜不升级为架构 update**：本次不产生架构事实变化，`architecture_impact: unchanged`；若哪天要回填 ARCHITECTURE.md 正文，走 `cs-arch update` 另开。

### 名词与编排

- **F1/F2**：check-compliance 的两条仓库级检查项，与具体 change 无关
- **前置**：先确认锚点数为 0（D2 的证据），再动文件；顺序反了会把"未必安全"当成"已安全"

## 验收契约

- **S1｜F1 归零**：`python3 .codestable/tools/check-compliance.py --root . --change 2026-09-25-codestable-frontmatter-debt --phase accept` 输出中不再出现 `attention.baseline_mode` 失败
- **S2｜F2 归零**：同上输出中不再出现 `architecture.read` 失败，且 `architecture.anchors` 仍为 pass
- **S3｜零实质改动（反向检查）**：`git diff` 中 ARCHITECTURE.md 的变更仅为**新增** frontmatter 行（`-` 行数 = 0）；attention.md 的变更仅 1 行（`baseline_mode` 那行）
- **S4｜无副作用（反向检查）**：产品代码 / 测试 / 构建脚本 0 文件变更；`docs/` 无变更

## 执行计划

- **Step 1｜F1 字符串化**：改 `.codestable/attention.md` 的 `baseline_mode` 取值加引号 → 验证：`check-compliance --phase design` 不再报 `attention.baseline_mode`
- **Step 2｜F2 补 frontmatter**：为 `.codestable/architecture/ARCHITECTURE.md` 加 frontmatter（cs-arch 模板字段），正文一行不动 → 验证：`architecture.read` 转 pass 且 `architecture.anchors` 保持 pass
- **Step 3｜收敛**：跑 `--phase accept`，确认仅剩本包自身相关结果；反向 `git diff --stat` 核对 S3/S4

## 执行证据

### RED（改动前）

```
$ python3 .codestable/tools/check-compliance.py --root . --change 2026-09-25-codestable-frontmatter-debt --phase design
Compliance: fail (33 checks, 2 failures, 0 warnings)
[FAIL] attention.baseline_mode: baseline_mode parsed as boolean False; quote the value (e.g. baseline_mode: "off")
[FAIL] architecture.read: missing opening frontmatter delimiter
```

两条失败在改动前即存在，且与已接受的 `2026-09-24-ui-interactability-assertions` 上报的是同一对——所以它们是仓库级债，不是本包引入。

### Step 1｜F1 字符串化

`sed -i 's/^baseline_mode: off$/baseline_mode: "off"/' .codestable/attention.md`

```
$ head -3 .codestable/attention.md
---
workflow_mode: standard
baseline_mode: "off"
```

同一命令的 `--phase impl` 输出中 `attention.baseline_mode` 已消失，只剩 `architecture.read` 与 `change.phase`（状态未翻转，见 Step 3）。

### Step 2｜F2 补 frontmatter（含正文不变式断言）

补入前先取证 D2 的安全性——正文里的 `path:line` 锚点数：

```
$ python3 -c "import re;print(len(re.findall(r'\`([^\`\n]+):(\d+)\`', open('.codestable/architecture/ARCHITECTURE.md',encoding='utf-8').read())))"
0
```

补 frontmatter 的写入脚本自带反向断言（正文必须逐字节不变）：

```
frontmatter 字节数: 369 正文 sha256 前后一致: True
```

```
$ git diff --stat .codestable/architecture/ARCHITECTURE.md .codestable/attention.md
 .codestable/architecture/ARCHITECTURE.md | 12 ++++++++++++
 .codestable/attention.md                 |  2 +-
 2 files changed, 13 insertions(+), 1 deletion(-)
```

ARCHITECTURE.md 的 12 行全部是新增（`-` 行 0），attention.md 仅 1 行变化——S3 的两条反向检查成立。

### Step 3｜收敛

```
$ python3 .codestable/tools/check-compliance.py --root . --change 2026-09-25-codestable-frontmatter-debt --phase impl
Compliance: pass (33 checks, 0 failures, 0 warnings)
[PASS] architecture.anchors: architecture code anchors are valid
```

`architecture.anchors` 仍为 pass，证实 D2 的判断：补 frontmatter **没有**级联出锚点失败。

### 反向检查（范围外零改动）

```
$ git status --porcelain
 M .codestable/.runtime/current-package
 M .codestable/architecture/ARCHITECTURE.md
 M .codestable/attention.md
?? .codestable/changes/2026-09-25-codestable-frontmatter-debt/
```

工作区只有 4 项：两项修复目标、本包目录、以及已声明为 `preexisting_changes` 的包指针（本次从 `2026-09-25-ux-empty-focus` 指向本包，属预期）。产品代码 / 测试 / 构建脚本 / docs 无变更，S4 成立。

> 记录一次校订：初版这里写的是 `grep -vE` 过滤后的"（空）"，实跑发现 `^ M .codestable/...` 模式不覆盖 `.codestable/.runtime/current-package`（该路径无子目录名匹配），过滤结果非空。证据以实跑原文为准。


## 验收结果

| 项 | 结果 | 证据 |
|---|---|---|
| S1 F1 归零 | pass | `attention.baseline_mode` 不再出现于 accept 输出 |
| S2 F2 归零 | pass | `architecture.read` 转 pass，`architecture.anchors` 保持 pass |
| S3 零实质改动 | pass | `diff --stat` = ARCHITECTURE.md +12/-0 纯新增、attention.md 1 行 |
| S4 无副作用 | pass | 产品代码/测试/脚本/docs 0 文件变更 |

**收敛口径**：本包执行后，`check-compliance --phase accept` 在任何 change 上的输出不再包含这两条仓库级失败；后续包只需关心自身改动引入的结果。

**遗留**：无。本包登记的两项债各自有独立的反向检查，不存在"修了但没验"的项。
