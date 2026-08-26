<!-- codestable:managed 受管文件——由 `codestable update` 维护；项目内的修改会被更新器保留（跳过刷新），要恢复跟随技能包更新请运行 `codestable update --force`。 -->
# codestable 共享口径

由 `cs-onboard` 复制到项目的 `.codestable/reference/shared-conventions.md`。所有 codestable 子技能用项目相对路径 `.codestable/reference/shared-conventions.md` 引用本文件——跨子技能共享但不适合堆在单个技能里的规范的唯一权威版本。

skill 本身不共享文件系统（每个 skill 是独立安装单元），共享口径不能放在某个 skill 内部被别的 skill 引用。放在"工作项目"里对所有 skill 都可达。

---

## 工作流档位

档位只在 `.codestable/attention.md` frontmatter 声明：`workflow_mode: lean | standard`。未声明时按仓库现状推断：只有 attention/少量现状文档时按 `lean`；已有多阶段 feature、roadmap、refactor 或审计要求时按 `standard`。用户显式声明优先。

交付基线层是独立维度，由 `baseline_mode: off | lean | baseline | regulated` 控制（默认 `off`），与 `workflow_mode` 独立。`baseline_mode` 非 `off` 时在 `docs/baseline/` 维护对外正式设计基线，从 `.codestable/` 过程证据增量投影。完整定义见 `baseline.md`。

### lean

面向小项目和低风险日常修改，目标是保留工程约束，不制造过程文档：

- 低风险、小范围 feature / issue / refactor 直接执行并验证，默认不写每任务 spec、note 或报告
- 目录和共享工具按需创建，不预建空目录，不复制当前任务不会读取的参考文件
- 最终回复必须包含改动、验证和未解决风险；需要长期追溯时依赖 git commit，或由用户明确要求留档
- 只有命中相关文件才读取 architecture / compound；禁止启动时全量读取知识库

### standard

面向大型、多人、长期维护或强审计项目，保留完整阶段和证据，但默认聚合在一个 Change Package，不再默认每阶段一个文件。

### 自动升级

lean 任务出现任一情况，当前任务升级到 standard，不必修改项目默认档位：跨 3 个以上模块、方案存在未决权衡、超过 4 个实现步骤、涉及安全/数据迁移/公开 API、无法用现有测试验证、用户明确要求完整留档。升级前说明原因并取得用户确认。

**优先级**：安全与正确性守护规则 > 用户明确要求 > 当前任务升级结果 > 项目默认档位。档位只控制过程重量，不降低测试、证据和范围控制要求。

### 交互预算

lean 的目标是“一次对齐 → 一次执行 → 一次验证汇报”：不逐步汇报、不要求用户再次触发下一技能、不为同一结论重复搬运上下文。standard 只在阶段边界、未决方案、人工验证或风险升级处停顿；同一阶段内应批量完成可独立验证的动作。

Change Package 的目录、状态机和旧格式兼容规则见 `change-package.md`。feature / issue / refactor 和独立 audit 的新任务使用该格式；对单一既有包的后续复查追加到原包，不另建目录。requirements / architecture / compound 仍保持独立文档。

### 模型切换 ROI

简单任务不为模型分工增加交接：≤2 个明确 step、低风险、少量文件时由当前模型直接完成。复杂但小的任务也保留高阶模型全程处理。只有高阶模型已写清接口、约束、测试，且至少三步低风险实现可组成连续 wave 时，才建议用户切换低阶模型。切换与回切均由用户确认，不能由技能假定已经发生。

---

## 0. 目录结构与路径命名

onboard 后只保留这些职责：`attention.md` 放常驻规则；`requirements/` 放能力愿景；`architecture/` 放当前系统地图；`roadmap/` 放跨 feature 规划；`changes/` 聚合 standard 变更；`compound/` 放可检索知识；`tools/` 和 `reference/` 放共享支持文件。讨论层的 spike 记录用 `brainstorms/{slug}/brainstorm.md`，只在 spike 时创建（详见 `cs-brainstorm`）。

`.codestable/.runtime/` 是运行时工作区，不进版本库（onboard 时写入 `.gitignore`）。目前只有一个文件：`current-package` 记录活动 Change Package 的相对路径（如 `changes/2026-02-11-login`，单行）。它是**续作加速指针，不是事实源**：创建 Change Package 的阶段写入（feat-design / issue-report / refactor design），档案关闭或归档时清除；读取前必须核对目录存在且 phase 与预期一致，对不上就忽略指针、回退到合规检查器的 `--index --agent` 模式或目录扫描。手工删除不影响任何流程正确性。

