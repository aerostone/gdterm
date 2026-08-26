<!-- codestable:managed 受管文件——由 `codestable update` 维护；项目内的修改会被更新器保留（跳过刷新），要恢复跟随技能包更新请运行 `codestable update --force`。 -->
# codestable 体系总览

本文档介绍 codestable 工作流家族整体——有哪些子技能、各管什么场景、产物怎么组织。无论是 AI 在运行时读到这个文件，还是人打开来看，都能对整个体系有个完整印象。

AI 辅助开发里，有几类场景会反复出现——加新功能、修 bug、遇到值得沉淀的经验、做技术选型、摸新模块的代码、接入新仓库。每种场景如果每次从零处理，都会出各自的典型问题：AI 给功能起的术语跟老代码冲突、bug 改完没人记得当时怎么诊断的、上周刚踩过的坑下周又踩一遍。

codestable 把这几类场景各配一套子技能，产物放进统一的目录结构、带统一的 YAML frontmatter,互相之间可以检索引用。

## 模型能力基线

codestable 面向具备可靠工具调用、结构化输出和代码推理能力的 27B+ 现代模型，默认 profile 是 128K；64K 使用 constrained profile，只缩减上下文文件数、字符预算和 reference 读取，不改变工程门禁。9B 不作为兼容目标，也不维护独立低上下文分支。

入口和阶段技能都保持可见、可显式或自然语言调用；触发边界由各技能 frontmatter description 声明。阶段技能的状态条件写在 description 中，避免它们抢占新任务路由。


## 技能分成四部分

**根入口**——开放式诉求 / 不知道走哪个时的统一入口:

- `cs` — 介绍体系、路由诉求；明确 lean 小任务会在同一轮接续执行技能

**做事**——从一段模糊想法走到上线的功能、或者从一份错误报告走到修好的 bug:

- `cs-feat` — 新功能,design → implement → acceptance（想法还模糊时先走讨论层 `cs-brainstorm` 做分诊，不属于 feature 流程内部）
- `cs-issue` — 修 bug,report → analyze → fix
- `cs-refactor` — 代码优化(行为不变、结构/性能/可读性变),scan → design → apply
- `cs-audit` — 主动审计指定范围，发现 bug / 安全 / 性能 / 维护性 / 架构偏离，输出带代码证据的 finding 清单；只发现不修复，修复转 issue / refactor

standard 先产出 Change Package 并经 review 再实现；lean 的低风险小任务直接实现和验证，不制造过程文档。两种档位都必须遵守项目规则、范围和测试证据。

standard 可按风险启用 artifact graph、`evidence.jsonl`、constitution 和 changes index：高风险任务强制 standard；大型项目通过索引和结构化证据获得追溯，小项目不预建这些文件。

**沉淀**——把做事过程产生的知识存下来,下次遇到同类问题直接复用:

- `cs-knowledge` — 统一归档 learning / trick / decision，按 frontmatter 区分性质
- `cs-explore` — 存档"调查了 X 问题,看到代码里是这样的"
- `cs-note` — 把一两行启动必读的项目注意事项追加到 `.codestable/attention.md`

**讨论层**——想法还模糊时的统一入口,不直接产出设计或代码:

- `cs-brainstorm` — 和用户对话做分诊:case 1(已经够清楚,直接进 feature 起草 design)、case 2(小需求,在 feature 的 Change Package 里继续讨论)、case 3(大需求,移交给 roadmap)。旧项目才单独落 `{slug}-brainstorm.md`

**辅助**——围着前几类转的周边工具:

- `cs-onboard` — 把新仓库接入 codestable 目录结构
- `cs-req` — 起草或刷新 `.codestable/requirements/` 下的需求文档——系统的能力愿景层，覆盖过去/现在/未来
- `cs-arch` — 架构相关一站式:起草新架构文档 / 刷新已有文档 / 做架构体检(含 design 自洽 / design↔代码一致 / architecture 目录多份文档间一致)。architecture 只记现状
- `cs-roadmap` — 把一块装不进单个 feature 的大需求拆成带依赖和状态的子 feature 清单,作为后续多次 feature 流程的种子和排期依据;独立于需求 / 架构档案
- `cs-guide` — 写给外部读者的开发者指南 / 用户指南
- `cs-libdoc` — 为库的公开 API 逐条目生成参考文档
- 阶段子技能（`cs-feat-design` / `cs-feat-impl` / `cs-feat-accept` / `cs-issue-report` / `cs-issue-analyze` / `cs-issue-fix`）不单独列出——它们由各主流程技能按阶段接续，见对应主流程条目


## 场景路由

仓库里还没有 `.codestable/` 目录,先用 `cs-onboard` 搭骨架。

