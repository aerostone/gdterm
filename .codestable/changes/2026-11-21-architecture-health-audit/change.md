---
doc_type: change
kind: audit
slug: 2026-11-21-architecture-health-audit
mode: standard
scope: 全仓模块依赖概览与 UI 组合根、标签会话边界抽样
created: 2026-11-21
status: closed
total_findings: 1
---

# 架构健康度审计报告

## 范围与总评

本次按 Architecture Health / Standard 检查 12 个生产项目的项目引用图，并深查 UI 组合根、MainForm 的实际职责、标签页会话生命周期与 Services 分工；其它业务模块按项目边界及近期变更作表面扫描。依赖方向总体清楚、无项目级循环；在审查范围内发现一项 P2 架构偏离。

覆盖范围内的结构属于“局部结构债务”，不支持把整个项目称为“屎山”。Core 作为底层模型/序列化边界，Connections、Security、KeePass、Logging 等持久化和领域模块依赖 Core；Terminal、SFTP、RDP 与 Tools 使用 Tunnel/Core；AI 使用 Terminal/Core；UI 在 Program 组合根中引用各业务库。`Gdterm.Tools` 面板接口暴露 WinForms `Control` 是架构文档已记录的有意取舍，本次不重复报告。

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 位置 |
|---|---|---|---|---|---|
| 1 | arch-drift | P2 | high | MainForm 仍承担多种独立应用行为，超出组合根与布局边界 | `src/Gdterm.UI/Forms/MainForm.cs:820` |

## 发现详情

### Finding 01：MainForm 仍承担多种独立应用行为，超出组合根与布局边界

- 位置：`src/Gdterm.UI/Forms/MainForm.cs:820`；架构约束见 `.codestable/architecture/ARCHITECTURE.md:155`
- 触发条件：修改会话录制、终端缓冲导出、欢迎页、托盘/退出行为、窗口状态或命令面板时，直接修改 MainForm 的事件处理和状态协调。
- 证据链：架构索引明确要求新业务逻辑优先进 `Services`，并称 MainForm 仅负责组合根和布局（`ARCHITECTURE.md:155`）。当前 MainForm 仍含会话录制启停和录制状态提示（`MainForm.cs:820-856`）、录制中心弹窗和终端缓冲导出（`MainForm.cs:859-907`）、欢迎页生命周期（`MainForm.cs:911` 起）、托盘/退出处理（`MainForm.cs:972`、`:1118`）、窗口几何读写（`:1047` 起）以及跨功能命令清单构造（`:1199` 起）。该类有 49 个字段、31 个成员；会话录制近期变更实际同时修改 MainForm 与 Terminal/UI 其它模块（提交 `016e211`），证明新增独立行为仍扩展此中心类。
- 影响：修改录制、窗口生命周期或欢迎页等功能时，开发者需同时理解主窗体的多组服务依赖和共享状态；跨功能协调与回归验证继续集中在 MainForm，扩大主壳的变更影响面。架构索引中“仅组合根+布局”的描述也会让后续开发者误判这些行为已有独立服务承接。
- 置信度：high（代码职责、架构约束和近期变更记录均可直接核实；未运行动态验证，不据此推断运行时故障）。
- 建议：走 `cs-refactor`，把录制/缓冲导出、欢迎页状态和托盘/窗口生命周期中适合隔离的协调职责移到现有 Services 模式，并同步维护架构索引；保留 MainForm 作为组合与事件路由入口。

## 已核查并排除

- 项目依赖循环：项目引用边均向 Core/Tunnel 基础层汇聚，UI 位于消费端；未发现反向 ProjectReference 或循环。
- 前次审计的手写 JSON 契约、Connections → Security 越层依赖、重复 SSH 连接工厂、构造参数与 setter 双轨：后续 `f03d2f0` 已统一 JSON 契约、移除越层引用与重复实现、引入 `UiDependencies` / `TabDependencies`，当前项目引用和注入代码与修复记录一致。
- `TabContainerControl` 行数较多本身不作为发现：当前打开/关闭/重连大部分委托给 `ProtocolTabOpener`、`TabCloseService`、`TabReconnectService`、`TabSelectionCoordinator`；其余保留标签集合、事件转发和视图装饰等壳职责。没有证据证明仅凭其规模已造成额外坏结果。
- `IToolModule.CreatePanel()` 返回 WinForms 控件：该耦合已在架构决策 D3 和历史审计中明确记录为取舍，本次未观察到新的分叉或错误影响。

## 下一步

1. P2：通过 `cs-refactor` 收窄 MainForm 的应用行为职责；若当前实际职责有意保留，则用 `cs-arch` 更新边界说明和代码锚点。

## Verification Evidence

- 范围：12 个生产 `.csproj` 项目引用、UI 组合根/主窗体/标签会话路径；架构文档中的 `Services` 分工和硬边界。全仓其它领域服务未逐文件深读，第三方代码与生成物未纳入。
- 维度：依赖方向/循环、职责内聚、接口边界、变更扩散、测试隔离线索。部署与运行故障传播未核查。
- 高风险区域：终端标签生命周期、会话录制与输入相关状态、主窗体退出/窗口恢复、工具与协议库依赖边界。
- agent：0。
- 候选：1 个候选 / 0 个验证淘汰 / 1 个去重后 finding。
- 主 agent 回读：`.codestable/attention.md`、`.codestable/architecture/ARCHITECTURE.md`、`.codestable/changes/2026-11-11-architecture-health-audit/change.md`、`src/Gdterm.UI/Forms/MainForm.cs`、`src/Gdterm.UI/Controls/TabContainerControl.cs`、`src/Gdterm.UI/Services/UiDependencies.cs`、`src/Gdterm.UI/Services/SidePanelFactory.cs`、`src/Gdterm.Tools/IToolModule.cs`、各生产项目 `.csproj` 的 `ProjectReference`。
- 架构对照：仅读取 `.codestable/architecture/ARCHITECTURE.md`；该文档状态为 current，但 MainForm/TabContainer 行为边界需要按当前代码更新。
- 历史证据：复核了 2026-11-11 架构审计的后续修复记录、`f03d2f0` 结构整改提交，以及 `016e211` 会话录制等独立功能变更。
- 动态验证限制：架构结论基于代码和 Git 历史；本机无 .NET/MSBuild/Mono，未构建、运行 UI 或测量实际变更工时。变更扩散的成本结论限于代码路径与提交记录，不等同于定量成本测量。
