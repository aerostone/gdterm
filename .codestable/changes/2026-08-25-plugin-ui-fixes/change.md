---
doc_type: change
kind: issue
slug: 2026-08-25-plugin-ui-fixes
status: accepted
phase: fixed
mode: standard
risk:
  level: medium
  reasons: [security, cross_module, no_tests]
roadmap: 
roadmap_item: 
contract:
  include:
    - src/Gdterm.Tools/Scanning/**
    - src/Gdterm.Tools/Modules/**
    - src/Gdterm.Tools/ISshRemoteSession.cs
    - src/Gdterm.Tools/SshNetRemoteSession.cs
    - src/Gdterm.Tools/ToolConfigBase.cs
    - src/Gdterm.Tools/ToolPanelHelper.cs
    - src/Gdterm.UI/Forms/ScannerCenterForm.cs
    - src/Gdterm.UI/Forms/ChangeMasterPasswordForm.cs
    - src/Gdterm.UI/Forms/MainForm.cs
    - src/Gdterm.UI/Services/ToolsDialogsLauncher.cs
    - src/Gdterm.UI/Services/AppShutdownCoordinator.cs
    - src/Gdterm.UI/Services/MainFormMenuBuilder.cs
    - src/Gdterm.UI/Services/MenuIconFactory.cs
    - src/Gdterm.UI/Controls/ToolboxPanel.cs
    - .codestable/changes/2026-08-25-plugin-ui-fixes/**
  exclude:
    - src/Gdterm.Tests/**
    - src/Gdterm.Core/**
    - src/Gdterm.Connections/**
  preexisting_changes: []
  baseline:
    git_head: c59df6719b6156dbb5cf9fd59b625ad49ba6ccf4
    dirty_hashes: {}
  architecture_impact: update
  architecture_reason: 台账路径记载修正与 D3 已知债补充（attention.md / ARCHITECTURE.md，均为文档对齐，非架构变更）
  architecture_refs:
    - .codestable/architecture/ARCHITECTURE.md
    - .codestable/attention.md
  requirement_impact: not-applicable
  requirement_reason: 纯缺陷修复与加固，无行为需求变化
  context_refs:
    design:
      - .codestable/changes/2026-08-25-plugin-ui-audit/change.md
    impl:
      - .codestable/reference/change-package.md
    accept: []
artifacts:
  - id: audit-source
    path: .codestable/changes/2026-08-25-plugin-ui-audit/change.md
    depends_on: []
evidence_ledger: false
---

# plugin-ui-fixes：审计 16 条 finding 修复

## 1. 目标与边界

修复 `.codestable/changes/2026-08-25-plugin-ui-audit/change.md`（closed）全部 16 条 finding。边界：不引入新依赖、不改签名信任链协议、不动 Gdterm.Tests、库侧（Gdterm.Tools）不引入对 UI/DiagLog 的依赖。

## 2. 行为增量

### MODIFIED

- 内置插件 .sig 补写真正生效（原 `byte[] ==` 恒 false）。
- SSH 扫描命令带超时：远端挂死时按 manifest.TimeoutSeconds 中断并返回超时错误，UI 不再永久卡"执行中"。
- 插件脚本在验签后、执行前重算 SHA256，不一致则拒绝执行（收窄 TOCTOU 窗口）。
- 扫描中心运行异常后按钮恢复可点；窗体关闭后不再向已释放句柄渲染。
- 工具箱切换工具释放旧面板；共享 ScanPluginStore 由组合根持有并在退出时 Dispose。
- 五个工具模块的耗时操作移入后台线程，UI 不再冻结；状态栏经 BeginInvoke 封送。
- Keepass 改主密码改 async handler，失败弹窗提示而非冻结 UI。
- WMI 通道超时 best-effort 终止远端进程；读回文件失败不再覆盖已有错误提示。
- ExtractBool 跳过冒号后空白且从键位置起搜索，多布尔键配置不再误读。
- 内置插件版本刷新时同时备份 manifest。
- 扫描中心菜单图标改为 radar（与敏感信息扫描区分）；删除重复的 AppendLine 扩展类与孤儿 dummy TextBox。

## 3. 方案与修复映射

| # | 修法 | 文件 |
|---|---|---|
| F01 | `FileMatchesContent` 改 `SequenceEqual` | ScanPluginStore.cs |
| F02 | `ISshRemoteSession.RunCommand(command, timeoutSeconds)` 重载；SshNet 实现 `CreateCommand→BeginExecute→WaitOne(timeout)` 超时 `CancelAsync` 返回 ExitCode=-1；SshScanChannel 三处调用传 TimeoutSeconds | ISshRemoteSession.cs / SshNetRemoteSession.cs / ScanChannel.cs |
| F03 | OnRunClicked try/catch/finally 包 await 段；finally 复位 `_running` 并守卫 IsDisposed 后刷新按钮；渲染前 IsDisposed 早退 | ScannerCenterForm.cs |
| F04 | 面板移除循环对所有非 Label 子控件 RemoveAt + Dispose | ToolboxPanel.cs |
| F05 | PortScanner onRun 改 Task.Run fire-and-forget，输出走自带封送的 AppendLine，状态经 root.BeginInvoke；NetworkScanner/CertInstaller/TimeSync/RepoConfig 同款 RunBackground<T>+SetStatus 辅助 | Modules/*.cs |
| F06 | ChangeRequested handler 改 async + try/catch 弹窗，去 GetResult() | ToolsDialogsLauncher.cs |
| F07 | ScanPlugin 增 VerifiedScriptSha256（信任判定时算），RunOne 执行前重算比对，不符拒执行 | ScanModels.cs / ScanPluginStore.cs / ScanRunner.cs |
| F08 | 目录遏制加尾部分隔符前缀（dirFull + DirectorySeparatorChar 或相等） | ScanPluginStore.cs |
| F09 | 删静态单例 SharedScanPluginStore；MainForm 组合根持字段注入 launcher 与 AppShutdownCoordinator；退出 DiagLog.Try Dispose | MainForm.cs / ToolsDialogsLauncher.cs / AppShutdownCoordinator.cs |
| F10 | ExtractBool 跳空白后从键位起 Compare("true"/"false", Ordinal) | ToolConfigBase.cs |
| F11 | ARCHITECTURE.md：D3 补已知债（IToolModule 返回 WinForms Control）；特性列表补扫描插件体系一行 | ARCHITECTURE.md |
| F12 | UI 侧 8 处裸 catch 补 DiagLog.Swallowed（ToolsDialogsLauncher×3、ToolboxPanel×1、ScannerCenterForm×4）；库侧保持静默+注释（不可引 UI） | 同左各文件 |
| F13 | RefreshOutdatedBuiltin 增加 manifest 备份（.bak，覆盖式） | ScanPluginStore.cs |
| F14 | WaitForExit 超时调 TryTerminateRemote（Win32_Process.Terminate）后返回；Execute 主流程超时不取回结果；TryReadFile 仅在无既有错误时写入 | WmiScanChannel.cs |
| F15 | attention.md 台账路径改为实际 `data\plugins\config\scanner-approved.json` 并注明推导式 | attention.md |
| F16 | stderr 行改 AppendRawLine 并删 TextBoxExtensionsForScanner；菜单图标 scaneye→radar（MenuIconFactory 新增 radar 图形）；ToolPanelHelper 孤儿 dummy TextBox 改传 null 占位；PluginCompleted 事件保留（预留设计） | ScannerCenterForm.cs / MenuIconFactory.cs / MainFormMenuBuilder.cs / ToolPanelHelper.cs |

兼容性决定：`RunCommand(string)` 单参版保留并转发 `(command, 0)`（无限等待），CertificateInstallerTool / TimeSyncTool / RepoConfigTool 的远端调用行为不变。

## 4. 执行计划

- [x] 安全批次（F07/F08）— 两处编辑落盘，静态核对路径前缀与哈希比对逻辑
- [x] P1 批次（F01-F06）— 六处编辑落盘
- [x] 结构批次（F09/F10/F12/F13/F14/F16）— 编辑落盘
- [x] 文档批次（F11/F15）— ARCHITECTURE.md / attention.md 更新
- [x] change package 落盘 — 本文档

## 5. 执行证据

- 实际修改 24 个文件（23 源码 + 2 文档，含审计包新增目录），git diff 统计 472 insertions / 118 deletions。
- **构建验证受限**：本环境无 .NET SDK / msbuild / mono（attention.md 载明需在 Windows 上 MSBuild 编译）。已做替代验证：
  - 每处编辑基于编辑前精确回读的代码锚点；
  - 接口变更（ISshRemoteSession 加重载）grep 确认唯一实现类 SshNetRemoteSession 与全部调用点，测试目录无引用；
  - 删除静态属性 SharedScanPluginStore 后 grep 全仓确认无残留引用；
  - ScannerCenterForm 新增 using Gdterm.UI.Diagnostics 与 DiagLog 调用点逐一核对；
  - MenuIconFactory radar 分支使用同文件已有 L(g,p,x1,y1,x2,y2) 辅助，签名一致。
- 待 Windows 侧执行：`msbuild gdterm.sln /p:Configuration=Release` 编译 + 冒烟（扫描中心跑内置插件、工具箱五模块、Keepass 改密对话框）。

## 6. 验收结果

- [x] 16/16 finding 均有对应代码或文档修改，映射见第 3 节
- [x] 未修改审计包（2026-08-25-plugin-ui-audit 保持 closed）
- [ ] Windows 构建通过 + 冒烟（环境限制，遗留）

## 7. 遗留事项

1. **Windows 构建 + 冒烟未执行**（本环境无 SDK）。重点回归：SSH 扫描通道超时路径、WMI 超时终止路径、Keepass 改密异步流、工具箱五模块后台化后的输出封送。
2. F16 遗留项：每次选中插件 new 一个会话包装探测 RemoteSessionAvailable 的轻量开销保留现状（审计判定可接受）。
3. F11 根治（IToolModule 与 WinForms 解耦）需接口重构，超出本次缺陷修复范围，建议另立 refactor 变更。