交付基线层在项目根 `docs/baseline/`（不在 `.codestable/` 内），由 `codestable baseline` 命令族维护，内容从 `.codestable/` 过程证据增量投影。只有 `attention.md` 的 `baseline_mode` 非 `off` 时存在。`docs/dev/` 和 `docs/user/` 仍由 `cs-guide` 维护，不受 baseline 层影响。

旧 `features/`、`issues/`、`refactors/`、`audits/` 只读兼容，不在新项目创建，也不在新任务写入。

### 命名规则

- 需求文档：`requirements/{slug}.md`（能力愿景，不带日期前缀，扁平不分组）；中心索引 `requirements/VISION.md`
- roadmap：`roadmap/{slug}/`（不带日期前缀，平铺不嵌套）
- standard 变更目录：`changes/YYYY-MM-DD-{slug}/`
- 沉淀类：`compound/YYYY-MM-DD-{doc_type}-{slug}.md`，日期用**归档当天**
- 架构 doc：`architecture/{type}-{slug}.md`（长效，不带日期前缀）；总入口固定 `ARCHITECTURE.md`
- 项目注意事项入口固定为 `.codestable/attention.md`，所有 codestable 子技能启动前必须读取；不再兼容 `AGENTS.md` / `CLAUDE.md` 等外部入口
- 讨论层目录：`.codestable/brainstorms/`（复数）；旧版单数目录由 `codestable update` 自动改名
- 技能包资源目录约定：单参考文件用 `reference.md`，多文件用 `reference/` 目录；不用 `references/` 复数。历史偏离由更新器与维护者向此约定收敛

### 架构 doc 分组规则（同类聚合）

`architecture/` 下用文件名第一段作 type 标记：`ui-chat.md` 和 `ui-events.md` 同 `ui` 类。**所有架构 doc 必须 `{type}-{slug}.md`**——只有一份的也要带合理 type 段（如 `cli-entry.md`），否则未来同类出现时聚合不了。

**触发**：某 type 在 `architecture/` 根目录达到 ≥6 份时（即新加第 6 份那次），把这一类全部收进同名子目录。

**收入后**：去掉 type 前缀。`ui-chat.md` → `ui/chat.md`。

**只升不降**：删到 ≤5 份也不折回平铺。

**触发时谁负责**：`cs-arch` 的 `backfill` / `update` 在落盘前检查并提出搬迁清单，用户 review 后再搬，同时更新 `ARCHITECTURE.md`。`check` 只报告，不搬迁。

### 改目录结构

改 `cs-onboard/reference/shared-conventions.md` 模板，新项目 onboard 时带上新版本；已有项目手动同步 `.codestable/reference/shared-conventions.md`。

---

## 1. 共享元数据口径

**Change Package**：feature / issue / refactor / audit 的新任务统一使用单文档变更包——`doc_type: change`、`kind: feature | issue | refactor | audit`，状态机与落盘位置见 `change-package.md`。各阶段技能只补特有字段和阶段状态变化，不再按 brainstorm / design / acceptance 各建一份 spec。

**旧版 issue spec**：report / analysis / fix-note 共用 `doc_type` / `issue` / `status` / `tags`。新任务统一使用 Change Package 的 `kind: issue`。

**归档类（compound）**：

- learning / trick / decision / explore 四类**统一写入 `.codestable/compound/`**
- 每个文档 frontmatter 顶部带 `doc_type`（learning / trick / decision / explore）作跨子技能归属判定
- 文件名 `YYYY-MM-DD-{doc_type}-{slug}.md`——日期打头便于 `ls` 排序，type 段在中间便于 grep
- 各子技能在 `doc_type` 之外保留专属 frontmatter（learning 的 `track` / trick 的 `type` / decision 的 `category` / explore 的 `type`）
- 各子技能只认自己的 `doc_type` 不读写别家
- `status` 等通用字段语义和本文件保持一致

**外部读者文档**（guidedoc / libdoc）：frontmatter 由各自子技能定义。无特殊说明：`draft` = 待 review，`current` = 当前有效，`outdated` = 代码已变更待同步。

