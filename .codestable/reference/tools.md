# CodeStable 工具用法参考

本文件由 `cs-onboard` 复制到项目的 `.codestable/reference/tools.md`，所有 CodeStable 子技能用项目相对路径 `.codestable/reference/tools.md` 引用。

`.codestable/tools/` 下共享脚本的完整用法参考。子技能里只写本技能特有的 1-2 行典型查询；完整语法和示例看这里。

`search-yaml.py`、`validate-yaml.py`、`check-compliance.py` 共用 `yaml_support.py`；不要在新脚本里复制 YAML fallback。

升级 CodeStable 后，已有项目用 `npx codestable update` 刷新本目录和 reference；`--dry-run` 预览，`--check` 供 CI 检查。它不迁移或覆盖项目档案。

---

## 1. search-yaml.py

通用 YAML frontmatter 搜索工具。从项目根目录运行，无需安装额外依赖（PyYAML 可选，有则用，无则内建 fallback parser）。

### 基本语法

```bash
python .codestable/tools/search-yaml.py --dir {目录} [--filter key=value]... [--query "全文关键词"] [--sort-by FIELD [--order asc|desc]] [--full] [--json]
```

### filter 语法

- `key=value`：字段精确匹配（大小写不敏感）
- `key~=value`：字符串字段子串匹配；列表字段元素包含匹配
- `key=a|b|c` / `key~=a|b|c`：同一字段多个候选值，候选之间是 OR；在 PowerShell / Bash 中请给整个 filter 加引号，例如 `--filter "doc_type=decision|explore|learning"`

### 排序语法

- `--sort-by FIELD`：按 frontmatter 字段排序（典型字段：`last_reviewed`、`date`、`updated_at`）
- `--order desc|asc`：`desc` 默认，新的在前；`asc` 老的在前（查"谁最久没更新"用这个）
- 字段缺失 / 值为空的文档一律排到最后，不干扰前排结论

### 常用命令

沉淀类文档统一在 `.codestable/compound/`，`cs-knowledge` 管理 learning / trick / decision，`cs-explore` 管理 explore：

```bash
# 按 doc_type 筛选
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=learning
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter "doc_type=decision|explore|learning" --filter status=active
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=decision --filter status=active
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=trick --filter status=active
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=explore --filter status=active

# doc_type + 子技能内部细分字段
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=learning --filter track=pitfall
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=decision --filter category=constraint
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=trick --filter type=pattern
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=explore --filter type=question

# 按 tag（列表元素包含匹配）
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter tags~=prisma

# 全文搜索
python .codestable/tools/search-yaml.py --dir .codestable/compound --query "shadow database"

# 按领域/框架/语言筛选
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=decision --filter area=frontend
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=trick --filter framework~=vue
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=trick --filter language=typescript

# 搜索 feature 方案 doc
python .codestable/tools/search-yaml.py --dir .codestable/features --filter doc_type=feature-design --filter status=approved

# 搜索新版 Change Package
python .codestable/tools/search-yaml.py --dir .codestable/changes --filter doc_type=change --filter kind=feature --filter status=in-progress

# 输出控制
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter doc_type=decision --filter status=active --full
python .codestable/tools/search-yaml.py --dir .codestable/compound --filter tags~=llm --json

# 按时间排序
python .codestable/tools/search-yaml.py --dir .codestable/compound --sort-by date --order desc                     # 最近归档的在前
python .codestable/tools/search-yaml.py --dir .codestable/library-docs --sort-by last_reviewed --order asc         # 最久没 review 的在前（找陈旧文档）
python .codestable/tools/search-yaml.py --dir .codestable/guides --filter status=current --sort-by last_reviewed --order asc
```

### 典型使用场景

| 场景 | 命令建议 |
|---|---|
| feature-design 开始前查已有归档 | 搜 `.codestable/compound` 目录，按 `--query "{关键词}"` 全文搜；要分类看就加 `--filter "doc_type=learning\|trick\|decision\|explore"` |
| issue-analyze 根因分析前查历史 | 搜 `.codestable/compound` `--filter doc_type=learning --filter track=pitfall`、再搜 `--filter doc_type=trick --filter type=library`，按相关组件/框架过滤 |
| 归档落盘后查重叠 | 搜 `.codestable/compound --query "{关键词}" --json`，看有无语义重叠 |
| 新人了解项目规约 | `--dir .codestable/compound --filter doc_type=decision --filter status=active` |
| 按技术栈浏览技巧 | `--dir .codestable/compound --filter doc_type=trick --filter language={语言} --filter status=active` |
| 找最久没 review 的库文档 / 指南 | `--dir {目录} --filter status=current --sort-by last_reviewed --order asc` |
| 看最近沉淀了哪些经验 | `--dir .codestable/compound --filter doc_type=learning --sort-by date --order desc` |

---

## 2. validate-yaml.py

YAML 语法校验工具。用于验证 frontmatter 语法和必填字段。

```bash
# 校验单个文件的 YAML 语法
python .codestable/tools/validate-yaml.py --file {文件路径} --yaml-only

# 校验必填字段
python .codestable/tools/validate-yaml.py --file {文件路径} --require doc_type --require status

# 批量校验目录下所有文件
python .codestable/tools/validate-yaml.py --dir {目录} --require doc_type --require status
```

## 3. check-compliance.py

语义遵循检查工具。Python 3.9+，只读项目文件、`git diff` 和未跟踪文件，不修改代码或文档。它不是 YAML 语法校验的替代品：先用 `validate-yaml.py`，再用本工具检查关系和范围。

