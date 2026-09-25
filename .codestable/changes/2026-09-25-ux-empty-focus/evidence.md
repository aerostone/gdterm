---
doc_type: change-evidence
change: 2026-09-25-ux-empty-focus
status: accepted
created: 2026-09-25
---

# 执行与验收证据：2026-09-25-ux-empty-focus

> 本文件承载可回放的命令、夹具、CI 产物与逐条验收判定。
> 设计/契约 SSOT 见同目录 `change.md`（保持精简，≤300 行）。

### 阶段：impl（2026-09-25）

**本地约束**：本机无 dotnet/mono/msbuild/wine，WinForms 不可编译、不可运行。故本地证据 = 静态契约驱动 +
夹具（Python），动态证据 = CI 322 的冒烟截图/dump。与 2026-09-24 可交互性 change 同一模式。

**1) RED（实现前）**：`fixtures/check-ux-empty.py` 一份驱动对三个窗体 + 冒烟 + 树检做 26 项契约检查，
实现前 15 项 S1–S4 全红（exit 1），逐条对应 S1 引导 Name/文案/两态显隐、S2 空态开关与右列 pad、
S3 状态行字段与两处赋值、S4 KeyPreview/Escape/两分支。

**2) GREEN（实现后）**：同一驱动 26 项全 ok（exit 0）。命令：
`python .codestable/changes/2026-09-25-ux-empty-focus/fixtures/check-ux-empty.py`（需在仓库根跑）。

**3) 几何验证（本机可做的最强近似）**：把新控件按 `UiTreeDumper` 的口径合成进 CI 321 的真实 dump，
再用 `tools/ui-tree-check.py` 复验，避免"本地不可跑 → 上线才发现重叠/越界"：
- `password-health`：新状态行 abs=(405,140,395,24)，与评分标签 x 向隔 10px、与摘要标签 y 向隔 6px、
  右缘 800 < 父右缘 830 → 全绿（首版按 12/26 放会与摘要标签交叠 3000px²，被树检当场判红后改位）。
- `keepass-manager`：空库引导层按"与表同格 + Dock=Fill"合成 → 兄弟重叠 0、越界 0、R1/R2/R4 均 0。
- `scanner-center`：空态提示 + 右列 pad 对齐合成 → 全绿。

**4) D5 新规则的红绿（真实产物，非构造）**：
- RED：`python tools/ui-tree-check.py /tmp/ci321`（CI 321 原始 18 份 dump）→ `UI-TREE-CHECK FAIL: 3`，
  全部来自 scanner-center：工具栏(Top) vs SplitContainer(Fill) 交叠 53760px²、FindingHeader(Top) vs
  FindingTable(Fill) 15552px²、原始输出(Top) vs Input(Fill) 15552px²。**其中第一处正是"表头看不见"的根因**
  ——旧重叠规则对"Dock≠None 的一律跳过"结构性地看不见它，只有像素探针才暴露。
- GREEN（夹具）：`fixtures/dock-overlap.json` → 2 FAIL（exit 1）；`fixtures/dock-clean.json` → 全绿（exit 0）。
  命令：`python tools/ui-tree-check.py .codestable/changes/2026-09-25-ux-empty-focus/fixtures`。
- 全量 18 份 dump 的该签名命中统计：scanner 3（本次修复）+ dangerous-cmd 3（存量、非本 change 文件、
  已在树检按前缀豁免并登记）+ 其余 16 份 0。

**5) 反向核对**：`git diff --stat` 只含 5 个契约内文件（三窗体 + UiSmokeRunner + FakeKeePassService）
与 tools/ui-tree-check.py；无新增 PackageReference；无 MainForm.cs 改动；无新 NuGet。
`/tmp/cs-balance.py`（剥离字符串/注释后做括号配平）对 5 个 C# 文件全部 ok。

**6) 实现期偏离（对已批准设计的修正，逐条留痕）**：
- 偏离 1（D1）：设计写"空引导按钮调用 OnAddClick/打开入口"，但 KeePassManagerForm 无"连接/切换库"入口
  （IKeePassService 无该能力，kdbx 路径在 Program.cs:215 决定）。改为 [添加第一条]+[刷新] 两个**已存在**动作
  + 说明文案，未新增能力，D1 语义不变。
- 偏离 2（D3）：设计设想的"运行中 ESC → 停扫描"不可实现——`ScanRunner` 无 CancellationToken/Cancel API
  （RunOne 不可中断）。改为上下文守卫：运行中 ESC 提示"请等待本次扫描完成"且不关窗（保护在飞输出），空闲 ESC 关窗。
- 偏离 3（D4）：设计说"表头高度抬到 stateY+rowH+padding"，实测 headerPanel 高 80 + rowH 38 已贴边，
  改为与评分同行右侧（275,10 395x24 右对齐），不动面板高度，几何经真实 dump 复验。
- 偏离 4（实现细节）：KeePass 引导的 TableLayoutPanel 显式补 `ColumnStyles` 100%——不写列样式时 TLP 列按内容
  AutoSize，内容会偏左而非居中（"居中引导"是本条的验收点）。
- 偏离 5（范围）：发现并修复 scanner-center 根级 Dock 顺序 + 右列表头遮挡（D5），两者均在设计决策之外，
  按"发现即记录"处理：写进 D5 + 行为增量 + 本节，并补 CI 门与树检规则，而非静默扩大改动。

