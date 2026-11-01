---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "ux-C1"
nature: ux
severity: P1
confidence: high
suggested_action: cs-feat
status: reported
---

# Finding C1：11 处列表型 UI 无空态引导

## 速答

创建了 `ListView` / `ListBox` / `AntdUI.Table` 的 15 个 UI 文件里，**11 个没有任何空态引导**
（无提示文字、无"下一步"动作）。用户首次打开侧栏（历史为空、无转发规则、无通知、无宏）看到的
是**一整片背景色**，无法区分"空"与"还在加载"与"坏了"。

## 关键证据

`python3 tools/ui-ux-lint.py --strict` 的 R09 命中（可复跑）：

| 文件 | 说明 |
|---|---|
| `Controls/CommandHistoryPanel.cs` | 命令历史，首次安装为空 |
| `Controls/NotificationCenterPanel.cs` | 通知中心，首次为空 |
| `Controls/PortForwardPanel.cs` | 端口转发规则表 |
| `Controls/HighlightRulePanel.cs` | 高亮规则 |
| `Controls/KeyBindingPanel.cs` | 键位绑定 |
| `Controls/LogonScriptPanel.cs` | 登录脚本步骤 |
| `Controls/MultiChannelPanel.cs` | 多通道 |
| `Controls/SecretScanPanel.cs` | 密钥扫描结果 |
| `Controls/SnippetSearchPanel.cs` | 代码片段 |
| `Controls/ToolboxPanel.cs` | 工具箱 |
| `Controls/BatchCommandPanel.cs` | 批量命令目标列表 |

对照**已做好**的两处样板（说明团队知道该怎么做，只是没推广）：
- `Forms/KeePassManagerForm.cs` 有 `keepass-manager-empty` 空态 dump 用例（L1 已覆盖两态）；
- `Forms/ScannerCenterForm.cs` 有 `UpdateEmptyHints()`，随运行状态切换空态文案。

命名也分裂：`UpdateEmptyHints`（ScannerCenter）/ `EmptyHint` / 直接静默，三种写法并存。

## 影响

- 首次体验：新用户打开侧栏 → 空白 → 不知道功能存在（"这面板是不是没加载？"）。
- 空态与加载态视觉相同，`FilePaneControl` 靠状态栏文字区分，其他面板连文字都没有。
- 空态是"引导用户产生第一条数据"的唯一机会（新建连接 / 录制第一个宏 / 添加第一条转发），
  11 处全部放弃。

## 修复方向

1. 抽一个标准组件：`EmptyState(title, description, actionText, action)` ——
   标题 + 一句说明 + 一个主操作（DL §4.3 标签规格、§3.1 间距）。
2. 面板统一走 `SetEmptyState(bool)`：列表数据源为空时显示，非空时隐藏；
   加载中显示 `LoadingState`（与 C2 的 busy 语义合并）。
3. 命名统一为 `EmptyHint` / `EmptyGuide`，让 R09 的检测口径与实现口径一致。

## 建议动作

`cs-feat`（新增空态组件 + 11 处接入），可拆两批：先 6 个高频侧栏（历史/通知/转发/高亮/键位/宏），
再其余。
