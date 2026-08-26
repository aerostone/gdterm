<!-- codestable:managed 受管文件——由 `codestable update` 维护；项目内的修改会被更新器保留（跳过刷新），要恢复跟随技能包更新请运行 `codestable update --force`。 -->
# Change Package 规范

Change Package 把一次变更的设计、执行、验证和结果聚合为一个主文档，避免一个 feature / issue / finding 拆成多个文件。对单个既有变更的后续复查，直接追加审计附录，不另建包。

## 目录

```text
.codestable/changes/YYYY-MM-DD-{slug}/
└── change.md
```

大型变更或需要机器状态管理时才增加：

```text
├── checks.yaml
└── evidence/{topic}.md
```

单个 Markdown 不超过 300 行；超过时按“设计 / 证据 / 审计性质”拆分，不按每条 finding 拆分。合规检查工具会拒绝超过上限的 `change.md`。

## Frontmatter

```yaml
---
doc_type: change
kind: feature | issue | refactor | audit
slug: YYYY-MM-DD-{slug}
status: draft | approved | in-progress | accepted | closed
phase: optional kind-specific phase
mode: lean | standard
model_route:                       # standard 可选；缺失等同 direct
  strategy: direct | retain-high | delegate-low | mixed
  reason: {为什么当前改动值得或不值得切换模型}
  high_model: optional declared high-tier model id
  low_model: optional declared low-tier model id
  user_confirmation: pending | approved # 下放前记录用户明确同意；非下放可省略
  low_steps: [step-id]             # delegate-low / mixed 时至少三项
risk:
  level: low | medium | high
  reasons: [public_api | migration | security | data_change | cross_module | no_tests]
roadmap: optional roadmap slug
roadmap_item: optional item slug
contract:
  include: [明确允许修改的 glob，至少一项；含本 change 目录]
  exclude: [明确禁止修改的 glob]
  preexisting_changes: [任务开始前已有的脏文件；通常为空]
  baseline:
    git_head: {任务开始时 HEAD；无首提交时为 unborn}
    dirty_hashes: {preexisting_changes 中每个文件的 sha256}
  architecture_impact: unchanged | update | not-applicable
  architecture_reason: unchanged/not-applicable 时必填
  architecture_refs: [update 时至少一项]
  requirement_impact: unchanged | update | not-applicable
  requirement_reason: unchanged/not-applicable 时必填
  requirement_refs: [update 时至少一项]
  baseline_impact:                 # 可选；项目启用 baseline 层（baseline_mode != off）时填写
    docs: [srs | arch | detail | database | interface | test | ops | manual]
    reason: {一句话说明为什么影响这些基线文档}
  context_refs:
    design: [本阶段额外必读文件]
    impl: [本阶段额外必读文件]
    accept: [本阶段额外必读文件]
  artifacts:
    - id: requirement
      path: .codestable/requirements/{slug}.md
      depends_on: []
    - id: design
      path: .codestable/changes/YYYY-MM-DD-{slug}/change.md
      depends_on: [requirement]
  evidence_ledger: false
---
```

lean 小任务默认不创建 Change Package；用户要求留档或命中自动升级条件时创建 standard 包。

standard 的 design / impl / accept 必须有 `contract`。`include` 锁定代码和产物范围；`preexisting_changes` 只记录 design 开始前已有且不属于本任务的脏文件，并配套 `baseline` 快照。先创建最小 `change.md`，再一次性回填所选脏文件清单和哈希，避免手抄或只更新其中一项：

```bash
python .codestable/tools/check-compliance.py \
  --change {change-dir} --snapshot \
  --snapshot-files path/a,path/b --write-baseline --agent
```

`--write-baseline` 只接受当前确实脏的文件，并会同时写入 `preexisting_changes`、`baseline.git_head` 和 `baseline.dirty_hashes`；没有 preexisting 文件时省略 `--snapshot-files`，仍运行该命令写入空列表和任务起点，不要捕获整仓哈希。架构与需求影响声明为 `update` 时，验收前必须实际更新对应 refs。`context_refs` 是阶段最小上下文白名单，可用字符串或 `{path, headings, symbols}`，避免后续阶段重新搜索整个知识库。

## 模型路由与下放

模型路由输出供应商无关的分派契约，不会直接控制某个代理运行时：`direct` 表示当前执行者直接完成；`retain-high` 表示复杂但小的改动保留高能力层；`delegate-low` 表示高能力层设计后下放一批低风险实现；`mixed` 表示同一包保留高能力步骤、下放其余低风险步骤。`high_model` / `low_model` 只能引用 `model-profile.yaml` 中对应 tier 的声明；未写时选该 tier 唯一或 `default: true` 的模型。

只有低风险、已有明确接口与验证、且至少三步可连续实施时，才使用 `delegate-low` 或 `mixed`。安全、迁移、公开 API、数据变更、跨模块、无测试和 L3 算法步骤不得下放。切换前必须先征得用户确认，再把 `user_confirmation` 写为 `approved`；简单任务不产生切换提示。该字段是可审计记录，不代表工具能检测实际平台模型。

