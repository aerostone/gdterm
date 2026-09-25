---
doc_type: change
kind: issue
slug: 2026-09-25-dangerous-cmd-dock-overlap
status: accepted
phase: analyzed
mode: standard
summary: 消除 dangerous-cmd 配置页 3 处停靠遮挡，并把该签名提升为 CI 内真跑的对话框全量门
tags: [ui, layout, dock, regression-gate]
risk:
  level: low
  reasons:
    - 只反转一个窗体末尾 4 行 Controls.Add 顺序并修正注释
    - 新增断言挂在既有冒烟用例上，不引入新依赖、不改业务逻辑
contract:
  include:
    - src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs
    - src/Gdterm.Tests/Ui/UiSmokeRunner.cs
    - src/Gdterm.Tests/Ui/DialogsSmoke.cs
    - tools/ui-tree-check.py
    - .codestable/changes/2026-09-25-dangerous-cmd-dock-overlap/**
  exclude: []
  preexisting_changes: [.codestable/.runtime/current-package]
  baseline:
    git_head: dfbddb769dcd87d65b1396f6c918bf82a9bbc664
    dirty_hashes:
      .codestable/.runtime/current-package: 07b24781edb1b70abd12cd22b3dc50c3dae189b4ad5c9bf0d09aae889b7339db
  architecture_impact: unchanged
  architecture_reason: 只改一个窗体内部的装配顺序与一处测试断言可见性，不触及模块边界或依赖方向
  architecture_refs: []
  requirement_impact: unchanged
  requirement_reason: 不改变已归档的能力目标与边界
  requirement_refs: []
  context_refs:
    design: []
    impl:
      - path: src/Gdterm.UI/Forms/DangerousCommandConfigForm.cs
        symbols: [DangerousCommandConfigForm.BuildUi]
      - path: src/Gdterm.Tests/Ui/UiSmokeRunner.cs
        symbols: [UiSmokeRunner.AssertNoDockOverlap]
    accept: []
  model_route:
    route: retain-high
    reason: WinForms 停靠顺序语义须与 dump 几何反复对照，且新门的覆盖面判断含取舍
  artifacts:
    - id: design
      path: .codestable/changes/2026-09-25-dangerous-cmd-dock-overlap/change.md
      depends_on: []
  evidence_ledger: false
---

# dangerous-cmd 停靠遮挡修复 + 对话框全量停靠门

## 目标与边界

**目标**：清掉 `DangerousCommandConfigForm` 的 3 处停靠遮挡，并把"工具栏/表头盖住表体"这一缺陷签名，从**事后靠人眼与 dump 交叉比对**提升为 **CI 内真跑的门**。

这 3 处与 `2026-09-25-ux-empty-focus` 修复的 scanner-center 是**同一签名、同一根因**，在前一包已按既有债登记（写入了 `tools/ui-tree-check.py` 的前缀豁免与执行证据的"超出范围的观察"）。

`2026-09-25-ux-empty-focus` 的巡检口径对 19 份 CI dump 全量扫过，命中的只有两处：scanner-center（已修）与 dangerous-cmd（本包）。故本包是那一类缺陷的**收口**。

**边界（不做）**：

- 不改白名单区/规则表的业务逻辑、列定义、数据加载
- 不改 DialogsSmoke 既有的 `CancelButton` 断言与其 4 项免判（那是"关闭语义"豁免，与遮挡是两件事）
- 不动 `UiSmokeRunner` 已有的 4 个 `AssertNoDockOverlap` 调用点（KeePassManager / PasswordHealth / ScannerCenter / KeePassManagerEmpty）
- 不引入任何 NuGet 包；不做鼠标/键盘注入

## 行为增量

**ADDED**

- `DialogsSmoke.Show` 对每个对话框调用停靠遮挡门 → 13 个对话框用例全部纳入

**MODIFIED**

- `DangerousCommandConfigForm.BuildUi` 末尾 4 行 `Controls.Add` **顺序反转**（Fill 提到 index 0），并修正被 dump 几何证伪的注释
- `UiSmokeRunner.AssertNoDockOverlap` 可见性 `private static` → `internal static`（同程序集复用）
- `tools/ui-tree-check.py` R5：删除 `dangerous-cmd` 前缀豁免

**REMOVED**：无

## 设计与契约

### 决策与约束

**D1｜用"反转装配顺序"而非"再套一层容器面板"**：本窗体只有一个 Fill 控件（`DangerRuleTable`），不存在两个 Fill 兄弟争位的问题，反转顺序即可达意，改动面最小（4 行）。KeePass 空引导当时引入容器，是因为那里有两个 Fill 兄弟必须靠可见性互斥——条件不同，方案不必相同。

**D2｜统一规则：Fill 必须最先添加（index 0）**。布局引擎按 Controls 集合**从最大索引向 0** 迭代，索引越大越先分配；而 `Dock=Fill` 取的是"**此刻尚未被边停靠兄弟消耗的剩余区**"，且**它自己不消耗该剩余区**。

于是只要 Fill 不是最低索引，它就会在部分/全部边兄弟之前被处理而拿到**整个客户区**；索引比它更低的边兄弟随后仍按各自边缘**正确落位**（彼此不重叠），最终表现为它们全部压在 Fill 的表体上。

关键推论：**Fill 必须是 index 0**，才能最后被处理、拿到扣掉所有边带后的中间区。这条规则被三份真实 dump 逐值验证（见 `fixtures/dock-sim.py` 的自证段），其中"两个 Bottom 兄弟仍然正确堆叠"这一事实同时证伪了"边兄弟会因剩余区归零而挤在同一起点"的另一种猜测。

本窗体原注释写的是"**Fill 必须最后添加**"，被真实 dump 几何直接证伪：

| 控件 | dock | CI321/323 实测 abs |
|---|---|---|
| DangerRuleTable | Fill | (26, 26, 800, 520) ← 占满 800x520 客户区 |
| （工具栏）FlowLayoutPanel | Top | (26, 26, 800, 46) ← 同起点压在表上 |
| （白名单区）TableLayoutPanel | Bottom | (26, 384, 800, 162) |
| （状态条）Label | Bottom | (26, 358, 262, 26) |

反转装配顺序后，模型预测（客户区相对坐标，交由后续 CI 实测仲裁）：

| 控件 | dock | 预测 bounds | 依据 |
|---|---|---|---|
| DangerRuleTable | Fill | (0, 46, 800, 286) | 客户区扣掉 Top 46 与 Bottom 26+162 |
| （工具栏）FlowLayoutPanel | Top | (0, 0, 800, 46) | 贴顶 |
| （白名单区）TableLayoutPanel | Bottom | (0, 332, 800, 162) | 在状态条之上 |
| （状态条）Label | Bottom | (0, 494, 262, 26) | **贴最底**（最后添加 → 最先分配） |

注意最后一行带来的**可见变化**：修复前状态条在 (26,358)、白名单区在 (26,384)，即状态条浮在白名单区**上方**；修复后状态条落到最底。这与代码内注释声明的意图（"最底状态条" / "白名单区在状态条之上"）一致——即旧布局连注释声明的意图都没达成。

**D3｜门挂在 `DialogsSmoke.Show`（全部对话框），不只挂 dangerous-cmd**：CI 19 份 dump 里除 scanner-center（已修）与 dangerous-cmd 外**零命中**，故全量挂载是零噪声的，能一次覆盖 13 个对话框；只挂一个则下个窗体复发仍要靠人眼。此取舍与 R4"零命中即硬门"的口径一致。

**D4｜只删 R5 豁免，保留 R3 豁免**：`dangerous-cmd` 同时出现在两处前缀豁免里——R3 是"无关闭语义"（`CancelButton` 实测未绑，设计即此），R5 是"遮挡"。前者不该动，后者必须删，否则规则对唯一已知违例失灵。

### 名词与编排

- **停靠遮挡（dock-overlap）**：同父、可见、面积为正的两个兄弟，一个 `Dock=Fill`、另一个边停靠（Top/Bottom/Left/Right）且矩形相交。`Fill` vs `Fill` 刻意豁免（KeePass 空引导与表就是两个 Fill 兄弟，靠可见性互斥）。
- **前置顺序**：先删 Python 豁免拿到真实失败（RED），再改 C# 装配顺序（GREEN），最后挂门——顺序反了会把"改了但没验"当成"已修"。
- **可证伪的模型**（`fixtures/dock-sim.py`）：本机无 dotnet，无法编译验证"改顺序是否真的修好"。故用三份**真实 dump** 反推停靠算法并把模型钉死：模型必须能逐值复现修复前 dangerous-cmd 与修复前/后 scanner-center 三组几何，**自证不通过就拒绝输出预测**，通过后才允许给出修复后的预测几何。这样"改顺序就修好"从信念变成了可被真实 CI 实测证伪的预测。
- **夹具**（`fixtures/`）：`dangerous-cmd-prefix.json` 是 CI 323 该窗 dump 的**原样冻结**（真实产物，非合成）；`dock-sim.py` 是模型自证 + 预测；`check-dock-gate.py` 是本地驱动（29 项）。夹具不随 CI 产物老化而消失，规则口径可长期回放。

## 验收契约

- **S1｜真实产物 RED→GREEN**：删除豁免后，`ui-tree-check` 对 CI 323 的 `dangerous-cmd.json` 报 ≥1 处停靠遮挡（exit 1）；对本包修复后的 dump 报 `停靠无遮挡=0`（exit 0）
- **S2｜对话框全量门已挂**：`DialogsSmoke.Show` 调用 `AssertNoDockOverlap`；CI 日志中每个对话框用例都出现停靠断言行
- **S3｜几何正确**：修复后 `DangerRuleTable` 的 `abs.Y` = 工具栏底边，`abs` 底边 = 白名单面板顶边，与工具栏/白名单面板/状态条均不相交
- **S4｜反向检查**：无新增 NuGet；`UiSmokeRunner` 既有 4 个调用点仍在；`tools/ui-tree-check.py` 的 R3 豁免仍含 `dangerous-cmd`

## 执行计划

- **Step 1｜删豁免取得 RED**：从 `tools/ui-tree-check.py` 移除 R5 的 `dangerous-cmd` 前缀豁免 → 验证：对 CI 323 `dangerous-cmd.json` 复现 3 处停靠遮挡（exit 1）
- **Step 2｜改装配顺序取得 GREEN**：反转 `DangerousCommandConfigForm.BuildUi` 末尾 4 行 Add 顺序并修正注释 → 验证：按停靠语义合成的修复后 dump 过 R5
- **Step 3｜挂对话框全量门**：`AssertNoDockOverlap` 提升为 `internal static`，在 `DialogsSmoke.Show` 调用 → 验证：本地驱动 4 项断言全绿 + CI 日志逐用例含停靠行
- **Step 4｜收敛**：`--phase accept` 通过；反向 `git diff --stat` 核对 S4

## 执行证据

**状态口径**：`kind: issue` 的 `state_order` 只允许 `in-progress:analyzed`（`workflow.yaml`），故 `phase` 记 `analyzed`；实现动作按本包"执行计划"的 4 步完成。

**环境约束**：本机无 dotnet/mono/msbuild（WinForms 无法编译运行），动态证据由 CI 提供，本地承担静态断言与夹具驱动的红绿。这与前一包 `2026-09-25-ux-empty-focus` 同一约束。

**Step 1｜删豁免取得 RED（真实产物）**

删除 `tools/ui-tree-check.py` R5 的 `dangerous-cmd` 前缀豁免后，对 CI 323 真实产物 `dangerous-cmd.json` 复现 3 处停靠遮挡，exit 1：

```
FAIL: 停靠遮挡 ?[FlowLayoutPanel](Top) vs DangerRuleTable[Table](Fill) 交叠36800px²
FAIL: 停靠遮挡 ?[TableLayoutPanel](Bottom) vs DangerRuleTable[Table](Fill) 交叠129600px²
FAIL: 停靠遮挡 白名单功能已就绪（通过添[Label](Bottom) vs DangerRuleTable[Table](Fill) 交叠6812px²
UI-TREE-CHECK FAIL: 3
```

同一份 dump 上其余规则（兄弟重叠/子越界/零尺寸/行高/触击目标/焦点链/关闭绑定/键盘可达性）**零失败**，证明这 3 条是唯一差异，不是口径噪声。

**Step 2｜改装配顺序取得 GREEN（可证伪预测）**

`fixtures/dock-sim.py` 的模型自证**必须**先通过——它逐值复现了三份真实 dump 的实测 bounds（修复前 dangerous-cmd 4/4 值、修复前 scanner-center 2/2 值、修复后 scanner-center 2/2 值），其中包含修复前那个"Fill 占满客户区 800x520"的破损几何。自证不通过脚本拒绝输出预测。

自证通过后给出的修复后预测（客户区相对）：Fill `(0,46,800,286)`、Top 工具栏 `(0,0,800,46)`、Bottom 白名单 `(0,332,800,162)`、Bottom 状态条 `(0,494,262,26)`——表顶=max(边带顶)=46、表底=白名单顶=332，与三条边停靠**零相交**。

把该预测几何回写成合成 dump 后，`ui-tree-check` 报 `停靠无遮挡=0` 且整检 `ALL OK` exit 0。

> 诚实边界：合成 dump 只证明"该口径下修复后几何可满足"，**不证明 C# 一定对**——真实 CI 的 `dangerous-cmd.json` 才是裁判，实测结果见「验收结果」。

**Step 3｜挂对话框全量门**

`UiSmokeRunner.AssertNoDockOverlap` 由 `private static` 提升为 `internal static`；`DialogsSmoke.Show` 与 `ShowConnectionVariants` 各挂一处（共 2 处），使 13 个对话框用例全部纳入。门放在 `File.WriteAllText(...json)` **之后**：一旦失败，dump 已落盘，仍留产物可诊断（否则门一红就丢证据）。

**Step 4｜本地驱动（`fixtures/check-dock-gate.py`）**

37 项检查，退出码 0：

- S1 规则口径 9 项：**两份真实产物分检**——CI 323 修复前 `prefix` 必 FAIL(exit=1)、R5 命中 3 处、三处交叠面积逐一复核（36800/129600/6812）、除 R5 外无其他规则失败；CI 327 修复后 `fixed` 必 ALL OK(exit=0)、`停靠无遮挡=0`、无任何规则失败
- S2 模型自证 + 预测 8 项：三份真实 dump 复现、四项预测坐标、几何自洽
- S3 修复后过 R5 4 项 + S3b 字节卫生 4 项
- S4 源码落点 11 项：R5 豁免已删 / R3 豁免保留 / R5 规则仍在 / Fill-Fill 豁免仍在 / 门挂 2 处 / 修饰符 internal static / 既有 4 调用点仍在 / 门在 dump 写出之后 / 装配顺序 = `_ruleTable,toolbar,wlPanel,_statusLabel` / 防回归注释在 / 零新依赖 / 夹具目录未被污染

**反向检查（S4）**：`git diff --stat` 仅 4 个受控文件 + 本包目录；`Gdterm.Tests.csproj` 无 FlaUI/WinAppDriver/TestStack。

**实施偏离（已记录，非静默扩范围）**

1. **D2 机制描述更正**：设计初稿把根因写成"边兄弟拿到的剩余区高度为 0，于是挤在同一起点"，模型自证**证伪**了这句——修复前 dump 里两个 Bottom 兄弟（状态条 332..358、白名单 358..520）落位完全正确。真实机制是 `Dock=Fill` **不消耗剩余区**，故 Fill 在高索引时先占满客户区、低索引边兄弟再正确落位而压在它上面。已改写 D2 并附推论。
2. **补 3 个夹具**：设计未列夹具，实现补 `dangerous-cmd-prefix.json`（CI 323 真实产物原样冻结）、`dock-sim.py`（模型）、`check-dock-gate.py`（驱动），以便 CI 产物老化后规则口径仍可回放。
3. **门的位置**：设计只说"挂在 `DialogsSmoke.Show`"，实现进一步定在 dump 写出**之后**，取"失败仍留证据"。
4. **顺带修正 `tools/ui-tree-check.py` 顶部 docstring**：原文写着"交叠面积 > 阈值（默认 **4px²**）"，与实现常量 `OVERLAP_MIN = 16`（4x4）矛盾，且 R1–R5 五条规则一条未写；已改为与实际一致的规则清单。属注释级修正，无行为变更。
5. **可见行为变化**：修复后状态条从白名单区上方落到最底（见 D2 预测表末行）。这是**回归到代码注释声明的设计意图**，非新增设计。

6. **补 S3b 字节卫生断言**：改 `tools/ui-tree-check.py` 的 docstring 时，我用 `encoding="utf-8-sig"` 写回，**静默给它加了一个 BOM**（HEAD 版本无 BOM）——BOM 在 `#!` 之前会使 `./tools/ui-tree-check.py` 直接执行失败，且 `ast.parse` 报 `invalid non-printable character U+FEFF`。已改回 `utf-8` 写回并用 `od -An -c` 核实首 3 字节为 `# ! /`；同时在驱动里加 S3b 三条断言（无 BOM / shebang 在第 0 字节 / 无 CRLF / 语法可解析）防复发，并用"临时注入 BOM → 3 条全红 → 还原后字节一致"验证该断言真有效。

**未纳入本包**：把 `tools/ui-tree-check.py` 接入 AppVeyor 流水线（本包的门在 C# 侧，已真在 CI 内跑）；接入 Python 侧需改 `appveyor.yml`，会因 cache key 变更强制重建 FreeRDP，且存在 cp936 控制台编码风险（R5 消息里的上标 `²`），属另一风险类，单独立包。

## 验收结果

**动态证据：AppVeyor 构建 0.1.327（commit `fc32769`，success，2026-09-25T06:21:01→06:22:55Z，24 个产物）**

| 项 | 结果 |
|---|---|
| 单元测试 | `Passed: 163  Failed: 0` |
| UI 冒烟 | `UI smoke Passed: 6  Failed: 0` |
| 对话框组 | `dialogs-one-fail=0` |
| 停靠断言行 | 18 条 = 契约内 4 + 对话框 14，**逐用例一条不漏、无多余** |
| 树检（19 份 dump，R5 豁免已撤销） | `UI-TREE-CHECK ALL OK` exit 0，逐窗 `停靠无遮挡=0`、`键盘可达性 TabStop=false 数=0` |

**S1｜真实产物 RED→GREEN｜通过**

- RED：删豁免后，`ui-tree-check` 对 CI 323 的 `dangerous-cmd.json` 报 3 处停靠遮挡（36800 / 129600 / 6812 px²），exit 1；同 dump 其余规则零失败
- GREEN：CI 327 的 `dangerous-cmd.json` 实测 `停靠无遮挡=0`；该 dump 已冻结为 `fixtures/dangerous-cmd-fixed.json`
- 一对**真实**产物构成红绿（同一口径在 `prefix` 上红、在 `fixed` 上绿），故这是规则口径在真实数据上的可分性，而非合成夹具自证

**S2｜对话框全量门已挂｜通过**：CI 327 日志中 18 个用例各含一条以自身用例名开头的停靠断言 ok 行 —— `KeePassManager`/`PasswordHealth`/`ScannerCenter`/`KeePassManagerEmpty` + `ai-settings`/`appearance-settings`/`change-masterpwd`/`connection-ssh`/`connection-rdp`/`connection-serial`/`dangerous-cmd`/`keepass-picker`/`keepass-unlock`/`pwd-generator`/`quickcmd-editor`/`setup-wizard`/`sshkey-manager`/`transfer-progress`，无缺失。

**S3｜几何正确｜通过（**逐像素命中预测**）**

`dock-sim.py` 在 CI 之前给出的预测 vs CI 327 实测（窗体根 clientSize 800x520，bounds 为客户端相对坐标）：

| 控件 | dock | 预测 bounds | CI 327 实测 bounds | |
|---|---|---|---|---|
| DangerRuleTable | Fill | (0, 46, 800, 286) | (0, 46, 800, 286) | ✓ |
| 工具栏 FlowLayoutPanel | Top | (0, 0, 800, 46) | (0, 0, 800, 46) | ✓ |
| 白名单面板 TableLayoutPanel | Bottom | (0, 332, 800, 162) | (0, 332, 800, 162) | ✓ |
| 状态条 Label | Bottom | (0, 494, 262, 26) | (0, 494, 262, 26) | ✓ |

4/4 值逐像素一致 → 表顶 = 工具栏底 = 46，表底 = 白名单顶 = 332，三条边停靠与表体零相交。修复前该表是 (0, 0, 800, 520) 占满客户区。

**S4｜反向检查｜通过**：`Gdterm.Tests.csproj` 无新增 NuGet（未引入 FlaUI / WinAppDriver / TestStack）；`UiSmokeRunner` 既有 4 个调用点仍在（定义 + 4 调用 = 5 处）；`tools/ui-tree-check.py` 的 R3 豁免仍含 `dangerous-cmd`；`--phase accept` 通过。

**诚实边界**：C# 侧 `AssertNoDockOverlap` 因本机无 dotnet 无法就地跑红，其"确有牙齿"由同口径的 Python R5 在**修复前真实产物**上报 3 处失败间接支撑（`fixtures/check-dock-gate.py` S1），CI 327 的 18 条 ok 行则证明它**确实被执行**而非被跳过。