**7) 尚未取证（等 CI 322）**：C# 侧 4 处 `AssertNoDockOverlap`、三种空态断言、ESC/KeyPreview 断言、
`keepass-manager-empty.png/json` 新 artifact——需 CI 322 全绿才算 S1/S2/S4/S6 闭环。

### 阶段：accept（2026-09-25）

**CI 323**（AppVeyor build 0.1.323，commit 82eb57c，jobId 见 build 记录，24 个 artifact）：

- 单测 `Passed: 163  Failed: 0`；UI 冒烟 `Passed: 6  Failed: 0`（新用例 KeePassManagerEmpty 已在内）；
  dialogs 组 `dialogs-one-fail=0`。
- 新增 C# 停靠门 `AssertNoDockOverlap` 在 4 个用例实跑，均打印 `停靠无遮挡(0 处)`：
  KeePassManager / PasswordHealth / ScannerCenter / KeePassManagerEmpty。
- 新 dump 断言全部 ok：三个 scanner 空态提示可见、右列 `Left=8/8` 对齐、`KeyPreview` 生效、
  `HealthScanStateLabel` 文案 = `上次扫描：04:22:11 · 2 个条目`（非占位符）、KeePass 空库引导可见 +
  有 2 条目时引导入树但不可见（两态）。
- 新 artifact `ui-smoke/keepass-manager-empty.png|json` 已产出（9632/9466 字节）。

**逐条验收（19 份 CI 323 dump 全量过树检）**：

| 场景 | 判据 | 结果 |
|---|---|---|
| S1 空态引导 | keepass-manager-empty.json 有 KeePassEmptyGuide/…Title/…Hint/…AddButton/…RefreshButton；有 2 条目时引导 visible=false | ✅ dump 实测可见；空态标题 abs(339,149,74,19) 水平居中（父中心 376 = 标题中心 376） |
| S2 空态与对齐 | 三处空态提示 Name 存在且可见；右列两面板左内边距一致 | ✅ 两表 `Left=8/8`；提示文案见下 |
| S3 健康状态行 | HealthScanStateLabel 存在、文案以 `上次扫描：` 开头且非占位符 | ✅ 实测 `上次扫描：04:22:11 · 2 个条目` |
| S4 scanner ESC | KeyPreview=true + Escape 上下文守卫（运行中不关窗、空闲关窗） | ✅ CI 断言 KeyPreview；分支逻辑由夹具 S4 静态验证 |
| S5 R4 键盘可达性 | 18+1 份 dump 全量：交互叶 TabStop=false 数=0 | ✅ 逐文件打印 0 |
| S6 停靠遮挡门 | C# 门 4 用例实跑 0 处；R5 对 CI 323 全量 ALL OK | ✅ R5 对 CI 321 报 3 处 FAIL（修前），对 CI 323 报 ALL OK（修后） |
| 反向 | 无新 NuGet、无 MainForm.cs 改动、改动文件全在契约内 | ✅ `git diff --stat` 5 个源文件 + tools/ui-tree-check.py |

**P0"表头看不见"修复的量化证据**（scanner-center，dump 绝对几何）：

| 控件 | CI 321（修前） | CI 323（修后） |
|---|---|---|
| ScannerPluginTable | y=164 h=600（与工具栏同起点，被压 56px） | y=220 h=520（工具栏下方） |
| ScannerFindingHeader | (460,164) 与表同原点 | (468,220,640,24) 表上方 |
| ScannerFindingTable | (460,164,648,312) | (468,244,640,236) |
| ScannerRawHeader / RawOutput | (460,488) 与输入框同原点 | (468,516,640,24) / (468,540,640,224) |
| R5 停靠遮挡 | 3 处（53760 / 15552 / 15552 px²） | 0 处 |

像素侧交叉印证：空态引导三行内容在 700x384 引导区内水平居中（标题中心 = 父中心），
垂直分布均匀（标题/说明/按钮各占一档），右侧 Close 按钮与底部状态栏均正常渲染。

**遗留（已登记，不属本 change）**：
1. dangerous-cmd 存量 3 处停靠遮挡（`tools/ui-tree-check.py` 内按前缀免判，C# 门未挂该用例）——
   应在后续 change 按同一签名修复。
2. ScannerCenterForm 639 行混合扫描编排/WMI/插件管理，本次未拆（设计已显式延后为独立 cs-refactor）。
3. AntdUI.Table 浅色背景默认值未处理（外观债，已记录）。
4. 仓库级既有合规失败 2 条：`attention.baseline_mode`（YAML 把 off 解析成布尔 False）、
   `architecture.read`（ARCHITECTURE.md 无 frontmatter）。二者在已关闭的 2026-09-24 包上同样复现，
   属仓库既有问题；修 ARCHITECTURE frontmatter 会连带触发其正文 `文件:行号` 锚点校验，
   故不在本 change 内顺手改，另立任务处理。

**方法学产物**：本 change 把"空态不可见/表头被压"这类只能靠人眼看图发现的缺陷，
变成了可回放的机检信号（R4 键盘可达性 + R5 停靠遮挡 + C# AssertNoDockOverlap），
且 RED（CI 321 真实产物报 FAIL）与 GREEN（CI 323 全绿）都在**真实 artifact** 上验证过，
而非仅合成夹具。
