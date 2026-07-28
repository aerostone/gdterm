# Change Package 规范

Change Package 把一次变更的设计、执行、验证和结果聚合为一个主文档，避免一个 feature / issue / finding 拆成多个文件。

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

单个 Markdown 不超过 300 行；超过时按“设计 / 证据 / 审计性质”拆分，不按每条 finding 拆分。

## Frontmatter

```yaml
---
doc_type: change
kind: feature | issue | refactor | audit
slug: YYYY-MM-DD-{slug}
status: draft | approved | in-progress | accepted | closed
phase: optional kind-specific phase
mode: lean | standard
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

standard 的 design / impl / accept 必须有 `contract`。`include` 锁定代码和产物范围；`preexisting_changes` 只记录 design 开始前已有且不属于本任务的脏文件，并配套 `baseline` 快照。快照保证任务后继续修改同一脏文件时不会被整体忽略；可用 `check-compliance.py --snapshot` 生成。架构与需求影响声明为 `update` 时，验收前必须实际更新对应 refs。`context_refs` 是阶段最小上下文白名单，可用字符串或 `{path, headings, symbols}`，避免后续阶段重新搜索整个知识库。

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

## 旧格式兼容

已有 `.codestable/features/`、`issues/`、`refactors/`、`audits/` 只读兼容。技能读取时优先查 `changes/`，找不到再查旧目录；新任务不再创建旧格式。历史文档不自动迁移、不批量重写。

## 审计规则

一个 audit package 只有一个 `change.md`。Finding 以内嵌章节呈现；主文档超过 300 行时按 `findings-security.md`、`findings-bug.md` 等性质分组，禁止一条 finding 一个文件。
