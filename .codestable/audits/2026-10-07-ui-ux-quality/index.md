---
doc_type: audit-index
audit: 2026-10-07-ui-ux-quality
mode: standard
scope: Gdterm.UI 全部 52 个 Form/Control + 现有 UI 验证工具链（tools/*.py + Gdterm.Tests/Ui）
date: 2026-10-07
status: fixed
---

# 审计：UI/UX 质量与 UI 验证体系（2026-10-07）

## 范围与方法

- **代码**：`src/Gdterm.UI`（96 个 `.cs`，52 个 Form/Control）
- **SSOT**：`docs/DESIGN-LANGUAGE.md` v1.3、`docs/UI-SCALING-CONVENTIONS.md`
- **验证链**：`tools/ui-tree-check.py`（L1）、`tools/ui-check.py`（视觉）、
  `src/Gdterm.Tests/Ui/*`（dump 产出）、`appveyor.yml`（接线）
- **方法**：读 SSOT → 逐条找"谁保证它" → 用可数事实（grep 计数、token 对比度计算、
  覆盖登记）验证；不靠主观印象。**只发现不修复**（唯一例外见 C8：新增了 L0 静态门）。

## 总评

| 维度 | 分 | 依据 |
|---|---|---|
| 设计一致性 | 5.5 | token 化完成度高（裸色 0），但 ① 语义层被绕过：10 处"确定"作 Primary、7 处 Type+BackColor 双写；② **SSOT 表格与代码是两套色板**（C9） |
| 无障碍 | 3.0 | `AccessibleName/Description` 全仓 **0**；Tab 关钮命中区 16×18；交互描边 1.31:1 / 2.05:1（文字色全达标） |
| 反馈与状态 | 4.5 | 有正确样板（FilePane `_busy`+状态文案）但未推广；**全仓 0 处 WaitCursor**；11 处列表无空态 |
| 主题 | 5.5 | 三套暗色齐备，但运行期切换只 Refresh 不重取 token（233 处构造时快照） |
| 字体/缩放 | 6.0 | 裸字体 0、固定行距 0、`DpiScale` 覆盖好；但等宽字体 40 处逐点复制且跟随 UI 字号（放大即裁切） |
| **验证体系** | **5.0** | L1 结构门有效（R1–R5）；但**面板覆盖 1/36**、无文字适配断言、无 DPI×字号矩阵、R2 空转、视觉门未进 CI；规范条款无门 → 本次补 L0 |

**综合 ≈ 5.0/10**（权重：验证体系×2 + 无障碍×1.5 + 反馈×1.5 + 一致性×1 + 主题×1 + 字体×1）。

一句话：**外壳做得比内部严**。窗体能进 dump 的都有断言，而用户 90% 时间面对的是侧栏
面板 —— 那些面板既不在 dump 里，也没有任何规范门；连设计文档里的色值都和代码对不上。

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 文件 |
|---|------|--------|--------|------|------|
| C1 | ux | P1 | high | 11 处列表型 UI 无空态引导 | [finding-C1.md](finding-C1.md) |
| C2 | ux | P1 | high | 无"进行中"反馈与防重入统一语义（WaitCursor=0） | [finding-C2.md](finding-C2.md) |
| C3 | maintainability | P1 | high | 等宽字体 40 处逐点复制且跟随 UI 字号 | [finding-C3.md](finding-C3.md) |
| C4 | ux-consistency | P2 | high | "确定"作 Primary + Type/BackColor 双写：语义层失效 | [finding-C4.md](finding-C4.md) |
| C5 | a11y | P1 | high | 无障碍名称全仓 0 + 自绘命中区过小 + 描边对比度不足 | [finding-C5.md](finding-C5.md) |
| C6 | theming | P2 | medium | 运行期主题切换不重取 token（构造时快照） | [finding-C6.md](finding-C6.md) |
| C7 | monitoring | P1 | high | 验证体系四处硬缺口（面板 0 覆盖 / 无文字适配 / 无矩阵 / R2 空转） | [finding-C7.md](finding-C7.md) |
| C8 | monitoring | P1 | high | 设计规范无门（§7.3 靠人工 grep）→ **本次已闭合** | [finding-C8.md](finding-C8.md) |
| C9 | ssot-drift | P1 | high | DL §1 token 表是 v1 色板、代码是 v2；BorderStrong 零引用 | [finding-C9.md](finding-C9.md) |

## 本次交付（非审计修复，但同批落地）

| 产物 | 说明 |
|---|---|
| `docs/UI-UX-VERIFICATION.md` | L0–L4 五层验证体系 + SSOT 条款 → 门 映射表 + 收敛计划 + 人工验收清单 |
| `tools/ui-ux-lint.py` | L0 静态门，13 条规则（R01–R13），零依赖，基线棘轮 |
| `.codestable/tools/ui-ux-lint-baseline.txt` | 存量 94 键基线（今天绿、新增即红） |
| `.codestable/tools/ui-coverage-registry.txt` | 52 个 Form/Control 覆盖登记（dump=16 / manual=5 / none=31）+ 下限棘轮 |
| `appveyor.yml` | L0 门接入 CI（编码守卫之后） |

## 修复状态（2026-10-07 当日全部执行）

| # | 状态 | 落地点 |
|---|---|---|
| C1 | ✅ 3/11 + 组件 | `EmptyStateView`（标题/说明/动作钮/可见性语义）；命令历史（区分空库/筛选空）、端口转发、通知中心已挂；空库可见性被 SidePanels dump 断言锁住；余 8 处进 R09 棘轮（基线 8） |
| C2 | ✅ 组件 + 2 接入点 | `BusyScope`（按钮禁用 + WaitCursor + 状态行 + finally 恢复 + Nudge 重入提醒）；扫描中心（_running 重入 Nudge + 插件扫描 BusyScope）、转发启动已接入；余下长操作按样板铺 |
| C3 | ✅ 归零 | `FormFontPolicy.MonoFont(size)` 收口 40 处；L0 R10 存量 40→0；等宽字体跟随 UI 字号阶梯（字号放大不再逐点裁切） |
| C4 | ✅ 归零 | 10 处"确定"Primary → 具体动词（保存/应用/添加/连接…）；Type+BackColor 双写清除；L0 R06/R07 存量归零 |
| C5 | ✅ 主体 | `UiA11y.AutoName` 自动命名 9 类控件（Form/TabPage/列表/文本/进度等，构造期挂）；Tab 关闭钮命中区 16×18 → ≥24（`GetCloseHitRect` 扩张，命中测试与 `IHitRegionProvider` dump 同口径，R1 检查）；`BorderStrong` 提至 `#66757F`（3.57:1@Surface）用于交互描边；图标钮逐一补名 + L1 accessibleName 断言 = 遗留（见 §6） |
| C6 | ✅ 止血 | `GdtermColorTable.ThemeChanged` 事件 + `ThemeColorRemap.RefreshAll`（旧色→新色映射重贴）；MainForm 外壳/连接树/菜单/状态栏/标签栏已接入；完整"重建外壳/token 订阅"进 P2 |
| C7 | ✅ 四处全闭 | ① dump 增 `textSingleW/textWrapH/wrap`（真实 GDI 度量）+ L1 R6（默认 warn，`--textfit-strict` 可翻硬门）；② 6 个侧栏进 dump（SidePanels 用例，360×480 宿主窗，单面板隔离失败）；③ 自绘命中区 `hitRegions` 入 dump + 检查器按 ≥24 逻辑 px 判；④ R2 空转规则已删（永不失败的门比没有更坏），焦点链由 R4 承接 |
| C8 | ✅（审计时已闭合） | L0 静态门 `ui-ux-lint.py` 13 规则 + 基线棘轮，CI 接入 |
| C9 | ✅ | `gen-tokens.py`：SSOT=`GdtermColorTable.cs`，生成 `docs/tokens.json` + DESIGN-LANGUAGE §1 表；`--check` 进 CI（JSON 漂移/文档表漂移均验证实测红）；过期 XML 注释色值已改 |

**遗留（非本次范围，已入 `docs/UI-UX-VERIFICATION.md` §7）**：L2 矩阵门（DPI×字号×主题，当前最大缺口）、
L3 视觉基线 diff 进 CI、R04/R13 存量债（8/24）、余 8 处空态、BusyScope 铺开、主题完整重建。

## 下一步建议

### P0 本迭代

1. **C7-1** L1 增 `textFit` 字段与断言 —— 历史最高频 UI bug 类型（文字裁切）当前无门。
2. **C7-2** 6 个侧栏面板进 dump，覆盖度 16→22（登记表下限同步抬升）。
3. **C3** `FormFontPolicy.MonoFont()` 收口 40 处等宽字体。

### P1 下一迭代

- **C1** 抽出 `EmptyState` 组件，铺 11 处；**C2** 抽出 `BusyScope`（禁用 + 光标 + 可取消）；
- **C5** 图标按钮补 `AccessibleName`，Tab 关闭钮命中区 16×18 → 24×24；
- **C6** 主题切换改"重建外壳"或 token 订阅。

### P2 清理

- **C9-1** token 表改为由 `GdtermColorTable` 生成（手抄必然再漂）：Surface2/BorderStrong/Tint
  进文档，删掉过期的 XML 注释色值；
- **C4** "确定" → 具体动词（"保存"/"应用"/"添加"），去掉 Type+BackColor 双写；
- **C9-2 / C5-3** 接上并把 `BorderStrong` 提到 ≥3:1（`#66757F` = 3.57@Surface / 4.00@Background），
  用于输入框与按钮描边；
- `ui-tree-check.py` R2：补真实 Tab 序或删掉该规则（不留永不失败的门）。

---

**证据来源**：SSOT 逐条核对 + `tools/ui-ux-lint.py` 全量扫描（修复前 107 命中 / 94 键，修复后 47 / 46）+
`tools/ui-tree-check.py` 规则实现通读 + `Gdterm.Tests/Ui/*` 覆盖率统计 +
WCAG 对比度计算（#11161A/#1A2127/#35414A/#E8EEF2/#9AA7B1 等）。

---

## 2026-11-01 复核（as-desktop-ui-verify 技能，证据契约逐条验证）

按技能的「判定器可达性与量纲校准」「消除空过」「最小闭环」三条对本仓验证体系做
反向审计——即检查"检查本身"。发现并修复 8 项（前 6 项为验证体系自身缺陷，非业务 UI 问题）：

| # | 发现 | 证据 | 修复 |
|---|---|---|---|
| 1 | **R4/R6 是死规则**：dump 布尔是字符串 `"true"/"false"`，`tabStop is False` 永不成立、`autoSize="false"` truthy 恒跳过 | 96dpi 合成面板：R6 受检=0（应 3） | 统一 `sbool()`；修复后 R6 受检=3、R4 对 tabStop=false 命中 |
| 2 | **命中区量纲倒置**：`24×96/dpi`（高 dpi 阈值反而变小）。app.manifest 是 PerMonitorV2 → 几何为设备 px | 同一 16 逻辑 px 区：96dpi 合成树 FAIL、192dpi 合成树假过 | 改 `24×dpi/96`；R1 同口径；96/192 正反例校准（合成例只证算法） |
| 3 | **mainform 分支跳过新规则**：命中区/R6 的真实目标（主窗标签 ×）受检=0 | 分支 `continue` 静态核对 + 96dpi mainform 合成树 | hitRegions/R6 移至分支前通用段；mainform 专属规则不变 |
| 4 | **空树假绿**：0 控件文件 ALL OK exit 0 | 空树合成文件 | 空控件树 → 该文件 FAIL |
| 5 | **`--textfit-strict` 放第一位崩溃**：argv[1] 无条件当目录 | 实跑 traceback | flag/位置参数分离 |
| 6 | **CI 失败不留证**：smoke 非零即 throw，上传与树检被跳过 | appveyor.yml 静态核对 | 先上传 dump/截图，再统一宣判 |
| 7 | **空态"找到才检查"**：EmptyStateView 被删后静默过 | DumpSidePanel 静态核对 | `expectEmpty` 参数：command-history/port-forward 先断言存在 |
| 8 | **smoke 无 tab 场景**：MainFormSmoke 不建标签 → hitRegions 恒空 | MainFormSmoke 静态核对 | `OpenLocalTerminal()` + 断言 `GetHitRegions()≥1`（本地 PTY，不连主机） |

**证据层级（如实标注）**：以上判定侧修复全部经**合成样本**反例验证（只证明判定算法）；
C# 侧改动（smoke/命中区）本机无 Windows 运行时，**目标运行时结果未验证**，由 AppVeyor
下次构建验证——若 R4 激活后首跑红，每个命中都是真实键盘可达性缺陷或需登记豁免。
R6 仍是 **warn-only 告警**（非阻断门）；L2 DPI×字号×主题矩阵、L3 视觉基线 diff、
余 8 处空态、SftpPermissionForm 仍为开放缺口。

## 2026-11-01 复审：as-desktop-ui-verify 对验证体系本身的反向审计（8 项缺陷全修）

按 `as-desktop-ui-verify` 技能的证据契约（判定器可达性/量纲校准/消除空过/最小闭环）反向审计
本仓验证体系，技能 winforms.md 末段 gdterm 反例逐条命中：

| # | 缺陷 | 修复 |
|---|---|---|
| 1 | R4/R6 死规则：dump 布尔是字符串 "true"/"false"，truthy 判断把假当真（tabStop is False 永不成立） | 检查器统一 sbool() |
| 2 | L0 检查器自身反例未验证 | 10 个合成反例（96/192dpi）全命中 |
| 3 | textfit 字符串布尔 | 同上 |
| 4 | CI 冒烟只覆盖 18 对话框，4 个新侧栏+mainform 无硬门 | SidePanels 场景 6 面板 + mainform 标签/hitRegions 断言 |
| 5 | 空态"找到才检查"反模式（无引导则静默绿） | expectEmpty 存在性断言（command-history/port-forward） |
| 6 | CI 失败不留证（失败构建 artifacts 为空） | 先上传 dump/截图再 throw |
| 7 | 参数解析：flag 置首即崩（sys.argv[1] 未过滤） | 过滤后取 argv |
| 8 | 检查器在 AppVeyor 镜像 Python 3.11 兼容性 | 本地 3.9 实测全绿（3.9 语法子集） |

## 2026-11-01 CI 闭环：0.1.345 → 0.1.350（匿名 API 全程可观测）

- 0.1.345 红：5 个审计新增 .cs 未进旧式 csproj Compile 清单（dcaece5 修）
- 0.1.346 红：GetHitRegions 用 IsVisible（Control 无此成员）→ Visible（67b55d3 修）
- 0.1.347 红：Gdterm.Tests 3 类编译错误（a8f8945 修，csc 本地编译验证后提交）
- 0.1.348 红：SidePanels 2/6——**空态首开永不显示**（EmptyStateView 初值 Visible=false，
  SetEmpty 只在手动刷新调用，构造/工厂路径无人触发首刷 = C1 原症状残留）。
  两面板构造尾部补初始刷新（e0c3250）。同轮确认：L0 全 OK、tree-check 23 文件 ALL OK（R4 复活后真数据通过）
- 0.1.349 红：tree-check 2 FAIL——EmptyStateView 的 action 按钮（打开终端/新建规则）
  AutoSize 且未设 Padding → R-贴边 规则命中。像素级验证（CI 产物截图）：AntdUI 内绘留 7-9px
  视觉内边距，但组件未按设计语言显式声明 → 定性为组件缺陷（非误报），EmptyStateView._action
  补 Padding(12,4,12,4)（本条修复随 0.1.350）
- 经验：AppVeyor 匿名 API 对 build job log/artifacts 开放（302 跟随重定向），失败构建的
  留证产物可下载 → "首跑红逐条定性"全程无日志通道也能闭环
