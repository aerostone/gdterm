---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "ux-C4"
nature: ux-consistency
severity: P2
confidence: high
suggested_action: cs-refactor
status: reported
---

# Finding C4：语义层失效 —— "确定"作 Primary + Type/BackColor 双写

## 速答

`docs/DESIGN-LANGUAGE.md` §4.1 明文写着"禁止'确定/OK'类无语义文案做 Primary"，
但代码里有 **10 处**把"确定"设成 `TTypeMini.Primary`；另有 **7 处**同时写 `Type=TTypeMini.X`
和 `BackColor=`，等于把语义级别又用手调颜色盖掉 —— 设计师改 token 时这些按钮**不跟随**。

## 关键证据

R06（"确定" + Primary 同现）：

| 文件:行 | 场景 |
|---|---|
| `Controls/HighlightRulePanel.cs:205` | 高亮规则编辑框 |
| `Controls/LogonScriptPanel.cs:240/289` | 登录脚本编辑 / 步骤行 |
| `Controls/PortForwardPanel.cs:141` | 端口转发规则编辑 |
| `Controls/KeyBindingPanel.cs:355` | 键位编辑 |
| `Controls/SessionBookmarksPanel.cs:311` | 书签编辑 |
| `Forms/DangerousCommandConfigForm.cs:451/548` | 危险命令规则编辑 |
| `Forms/KeePassManagerForm.cs:738` | 条目编辑 |
| `Forms/QuickCommandEditorForm.cs:143` | 快捷命令编辑 |

R07（`Type` + `BackColor` 双写）：`MasterPasswordPrompt.cs:62`、`ViewModeController.cs:57`、
`HighlightRulePanel.cs:205`、`LogonScriptPanel.cs:240/289`、`PortForwardPanel.cs:141`、
`SnippetSearchPanel.cs:248` —— 其中 5 处与 R06 重叠，即"既语义 Primary 又手涂颜色"。

对照**已符合规范**的样例：`Forms/PasswordHealthForm.cs:113` 用
`Type = TTypeMini.Default` + 显式 Padding（语义正确，颜色交给 AntdUI）。

## 影响

- **语义层名存实亡**：`Type` 本意是"由 AntdUI 按主题决定观感"，双写后等于回调到"手涂颜色"，
  暗色三主题（Dark/Darker/OLED）切换时这些按钮不跟随（与 C6 同源）。
- **文案无信息量**：编辑对话框主按钮写"确定"，用户不知道按下去是保存、应用还是仅关闭；
  DL §4.1 要求"2–6 字动词优先"。
- 一致性成本：新面板作者抄最近的面板（多数抄到"确定"+Primary），违规会继续扩散。
  **这正是"有规范无门"的典型后果** —— 现已由 L0 R06/R07 拦住增量（C8）。

## 修复方向

1. 逐处改文案：高亮规则/转发/键位/书签/危险命令/KeePass 条目/快捷命令 → **"保存"**；
   登录脚本 → **"保存脚本"**；两步向导 → **"下一步"/"完成"**。
2. 删掉与 `Type` 并存的 `BackColor`；确实需要特殊底色的（如 `ViewModeController` 的退出聚焦钮）
   改为 `MakeSecondary/MakeGhost` 工厂（DL §4.1 的三级工厂），把"特例"收进工厂而不是散在调用点。
3. 修完后 `python3 tools/ui-ux-lint.py --write-baseline` 收紧，R06/R07 基线归 0。

## 建议动作

`cs-refactor`（文案与样式收口，行为不变）。属于"改到哪、迁到哪"的高性价比批量项：
11 个文件、约 17 处，一次性可清。
