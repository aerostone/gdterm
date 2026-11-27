---
doc_type: change
kind: issue
status: in-progress
phase: analyzed
mode: standard
slug: 2026-11-27-dialog-buttons-credentials
severity: P1
risk:
  level: high
  reasons:
  - credential_input
  - shared_ui_policy
contract:
  include:
  - src/Gdterm.UI/Forms/*.cs
  - src/Gdterm.UI/Controls/*.cs
  - src/Gdterm.UI/Services/ToolsDialogsLauncher.cs
  - src/Gdterm.UI/Services/FormFontPolicy.cs
  - src/Gdterm.Core/L10n/L10n.cs
  - src/Gdterm.Tests/Ui/*.cs
  - src/Gdterm.Tests/Gdterm.Tests.csproj
  - src/Gdterm.UI/Gdterm.UI.csproj
  - docs/FEATURE-MAP.md
  - docs/DESIGN-LANGUAGE.md
  - tools/ui-baseline/ui-smoke.json
  - tools/ui-matrix-ratchet.txt
  - .codestable/tools/ui-coverage-registry.txt
  - .codestable/changes/2026-11-27-dialog-buttons-credentials/**
  exclude: []
  preexisting_changes: []
  baseline:
    git_head: 771882ca7777afce8c47969be532a9d1179a0217
    dirty_hashes: {}
  architecture_impact: unchanged
  architecture_reason: 沿用 FormFontPolicy 和 IKeePassService；密码仅进入已有加密库，连接仅保存引用。
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 修复已有连接和凭据创建流程及设计规范的一致性。
  requirement_refs: []
  context_refs:
    design:
    - src/Gdterm.UI/Forms/ConnectionDialog.cs
    - src/Gdterm.UI/Forms/KeePassManagerForm.cs
    - src/Gdterm.UI/Forms/SetupWizardForm.cs
    impl:
    - src/Gdterm.UI/Services/FormFontPolicy.cs
    - src/Gdterm.Tests/Ui/DialogsSmoke.cs
    accept:
    - docs/UI-UX-VERIFICATION.md
  artifacts: []
  evidence_ledger: false
---

# 对话框按钮与 SSH 凭据修复

## 1. 目标与边界
用户报告首次向导按钮与其他页面的 margin/padding 不统一，添加 SSH 时没有密码输入框。检查全部按钮创建点，统一同类按钮；修复连接/凭据编辑、创建的实际保存路径。使用现有测试与 Windows CI 采集真实布局，开发机只做编译与静态校验。

## 2. 设计与契约
- SetupWizardForm 的下一步采用默认 Margin(3)，取消采用 (0,0,12,0)，同排错位；对话框页脚有多套内外间距。共享页脚策略，逻辑间距 8、外边距 16/8、按钮内容留白统一；紧凑工具条保留独立密度。
- KeePassEntryEditForm 密码控件没有 Dock/MinimumSize，嵌套表格虽有高度但输入框未铺满。表单十行固定 470 高也不够；改为可滚动且固定页脚，补入矩阵采集。
- ConnectionDialog.CommitValidated 成功分支未调用 SaveToConfig，Result 一直为 null。保存前调用并覆盖创建/编辑往返。
- 新建 SSH/RDP 可输入密码，非空时创建独立 KeePass 条目后绑定；空值保留现有引用/自动匹配，不覆盖共享凭据；锁库需就地解锁，失败保持表单，取消不写库；密码不进入 ConnectionConfig/Metadata/日志。
- KeePassManagerForm.OnAddClick 漏传 Hostname/Port/Protocol/AutoTypeSequence；统一读取表单结果，避免 picker/manager 两条创建路径分叉。
- ConnectionDialog 加载无端口的配置时一律回填 22，覆盖协议切换事件设置的默认值；改为按 RDP/SSH/Telnet/Serial 分别取 3389/22/23/9600，已有端口不变。

比较：逐页修散值会继续漂移；全局覆盖所有 Button 会破坏终端键条和图标密度。采用显式页脚策略+既有尺寸入口，按调用点核对。

## 行为增量
创建连接时可直接输入密码；已有凭据保持原样；保存返回有效连接。向导和普通对话框的底部按钮共享同一间距。

## 3. 执行计划
1. 统一页脚布局与按钮度量，扫描全部创建点，记录紧凑/图标例外。
2. 修复密码输入和保存链路，保证取消/失败/编辑语义。
3. 补真实表单状态及保存回归，执行编译、规范门和 Windows CI，取回截图/控件树核对。

## 4. 执行证据
- 全量扫描 UI Button 创建点：151 处（包含按钮工厂，运行时实例数不等于此计数）。统一页脚策略接入 39 处。
- 普通窗口/模态表单页脚统一；终端键条、文件工具栏、复选选项旁按钮、图标按钮保留既有密度；新页脚规则用真实布局验证同排高度、margin 和可见间隙。
- 本地 csc-check（369 文件）、csproj 列表、编码、UI lint、i18n、安全闸门和 feature-map 检查通过。
- Windows 51f53ba：构建、1017 单测、UI 冒烟、控件树全部通过。新增密码输入、凭据表单往返、连接创建/编辑/取消/写库失败断言通过。
- 截图取回 /tmp/gdterm-ui-45957035074；核对向导欢迎/密码、凭据编辑、SSH 连接、AI/外观设置、OTP、凭据选择、SSH 密钥、SFTP 权限和主机密钥窗口。发现 AntdUI Padding 会缩进可见按钮，26156f1 改为尺寸留白、Padding=0、WaveSize=0；后续验证进行中。
- UI 矩阵为 DpiScale 注入 + 真实字体轴，不等同真实跨屏 DPI 操作。
- Windows 4b45f34：常规 UI 和 64 张像素基线通过。6 组矩阵产出 39 页 × 6 = 234 份；14pt 时凭据选择器表格中间的伸缩列挤到相邻按钮下。按按钮实测宽度设置最小客户宽度；搜索和排序输入也按实际字段高度撑开。

## 5. 验收结果
进行中。Windows 冒烟和控件树通过，已人工审查更新后的按钮、密码页截图并更新对应基线；字体与布局比例矩阵待验证。