| 场景 | 子技能 |
|---|---|
| 想法还模糊 / "有个想法没想清楚" / "先聊聊" | `cs-brainstorm`(分诊后路由到 feature design / Change Package 讨论 / roadmap) |
| 新功能 / 新能力 | `cs-feat` |
| BUG / 异常 / 文档错误 | `cs-issue` |
| 代码优化 / 重构 / 重写(行为不变) | `cs-refactor` |
| 审计 / 全面检查 / 找隐患(只发现不修复) | `cs-audit` |
| 摸代码、提问调研 | `cs-explore` |
| 补 / 更新需求文档 | `cs-req` |
| 补 / 更新 / 检查架构文档 | `cs-arch` |
| 大需求拆解 / 排期规划 | `cs-roadmap` |
| 经验、技巧、技术决定与规约 | `cs-knowledge` |
| 一两行稳定项目硬约束(构建前置、测试命令、路径禁区) | `cs-note` |
| 开发者指南 / 用户指南 | `cs-guide` |
| 库 API 参考 | `cs-libdoc` |
| 把 `.codestable/` 过程证据投影成交付基线草稿(显式触发) | `cs-baseline` |

完整的操作手册、退出条件、和其他工作流的关系,各子技能里讲。


## 沉淀文档如何区分

learning / trick / decision / explore 都是存档文档类型,区别在记录内容的性质:

- 回顾某次做 X 时发现了 Y —— `cs-knowledge` + `doc_type: learning`
- 以后做 X 就这样做的处方 —— `cs-knowledge` + `doc_type: trick`
- 全项目今后都得遵守的规定 —— `cs-knowledge` + `doc_type: decision`
- 调查了一个问题,留份证据 —— `cs-explore`(产出 `doc_type: explore`)

四种文档共用 `.codestable/compound/`，靠 `doc_type` 和文件名区分。`cs-knowledge` 负责前三种，`cs-explore` 负责调查证据。


## 愿景档案 vs 结构档案 vs 规划档案 vs 单次动作

四类文档各管一段时间尺度,不要混:

- **愿景档案**(requirements)——描述"用户需要什么、系统提供什么能力来满足"。`status` 区分三个时间深度：`draft`（未来愿景）、`current`（现在的能力）、`outdated`（过去的痕迹）。draft req 可独立于实现存在——先把愿景定下来，后续 roadmap 排期和 design 实现才有稳定对齐基准
- **结构档案**(architecture)——描述"系统现在用什么结构实现"。只记现状,默认在 feature-acceptance 时跟着代码同步;必要时由 cs-arch 主动刷新。**不写"未来会加什么层"**
- **规划档案**(roadmap)——描述"接下来打算怎么分步实现"。独立于愿景和结构档案,改动不牵连 requirements / architecture。所有条目 done / dropped 后 roadmap 进入 `completed` 状态,作为历史档案留存
- **单次动作**(feature / issue / refactor)——本次要做的一件具体事情的 spec。动作走完后,相关沉淀提炼进愿景档案、结构档案和沉淀类文档

用户说"我想要一个 X 系统"这种大需求,先走 roadmap 拆成若干子 feature,再一条一条走 feature 流程。直接起 feature 会变成巨型 design 塞不下、拆了又没有追踪抓手。


## 阶段与快速通道

standard feature 走 design → implement → acceptance，standard issue 走 report → analyze → fix。每个阶段有退出条件，上一个没满足，下一个不开始。（想法还模糊时先走讨论层 `cs-brainstorm` 分诊，再进对应流程。）

AI 最常见的问题是一口气铺几百行代码才让人看——等发现问题已经很难中止。阶段间的人工 checkpoint 就是为了早一步中止。每个 checkpoint 具体检查什么,对应子技能里讲。

lean 例外：根因明确的小 issue 直接 fix；小 feature 由 `cs-feat` 内置直通完成，默认不写 spec。命中安全、迁移、公开 API、跨模块或无法验证时必须升级 standard。


## 进一步参考

- `.codestable/reference/change-package.md` — 新版 feature / issue / refactor / audit 单文档变更包规范
- `.codestable/reference/shared-conventions.md` — 目录结构、元数据、执行计划、收尾和归档共享规则
- `.codestable/reference/tools.md` — 校验、检索与可选开放运行时路由观测用法
- `.codestable/reference/maintainer-notes.md` — 断点恢复、新增子工作流的登记

目录结构（requirements / architecture / roadmap / changes / compound，以及旧版 features / issues / refactors）的权威定义在 `shared-conventions.md`。要改目录先改模板，新项目 onboard 时会带上新版本。


## 相关

- `.codestable/attention.md` — codestable 技能启动必读的项目注意事项
- `.codestable/architecture/ARCHITECTURE.md` — 项目架构总入口