```bash
# 检查项目档位、架构目录和当前工作树
python .codestable/tools/check-compliance.py --root .

# 检查某个 feature 的 design/checklist、架构引用和 git diff contract
python .codestable/tools/check-compliance.py --root . --feature .codestable/features/{date-slug}

# 给 agent / CI 使用机器可读结果；严格模式把 warn 视为失败
python .codestable/tools/check-compliance.py --root . --feature {feature-dir} --json --strict

# standard design 完成时检查范围和架构影响契约；--agent 输出精简协议
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --phase design --check --agent

# 在实现/验收阶段强制要求 approved 状态和完整契约，并返回并行 wave
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --phase impl --check --agent --emit failures,waves

# 检查新的 Change Package
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --phase accept --check --agent --emit failures

# 续作时只返回 workflow.yaml 判定的下一技能
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --next --agent --emit next

# 返回当前阶段的最小上下文文件；缺失引用直接失败
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --phase impl --context --profile constrained-27b-64k --agent --emit context,failures

# 任务开始前只生成 preexisting_changes 对应文件的基线快照
python .codestable/tools/check-compliance.py --root . --snapshot --snapshot-files src/a.py,docs/b.md --agent

# 最终收敛：检查任务、行为增量、文档回写和命令证据，并返回 remediation
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --phase accept --converge --agent

# 检查 accepted 包是否可以归档，并输出 artifact merge/remediation 检查结果
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --phase accept --archive --agent

# 从行为增量生成待确认的验收场景
python .codestable/tools/check-compliance.py --root . --change .codestable/changes/{date-slug} --scenarios --agent

# 返回所有 Change Package 的紧凑索引，避免扫描 changes 目录
python .codestable/tools/check-compliance.py --root . --index --agent

# 用户明确需要持久化时写入 changes/index.yaml
python .codestable/tools/check-compliance.py --root . --index --write-index --agent
```

完整 `--json` 适合人和 CI；`--agent` 只返回 status、next、context、failures、remediation、waves，`--emit` 可进一步裁剪字段。`--context` 支持字符串或 `{path, headings, symbols}`；`--next` 不再执行整套检查；`--snapshot` 只读生成任务基线。工具不会声称测试已执行，测试命令仍需实际运行并记录。

存在 `.codestable/constitution.yaml` 时，checker 同时校验长期原则结构、path_rules、required_commands 和 active compound 文档中的术语冲突。术语用 `terminology: {Term: 定义}`，或在 knowledge frontmatter 中写 `terms` mapping。

上下文 profile 由 `.codestable/model-profile.yaml` 选择；checker 按文件数和字符数估算预算，不依赖特定 tokenizer。64K profile 只允许 compact output 和最小 context，不改变 workflow 语义。

项目级路径规则放在 `attention.md` 的 `standards.path_rules` 命名 mapping 中。例如 `domain-no-ui: {files: [src/domain/**], forbidden_terms: [src/ui]}`。它适合检查可文本识别的依赖方向；复杂语义仍交给项目自身 linter/architecture test，并把命令列入 `required_commands`。accept 阶段要求这些命令原文出现在“执行证据”或“验收结果”中；仅写在计划里不算执行证据。

## 4. 路由可观测性（可选）

`route-observer.py` 将 Codex 与 Pi 的路由事件写入 `.codestable/telemetry/routes.jsonl`。事件只含 request id、平台、技能和来源，不写原始 prompt、工具参数或模型输出。该目录应加入 `.gitignore`。

### Codex CLI

本模板按本机 Codex 0.144.5 的项目级 Hook schema 实现。项目没有 `.codex/hooks.json` 时，可复制 `tools/adapters/codex-hooks.json`；已有 Hook 时合并 `UserPromptSubmit` 和 `PreToolUse` 两组，不能覆盖原配置。首次加载项目 Hook 时按 Codex 自身提示审核信任。

`UserPromptSubmit` 记录 request，读取 `cs-*/SKILL.md` 的 `PreToolUse` 记录 invocation。当前 `codex exec` 对 prompt Hook 的支持取决于 Codex 版本；启用后先发一条无敏感信息的请求，并用下面的 stats 命令确认 request 已出现。未出现时不能声称 Codex 的漏触发率可观测。

### Pi

将适配器复制到项目自动发现目录：

```bash
mkdir -p .pi/extensions
cp .codestable/tools/adapters/pi-route-observer.ts .pi/extensions/codestable-route-observer.ts
```

Pi 的 `input` 记录 request，显式 `/skill:cs-*` 和模型读取对应 `SKILL.md` 分别记录 explicit / implicit invocation。人工纠正最近一次路由：

```text
/route-correct cs-expected [cs-original] [reason-code]
```

技能本不应触发时把 expected 写成 `none`，例如 `/route-correct none cs-feat unnecessary`。

### 统计与人工标注

```bash
python .codestable/tools/route-observer.py stats --root . --json

python .codestable/tools/route-observer.py record-correction --root . \
  --request-id req-xxx --expected-skill cs-issue \
  --original-skill cs-feat --reason-code existing-behavior
```

`zero_invocation_requests` 是待审信号，不等于漏触发：普通问答本来就不需要 CodeStable 技能。只有 correction 标注后才形成确认指标：应触发但未触发进入 `confirmed_missed`，不应触发却触发进入 `confirmed_false_positive`，选错技能进入 `confirmed_misroute`；三类标注共同生成 confusion matrix。