**写作约束**：子技能提字段时优先写"额外字段"或"阶段状态变化"，不重复展开整套通用字段。

---

## 2. 执行计划生命周期

新任务把 `steps` 和 `checks` 写入 Change Package；只有需要机器状态时才增加 `checks.yaml`。design 定义范围、退出信号和验收项；implement 只推进 step 状态；acceptance 只更新 check 结果。存在 `preexisting_changes` 时必须记录 `baseline` 快照，不能按文件名永久忽略后续修改。任何阶段发现范围或架构影响变化，都先回 design 修改 contract，不静默扩大。

旧 `{slug}-checklist.yaml` 只读兼容。lean 直通不生成 checklist 或任务 note；用户要求留档时升级 standard。

---

## 2.5 roadmap ↔ feature 衔接协议

`.codestable/roadmap/{slug}/{slug}-items.yaml` 是规划层和 feature 执行层的唯一接口。三个技能共同读写它——是 skill 都读写项目共享产物，不算耦合。

**items.yaml 状态机**：

```
planned  → in-progress  （cs-feat-design 启动 feature 时改）
in-progress → done      （cs-feat-accept 验收完成时改）
planned  → dropped      （cs-roadmap update 模式，用户决定不做时改）
```

`done` / `dropped` 是终态。`done` 只有在对应 feature 的验收证据、checklist 状态和 feature 链接同时成立时才允许。需要回退重做的新加一条 slug 略改的条目，不改终态。

**cs-roadmap 的职责**：生成和维护 roadmap 主文档 + items.yaml；把 `planned` 改 `dropped`（用户放弃时）；不改 `in-progress` / `done`（feature 技能负责）。

**cs-feat-design 的职责**（从 roadmap 起头时）：

1. 新 Change Package 的 frontmatter 加 `roadmap: {roadmap-slug}` + `roadmap_item: {子 feature slug}`；旧版 design.md 同样支持
2. items.yaml 对应条目 `status: in-progress` + `feature: YYYY-MM-DD-{slug}`
3. 校验 yaml

直接起 feature（非 roadmap 来）两字段留空，不触发 roadmap 写。

**cs-feat-accept 的职责**：

1. 读 design frontmatter `roadmap` / `roadmap_item`
2. 空 → 跳过
3. 有值 → items.yaml 对应条目 `status: done`；同步主文档子 feature 清单显示状态；校验 yaml

回写是**实际写文件的动作**，验收报告要明确记录回写结果。

**最小闭环标记**：items.yaml 每份只有一条 `minimal_loop: true`，标记"做完后系统能端到端跑通最窄路径"。design 启动 `minimal_loop` 条目时优先级最高。

---

## 3. 阶段收尾推荐

**feature-acceptance** 收尾按顺序判断：

1. `cs-knowledge`：按 learning / trick / decision 归档值得复用的结论
2. `cs-guide`：开发者 / 用户指南
3. `cs-libdoc`：公开 API 参考
4. `scoped-commit`

**issue-fix** 收尾按顺序判断：

1. `cs-knowledge`：坑点或暴露的长期约束
2. `scoped-commit`

**lean feature** 收尾按顺序判断（没有 architecture / req 回写动作）：

1. `cs-knowledge`：动手过程值得复用的结论
2. `scoped-commit`

**统一规则**：一律一句话提示；用户说"不用"立即跳过；不强制；上游主动提示，下游承接执行。

---

## 4. 收尾提交（scoped-commit）

acceptance / issue-fix 走完后把本次产物提交为一个 commit：

- **范围**：本次工作改到的代码 + 相关 spec 文档 + 本次实际更新过的架构 doc + 本次实际更新过的 roadmap items.yaml / 主文档
- **不该进**：和本次工作无关的顺手修改；属于"下次另起 feature / issue"的扩大范围
- **提交前确认**：用户没明确同意不要 `git commit`
- **commit message**：一句话说清"做了什么"，不贴 spec 目录路径

子技能只描述本阶段特有提交范围，通用规则看这里。

阶段技能的 frontmatter description 必须写明所需 Change Package 状态和排除条件；根入口据此接续，避免 design / impl / accept 抢占彼此。

大型项目可按需启用 `.codestable/constitution.yaml`、Change Package `artifacts`、`evidence.jsonl` 和 `changes/index.yaml`；lean 项目不预建空文件。它们是可选加速/门禁产物，不替代 `change.md`。

