---
doc_type: change
kind: issue
slug: 2026-09-25-ui-fix-audit-findings
status: accepted
phase: analyzed
mode: standard
summary: 修复 2026-09-25 UI 审计三处真缺陷——登录脚本步骤排序死按钮、主密码验证框 28px 无取消、外观数字框裸 86px
tags: [ui, audit-fix, dead-button, input-height]
risk:
  level: low
  reasons: []
model_route:
  strategy: retain-high
  reason: 三处缺陷分散在事件接线/安全弹窗/DPI 缩放三类上下文,需判断修复边界(不扩_scope)
contract:
  include:
    - src/Gdterm.UI/Controls/LogonScriptPanel.cs
    - src/Gdterm.UI/Services/MasterPasswordPrompt.cs
    - src/Gdterm.UI/Forms/AppearanceSettingsForm.cs
    - .codestable/changes/2026-09-25-ui-fix-audit-findings/**
  exclude: []
  preexisting_changes:
    - .codestable/.runtime/current-package
  baseline:
    git_head: 20ee05bb8c0ccaaf887627c0080fc650766aa5ba
    dirty_hashes:
      .codestable/.runtime/current-package: 427412eaef2b555d0f81d7a2cd10b25ed936b02804d842422acbd6caa2041af6
  architecture_impact: unchanged
  architecture_reason: 三处均为控件级修复,不改模块边界
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 审计缺陷修复,不改变需求
  requirement_refs: []
  context_refs:
    design: []
    impl: []
    accept: []
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-ui-fix-audit-findings/change.md
      depends_on: []
  evidence_ledger: false
---

# 审计缺陷修复:死按钮 + 主密码框 + DPI 宽度

## 目标与边界

修复 2026-09-25-ui-coverage-input-audit 审计报告中 P1/P2 三处真缺陷:

- **F1(P1)** LogonScriptPanel 步骤编辑对话框的 ↑/↓ 按钮无 Click 接线——步骤无法排序,界面可点但无效果。
- **F4(P1)** MasterPasswordPrompt 手写安全验证框:输入框 28px 硬编码高度(全仓口径 ≥38)、无 CancelButton(ESC 不可取消)。
- **F9(P2)** AppearanceSettingsForm.MakeNumber 数字框宽度裸 `new Size(86, …)` 未走 DpiScale.V。

不做:F2/F3 死代码删除(包 F)、F5 高度 SSOT 抽取与 F6 两窗体补 Cancel(包 E,行为增量类)、F7 快捷键 SSOT、F8 未解锁引导(需设计取舍)。

## 行为增量

- ADDED:登录脚本步骤支持在编辑对话框内上移/下移排序(选中项与相邻项交换 + 列表刷新,语义与 +/− 按钮一致)。
- MODIFIED:主密码验证框 ESC 关闭(等价取消);输入框/按钮/标签全部高度对齐 fieldH 口径。
- MODIFIED:外观设置数字框宽度随 DPI 缩放(86 → DpiScale.V(86))。
- REMOVED:无。

## 设计与契约

### 术语约定

- fieldH 口径:`Math.Max(DpiScale.V(this,38), FormFontPolicy.RowStep(this))`(仓库主流,17 窗体在用)。
- 步骤排序语义:`steps` 是对话框局部 List,交换相邻元素后 `refreshSteps()`。

### 决策与约束

- D1(排序修复)在 btnDelStep.Click 之后补 btnUp/btnDown 两段 handler:取 SelectedItems[0].Index,与 index-1/index+1 交换 `steps` 元素,再同步 ListViewItem 选中位置。不引 ListViewItemMove 库,不重排整表。
- D2(主密码框)布局由绝对坐标改字段驱动:引入 `int fieldH = Math.Max(DpiScale.Factor(dialog)*38 等价 DpiScale.S, RowStep)`;对话框是临时 Form,RowStep 用 dialog 字体计算(与 MasterPasswordPrompt 现有 UiFont 逻辑同源)。增加 `CancelButton = cancelBtn`——需要一个取消按钮(验证失败路径已有"取消"语义缺口,显式按钮 + ESC 双通道);按钮文字"取消",DialogResult.Cancel。对话框 Size 随 fieldH 重算。
- D3(86px)单 token 改 `new Size(DpiScale.V(this, 86), _fieldHeight)`,不抽常量(仅一处,包 E 再做 SSOT)。

### 名词与编排

- 修复点 3 个文件,均已在 contract.include。
- 验证:本地无 dotnet,静态 RED/GREEN 驱动(python 文本断言)+ CI 实测仲裁(与既往包同口径)。

## 验收契约

- S1 LogonScriptPanel 存在 btnUp.Click/btnDown.Click 两段订阅,handler 含相邻交换逻辑,refreshSteps 被调用。
- S2 MasterPasswordPrompt 存在 CancelButton 赋值,pwdBox 高度来自 fieldH 变量而非字面 28,不再出现 `335, 28)` 字样。
- S3 AppearanceSettingsForm.MakeNumber 宽度含 DpiScale.V。
- S4 反向:本包 diff 仅触三个文件;无新依赖;btnUp/btnDown 之外按钮行为不变(diff 不触及 +/− handler)。

## 执行计划

1. Step1 RED:静态驱动断言三处现状缺陷(btnUp 无 Click 等),exit 1。
2. Step2 F1 修复:补两段排序 handler。
3. Step3 F4 修复:fieldH 化 + CancelButton。
4. Step4 F9 修复:DpiScale.V 包裹。
5. Step5 GREEN:驱动全绿 + check-compliance impl。

## 执行证据 (impl 阶段追加)

**状态口径**:kind issue 用 in-progress:analyzed(与包 B/C 同例),4 步按执行计划完成。
**环境约束**:本机无 dotnet/WinForms,验证=本地静态驱动 + 括号平衡检查,CI 实测仲裁。

- Step1 RED(12 checks):驱动初版对三处缺陷现状全数成立(btnUp/btnDown 零订阅、字面 28 高、无 CancelButton、裸 86 宽),exit 0(判据式 RED:断言"缺陷存在")。
- Step2 F1:LogonScriptPanel.cs 在 btnDelStep.Click 后补 btnUp/btnDown 两段 handler(相邻交换 + refreshSteps + 选中保持),+/− 原逻辑未动。
- Step3 F4:MasterPasswordPrompt.cs 引入 fieldH = Math.Max(DpiScale.V(dialog,38), RowStep(dialog))(与 17 窗体同口径),pwdBox 高度 335,28→335,fieldH;新增取消按钮(185,105 与验证钮同行)+ dialog.CancelButton 绑定,Controls.AddRange 一并加入;首版误写 DpiScale.Factor(dialog)*38(float/CS0266 风险),当场改回仓库正字 DpiScale.V。
- Step4 F9:AppearanceSettingsForm.cs MakeNumber 宽度 86→DpiScale.V(this,86)。
- Step5 GREEN:驱动 18 checks ALL OK exit 0;三文件括号平衡 ok(/tmp/cs-balance.py)。
- 驱动自身两个坑(已修,记为教训):①正则 `i + 1` 中 `+` 未转义成对空格量词致"下移"判据误 FAIL;②cancelBtn 判据窗口 {0,120} 小于实际 170 字符距。均为检查器误差,非被检代码缺陷。
- 反向检查:git diff --stat 仅三 contract 文件 + 驱动 + 本文档;无新 NuGet。

## 验收结果 (accept 阶段追加)

CI 0.1.335(commit f304b00)success 2026-09-25T14:07:29→14:09:32Z:单元 163/0、UI 冒烟 6/0、ui-tree-check ALL OK、encoding-guard ALL OK。三缺陷编译验证通过:

- S1 ✓ 驱动 S1 全 ok(btnUp/btnDown 接线+交换逻辑+刷新+对照不回归)。
- S2 ✓ master 驱动 S2 全 ok(fieldH 口径、CancelButton、字面 28 清零)。
- S3 ✓ 驱动 S3 全 ok(DpiScale.V 包裹)。
- S4 ✓ git diff 边界=contract 三文件;无新依赖;+/− handler 原样。
- 动态行为(实际点按钮排序/ESC 取消)由 CI 编译+冒烟回归侧面覆盖,UI 交互人工验收留待下一版 Windows 实测。
### 合规复查口径说明

accept 复查(--phase accept)于本轮三包全部落盘后统一执行:git.diff.scope 列出的"包外文件"全部为同轮姊妹包(D/E/F 互照)的已提交文件,属跨包噪声;各包 impl 阶段合规在其自身窗口内为 pass(见执行证据),全仓最终状态 163/0 单测 + 6/0 冒烟 + 双 CI 门绿(0.1.335/336/337)为实际门禁。无真实越界文件。