下放步骤放入 `checks.yaml`，每项必须有 `executor_tier: low`、`instruction_level: 1|2`、`risk: low`、`target_files`、`context_refs`、`validation_commands` 和 `exit_signal`；需要工具时写 `required_capabilities: [file_read, file_edit, command_execution, test_execution]` 的子集。`--record-low-result step-id=failure` 在包内 `execution-state.json` 留最小回执；连续两次失败后，`--dispatch-plan` 自动改为高阶复核。不要继续试错或手改失败次数。

## 主文档章节

```markdown
# {标题}

## 1. 目标与边界
## 2. 行为增量
### ADDED
### MODIFIED
### REMOVED
## 3. 设计与契约
## 4. 执行计划
- [ ] {步骤} — {退出信号}
## 5. 执行证据
## 6. 验收结果
## 7. 遗留事项
```

行为增量只写对使用者或调用方可观察的变化。行为等价 refactor 写 `无（行为保持不变）`。验收收敛时不得保留“待补充”。

需要机器状态或并行执行时，`checks.yaml` 的 step 使用稳定 `id` 和 `depends_on`；无依赖 step 属于同一 wave，可并行执行。正文小任务仍用 checkbox，不强制创建 YAML。跨 requirement/design/implementation/acceptance 的关系写入 `artifacts`，验收时检查依赖节点和路径。

## 收敛辅助产物

### evidence.jsonl

只有 contract 声明 `evidence_ledger: true` 时创建，逐行记录：

```json
{"step":"api","command":"pytest tests/auth","exit_code":0,"assertion":"12 passed"}
```

### acceptance scenarios

```bash
python .codestable/tools/check-compliance.py --change {change-dir} --scenarios --agent
```

它从行为增量的 bullet 生成待确认场景，不替代真实测试；验收时未确认的场景仍属于遗留项。

不同类型只启用相关章节：

- feature：设计、步骤、验收
- issue：现象、根因、方案、修复、验证
- refactor：扫描、方法、步骤、行为等价验证
- audit：范围、总评、Finding 章节、Verification Evidence

## 状态机

```text
draft → approved → in-progress → accepted → closed
```

`lean` 快速通道可以不落盘；一旦创建 standard 包，阶段状态必须与主文档章节和 checks 状态一致。终态不能静默回退，需新建变更或显式记录原因。

`accepted` 表示验收门禁已经在任务基线之上通过；获得用户提交授权后，才可将实现和该状态提交。需要关闭档案时，在代码提交后把包设为 `closed`，再次运行 accept 检查并只提交关闭更新。检查器会继续以任务基线比较，不能为消除提交后的 diff 而改写原始 baseline。

## 提交范围

未得到用户明确授权时，不提交。获准后先检查暂存区，再用路径限定提交，避免已有暂存内容被带入：

```bash
git diff --cached --name-only
git add -- {本次允许路径}
git diff --cached --name-only
git commit --only -m "{message}" -- {本次允许路径}
```

脏暂存区中禁止裸用 `git commit`。提交前后的范围均以 Change Package 的 `contract.include` 为准；范围不符时停下并修正，不重写用户已有暂存内容。

## 旧格式兼容

已有 `.codestable/features/`、`issues/`、`refactors/`、`audits/` 只读兼容。技能读取时优先查 `changes/`，找不到再查旧目录；新任务不再创建旧格式。历史文档不自动迁移、不批量重写。

## 审计规则

先区分后续复查与独立审计：

- **后续复查**：只验证一个已知 Change Package 的拆分、验收或回归结果时，在原 `change.md` 追加 `## 后续审计（YYYY-MM-DD）`。追加会超 300 行时，在原包写 `evidence/post-audit-YYYY-MM-DD.md` 并链接。原包状态保持 `accepted` 或 `closed`，不得为补证据回退；发现修复需求时新建 issue/change。
- **独立审计**：范围跨多个变更、需要独立追溯，或用户明确要求独立报告时，创建 `kind: audit` 包。

一个独立 audit package 只有一个 `change.md`。Finding 以内嵌章节呈现；主文档超过 300 行时按 `findings-security.md`、`findings-bug.md` 等性质分组，禁止一条 finding 一个文件。

## baseline_impact（可选）

`contract.baseline_impact` 声明本变更对 `docs/baseline/` 交付基线的影响。只有项目 `attention.md` 的 `baseline_mode` 非 `off` 时才填写。

- lean 任务默认不填——不触发 baseline 投影
- standard 任务：`cs-feat-design` 在声明 `architecture_impact: update` 或 `requirement_impact: update` 时，同时声明 `baseline_impact.docs`
- 字段缺失或 `docs` 为空 = 不影响基线，`cs-feat-accept` 不写 `.baseline-state.yaml` 的 `pending_changes`
- 三个 impact 字段（architecture / requirement / baseline）独立，可任意组合

完整的 baseline 命令、投影流程和追溯机制见 `.codestable/reference/baseline.md` 及其配套文件。

## 相关文档

- `.codestable/reference/baseline.md` — 交付基线层总纲
- `.codestable/reference/baseline-commands.md` — baseline 命令与投影流程（含 baseline_impact 衔接）
- `.codestable/reference/shared-conventions.md` — 目录结构、元数据、档位权威版本