模型上下文差异通过 `.codestable/model-profile.yaml` 管理，不分叉技能代码。128K profile 仍优先 `--agent`；64K profile 强制按 `context_refs` 的 heading/symbol 读取，超过预算就缩小当前 step，不删除 contract 或验证门禁。

---

## 5. 归档检索规则

feature-design / issue-analyze / issue-fix 动手前到 `.codestable/compound/` 搜已有沉淀：

- 总是先搜 `architecture/` 和 `compound/`
- 在 `compound/` 用 `doc_type` 过滤（learning / trick / decision / explore）
- 搜到的结果只作参考输入，不盲目套用——可能已 `outdated` 或不适合当前上下文
- 搜到和当前方向冲突的 decision → **必须**正面回应"为什么仍然这么做"或调整方向

子技能只补本阶段查询命令。完整搜索语法看 `.codestable/reference/tools.md`。

---

## 6. 归档类子技能共享守护规则

`cs-knowledge` / `cs-explore` 共享下面这组规则：

1. **只增不删**——已归档除非被明确取代（`status=superseded`）否则不删；理由丢失成本极高
2. **宁缺毋滥**——用户说不出理由的节直接省略，不要 AI 编造
3. **不替用户写实质内容**——AI 负责起草结构和串联语言，实质结论必须来自用户或可追溯的代码证据
4. **attention.md 检查**——写完后若沉淀暴露出"每次启动都该知道"的一两行硬约束，提示用户用 `cs-note` 追加到 `.codestable/attention.md`；不要直接改外部 AI 入口
5. **起草前先查重叠**——动手写前用 `python .codestable/tools/search-yaml.py --query` 查语义相近的旧文档。命中就把候选列给用户在三条路径里选：
   - **更新已有**（默认优先）：沿用原文件名和原创建日期，**不新建**；frontmatter 补 `updated: YYYY-MM-DD`；超出小修在文末加"YYYY-MM-DD 更新"简述
   - **supersede**：旧文档保留原文，`status: superseded` + `superseded-by: {新文件名}`，正文顶部加 `**[已取代]** 见 {新 slug}`；新文档 frontmatter 带 `supersedes: {旧文件名}`
   - **确实是不同主题**：新建，文末"相关文档"列出已有那条说明区别
6. **识别用户意图是"改已有"还是"记新的"**——用户说"改 / 更新 / 修订 / 补充 {某条}"、明确指向某条旧文档、或话题高度重合时默认走"更新已有"，不要闷头新建。分不清就问。

`cs-knowledge` 可读写 learning / trick / decision；`cs-explore` 只写 explore。

---

## 7. 写代码时的反射检查

`cs-feat-impl` 和 `cs-issue-fix` 共用。AI 默认会往"大函数 / 大文件 / god class / 处处特殊分支"漂，这一节把漂移截在发生那一刻。

**不是阈值，是触发器**——硬数字会诱发为拆而拆把自然聚合的代码切碎。每条都是"遇到 X 情况就停下来问自己"。

| 触发场景 | 停下来问自己 |
|---|---|
| 要往一个已经很长的文件追加代码时 | 文件承担几件事？新加的是已有职责延伸还是第 N+1 件事？是第 N+1 就默认新建文件 |
| 要给已经很多方法的类加方法时 | 新方法是核心职责的自然扩展，还是把类推向"什么都能干"？ |
| 写的函数已超过一屏时 | 函数在做几件事？几件事就拆 |
| 要加 `if (特殊情况) { 特殊处理 }` 分支时 | 抽象维度选错了？正确做法可能是把特殊路径和通用路径分成不同函数 / 策略 / 类 |
| 要 copy-paste 一段代码时 | 能抽成共用还是只字面相似？能抽就抽 |
| 要给函数加第 4+ 个参数时 | 函数做的事是不是太多了？参数列表是 API 恶化的早期信号 |
| 要新写"万能工具类 / helper"时 | 真没归属还是只是想不起来放哪儿就先堆 util？ |

**停下来之后**：反射检查只把问题提出来，结论用户定。停下来想清楚的动作（拆 / 新建 / 重命名 / 抽共用）会让改动超出现有 steps 范围 → 跟用户对齐再决定（纳入当前推进 / 记顺手发现留后续）。

不许偷偷拆完继续写，也不许忽略信号硬冲。默认动作是停、问、再继续。