---
doc_type: change
kind: feature
slug: 2026-09-25-hotkey-ssot-unlock-guide
status: in-progress
mode: standard
summary: F7 快捷键 SSOT(路由表化,帮助/菜单同源) + F8 未解锁死端提示改直达解锁流程 + SharedMenuTip 改名 MenuTip
tags: [ux, hotkey, ssot, keepass, rename]
risk:
  level: low
  reasons: []
model_route:
  strategy: direct
  reason: 表驱动重构+对话框接线,无算法难点
contract:
  include:
    - src/Gdterm.UI/Services/MainFormCommandRouter.cs
    - src/Gdterm.UI/Services/ToolsDialogsLauncher.cs
    - src/Gdterm.UI/Controls/SharedMenuTip.cs
    - src/Gdterm.UI/Controls/MenuTip.cs
    - src/Gdterm.UI/Forms/MainForm.cs
    - src/Gdterm.UI/Services/MainFormMenuBuilder.cs
    - src/Gdterm.UI/Gdterm.UI.csproj
    - .codestable/changes/2026-09-25-hotkey-ssot-unlock-guide/**
  exclude: []
  preexisting_changes:
    - .codestable/.runtime/current-package
  baseline:
    git_head: 399349a46772f9f803337ab5aec2e87cbbb08121
    dirty_hashes:
      .codestable/.runtime/current-package: d0095dba40d22bce6dc5f5315df8f105109592ad5b8420be5542547aa02c6f2c
  architecture_impact: unchanged
  architecture_reason: 服务内部重构与命名,不改模块边界与依赖方向
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: UX 增强不改既有能力承诺
  requirement_refs: []
  context_refs:
    design:
      - .codestable/changes/2026-08-11-architecture-audit/change.md
    impl: []
    accept: []
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-hotkey-ssot-unlock-guide/change.md
      depends_on: []
  evidence_ledger: false
---

# 快捷键 SSOT + 未解锁引导 + MenuTip 改名

## 目标与边界

审计 F7/F8 与命名遗留的合并收口:

- F7:快捷键帮助文本硬编码,与 MainFormProcessCmdKey/CommandRouter/菜单 ShortcutKeys 三处绑定人肉同步,必然漂移。目标:路由器内建静态注册表(键组合→动作描述),`TryHandle` 按表分发,`ShowHotkeysHelp` 从同一张表渲染——帮助=SSOT 投影。
- F8:ToolsDialogsLauncher 三处 OpenKeePassManager/OpenPasswordHealth/OpenSshKeyManager 在未解锁时弹"密码库未解锁"即 return,用户无出路。目标:仿 ProtocolTabOpener:82 先例,弹 KeePassUnlockForm 解锁,成功后继续打开原目标窗体;仍失败则给出原因提示。仅解锁服务缺失(null)时保留死端提示。
- 命名:Services/SharedMenuTip.cs 只剩 ToolTipText2 扩展,改名 MenuTip.cs(类 ButtonTipExtension 同步改名 MenuTipExtension)。

不做:KeyBindingPanel(终端键位绑定,独立体系);Ctrl+` 全局热键(GlobalHotkeyController,RegisterHotKey 非 ProcessCmdKey 域)与 Ctrl+Shift+K(MainForm.ProcessCmdKey 直开 QuickJump)登记进表但路由点注明,不强行收拢到 router。

## 行为增量

- ADDED:未解锁时打开 KeePass/健康/SSH密钥 → 弹解锁框(既有 KeePassUnlockForm),解锁成功直达原窗体。
- MODIFIED:ShowHotkeysHelp 文本由手写串改为注册表渲染(内容与现状等价+表驱动校验);TryHandle 从 if 链改为表查找+特例链,行为等价。
- RENAMED:SharedMenuTip.cs→MenuTip.cs,ButtonTipExtension→MenuTipExtension(10 处调用点 using/方法不动,ToolTipText2 方法名不变)。

## 设计与契约

### 术语约定

- 注册表项:(Keys 组合, 描述, Action router 回调)。路由=先查表命中即执行;表外的 Escape/F11/Ctrl+Tab 族/Digit 族保持显式 if(它们带参数化语义,不适合平表)。

### 决策与约束

- D1:注册表放 MainFormCommandRouter 内部静态(只读),Help 渲染经 router 暴露;ToolsDialogsLauncher.ShowHotkeysHelp 从 router 表生成文本——同一程序集 internal 可达。
- D2:菜单 ShortcutKeys(L/M)不挂表(菜单项自带快捷键显示,WinForms 已是 SSOT);帮助文本中 L/M 行由表+菜单双源,标注即可,不追求菜单反射(反射 ToolStripMenuItem.ShortcutKeys 展示属过度工程)。
- D3:解锁引导只动 ToolsDialogsLauncher 三方法;KeePassUnlockForm 返回 OK 后复查 IsUnlocked,再开目标窗体;unlock==null 或解锁后仍非 unlocked → 单一提示"密码库未解锁,请检查 data/ 下 kdbx 或重设主密码"。
- D4:改名走 git mv + csproj 行改 + 类名替换,ToolTipText2 扩展方法名保持(避免动 10 个调用文件——本包 include 因此只有 5 个源文件)。

### 名词与编排

Step1 RED 驱动(断言表存在+帮助同源+解锁引导在)→ Step2 router 表化 → Step3 launcher 解锁引导 → Step4 改名 → Step5 GREEN+合规+CI。

## 验收契约

- S1 router 表覆盖现有全部 Ctrl+Shift 分支(R/W/F/P/H/G/Z),TryHandle 行为等价(代码审读+分支计数)。
- S2 ShowHotkeysHelp 文本由表渲染:驱动断言文本行含表中每条描述且 ToolsDialogsLauncher 不再硬编码键位串。
- S3 三处 Open* 未解锁路径=KeePassUnlockForm→继续打开;null 服务时保留提示。
- S4 git mv 改名,csproj 更新,全仓 SharedMenuTip/ButtonTipExtension 零残留(ToolTipText2 调用不变)。
- S5 编译卫生:balance ok;CI 163/0+冒烟 6/0。

## 执行计划

1. Step1 RED。
2. Step2-4 实施。
3. Step5 GREEN+合规+提交+CI 仲裁。

## 执行证据 (impl 阶段追加)

**状态口径**:feature 扁平状态机 draft→in-progress(用户「按你的建议来,请修复」+ 完全 ACT 授权)。

- Step1 RED:驱动 5 checks FAIL exit 1(表不存在/硬编码在位/无解锁引导/旧名在位)。驱动自身一坑:S4 路径写成 Services/ 而实际文件在 Controls/(审计 F2 时已在 Controls 下),断言路径修正并同步 contract。
- Step2 router 表化:HotkeyEntry(Combo/Desc/Run) + BuildRegistry() 8 项平表(K 登记为 Run=null 仅帮助展示,实际路由点在 MainForm.ProcessCmdKey 先于 router);TryHandle 平表 foreach 分派,Escape/F11/Ctrl+Tab/Ctrl+Shift+Tab/Alt+Digit 特例链原样保留;RenderHelpLines/FormatKeys 供帮助投影。balance ok。
- Step3 launcher:①ShowHotkeysHelp 键位串删硬编码,BuildHotkeyHelpText=「固定头+router 表投影(SetHotkeyTableSource 注入的 Func<string>)+特例键固定尾」;MainForm 构造 router 后注入 _cmdRouter.RenderHelpLines。②EnsureKeePassUnlocked(action) 公共助手:null 服务→提示;未解锁→KeePassUnlockForm(仿 ProtocolTabOpener:82 先例),OK 且 IsUnlocked 才放行;取消静默放弃;异常给原因。三 Open* 全部改走助手,死端「密码库未解锁」提示从 3 处归零。balance ok。
- Step4 改名:git mv Controls/SharedMenuTip.cs→MenuTip.cs,ButtonTipExtension→MenuTipExtension,csproj 行同步,过时 F02 注释修正(记录 DarkMenuRenderer 已删);ToolTipText2 方法名不动,10 处调用文件零改动。全仓 SharedMenuTip/ButtonTipExtension 零残留。
- Step5 GREEN:驱动 14 checks ALL OK exit 0。

## 验收结果 (accept 阶段追加)

