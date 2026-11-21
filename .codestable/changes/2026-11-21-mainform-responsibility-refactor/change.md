---
doc_type: change
kind: refactor
slug: 2026-11-21-mainform-responsibility-refactor
status: in-progress
mode: standard
risk:
  level: medium
  reasons: [cross_module]
contract:
  include:
    - .codestable/changes/2026-11-21-mainform-responsibility-refactor/**
    - .codestable/architecture/ARCHITECTURE.md
    - src/Gdterm.UI/Forms/MainForm.cs
    - src/Gdterm.UI/Services/SessionRecordingCoordinator.cs
    - src/Gdterm.UI/Services/WelcomePanelCoordinator.cs
    - src/Gdterm.UI/Services/MainWindowLifecycleCoordinator.cs
    - src/Gdterm.UI/Gdterm.UI.csproj
    - src/Gdterm.Tests/Ui/MainFormSmoke.cs
    - src/Gdterm.Tests/Gdterm.Tests.csproj
  exclude:
    - .codestable/changes/2026-11-21-architecture-health-audit/**
    - .codestable/changes/2026-11-21-functional-audit/**
  preexisting_changes: [.codestable/changes/2026-11-21-architecture-health-audit/change.md, .codestable/changes/2026-11-21-functional-audit/change.md]
  baseline:
    git_head: 62a9edfdde699746b126c9383a94e1fc525c828a
    dirty_hashes:
      .codestable/changes/2026-11-21-architecture-health-audit/change.md: ee36be5b2957d5dcdd478e5748a1c5ba97aab81592fa2162ff9d8a4027be31b9
      .codestable/changes/2026-11-21-functional-audit/change.md: 9a69d60c77eb223f50f2a19864cbddd45fafef7ef3a2180b4a813afa5ed5b276
  architecture_impact: update
  architecture_refs: [ARCHITECTURE.md]
  requirement_impact: not-applicable
  requirement_reason: 行为保持不变，不改变用户能力或需求边界。
  context_refs:
    design: [.codestable/architecture/ARCHITECTURE.md]
    impl: [src/Gdterm.UI/Forms/MainForm.cs, src/Gdterm.Tests/Ui/MainFormSmoke.cs]
    accept: [.codestable/architecture/ARCHITECTURE.md]
---

# MainForm 职责收敛

## 1. 目标与边界

将 MainForm 中独立的会话录制/缓冲导出、欢迎页状态、主窗口托盘与窗口几何行为移动到 Gdterm.UI/Services。MainForm 保留组合、布局与入口路由。对用户可观察的菜单、快捷操作、确认顺序、状态提示、窗口恢复和会话行为保持等价。

## 2. 行为增量

无（行为保持不变）。

## 3. 设计与契约

- `SessionRecordingCoordinator` 管理录制启停、录制中心打开和缓冲导出；活动终端、宿主窗体、底栏和录制目录由组合根提供。
- `WelcomePanelCoordinator` 管理欢迎页构造、动作路由及按标签数量更新可见状态。
- `MainWindowLifecycleCoordinator` 管理托盘图标、最小化/恢复、退出确认和窗口/侧栏几何保存恢复；窗体关闭事件顺序保持不变。
- 所有新增 C# 文件同时加入经典 `.csproj` Compile 清单。

## 4. 执行计划

- [x] 为主窗体欢迎页空态/开本地终端切换、录制启停添加冒烟断言。
- [x] 抽取三个 coordinator，MainForm 仅组合和路由，并更新 UI 项目 Compile 清单。
- [x] 更新架构索引的 MainForm/Services 当前职责描述。
- [x] 运行可用的测试与 UI/项目静态门；Windows UI 冒烟因当前环境缺少 .NET 工具链未执行。

## 5. 执行证据

1. `python3 tools/check-csproj-files.py`：通过，13 个工程 0 处问题。
2. `python3 tools/check-i18n.py`：通过，新增调用复用了已有词典文案。
3. `python3 .codestable/tools/check-compliance.py --root . --change .codestable/changes/2026-11-21-mainform-responsibility-refactor --phase impl --check --context --agent --emit context,failures,waves`：通过，无 failures。
4. `python3 .codestable/tools/check-compliance.py --root . --architecture .codestable/architecture --json`：通过，9 项检查、0 failures、0 warnings，架构代码锚点有效。
5. `git diff --check`：通过。
6. `MainFormSmoke` 已增加欢迎页显隐与临时目录录制启停断言；未能运行。当前环境没有 `dotnet`、`msbuild`、`mono`、`csc` 或 `mcs`，WinForms 冒烟需在 Windows 构建环境执行。

## 6. 验收结果

已按行为等价目标拆出三个 UI Services coordinator，并保留 MainForm 作为组合根、布局和入口路由。关闭确认、清理、几何保存的事件顺序保持不变；架构索引已同步。静态门全部通过。剩余验证仅为 Windows 上编译并运行 `Gdterm.Tests.exe` 的 UI 冒烟。

## 7. 遗留事项

Windows 构建环境仍需执行 `msbuild gdterm.sln /p:Configuration=Release` 并运行 `Gdterm.Tests.exe`，确认 WinForms 冒烟断言通过。
