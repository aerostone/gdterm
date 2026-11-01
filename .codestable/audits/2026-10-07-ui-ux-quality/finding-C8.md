---
doc_type: audit-finding
audit: 2026-10-07-ui-ux-quality
finding_id: "monitoring-C8"
nature: monitoring
severity: P1
confidence: high
suggested_action: cs-refactor
status: **resolved** (2026-10-07 L0 静态门落地)
---

# Finding C8：设计规范"有条款无门"（§7.3 靠人工 grep）

## 速答

`DESIGN-LANGUAGE.md` §7.2 写着"**禁止新增违规**：新代码出现裸 `FromArgb`/裸字体/固定行距
→ 评审直接打回"，§7.3 给的是**三条手写 grep**；`UI-SCALING-CONVENTIONS.md` 尾部同样给了
四条 grep "自查命令"。

这些 grep 的实际执行率取决于评审者是否记得跑 —— 而实测结果表明**没跑**：
"确定"作 Primary 已扩散到 10 处、等宽字体表达式复制到 40 处、24 处裸像素 Padding。
"禁止新增违规"这条规则本身没有执行机制。

## 关键证据

- `docs/DESIGN-LANGUAGE.md:226-237`（§7.3 预提交自检）：3 条 grep，无 CI 挂钩，无基线；
- `docs/UI-SCALING-CONVENTIONS.md:137-152`（自查命令）：4 条 grep，同样手工；
- 违规分布（L0 全量扫描，`--strict`）：R06=10、R07=7、R10=40、R13=24、R09=11、R04=8、R08=3、
  R12=4，合计 107 命中 —— 分散在 30+ 文件，说明不是个别疏漏而是**持续扩散**；
- 反证：R01（裸 `Color.FromArgb`）= **0**、R02（裸字体）= **0**、R05（固定行距）= **0**。
  这三条恰好是 §7.3 grep 覆盖的三条 → **能被机械检查的规范就没被违反**。
  这是最有力的证据：门有效，缺的只是门。

## 影响

- 规范文档与实际代码持续分叉（另见 C9：token 表格已经过期）；
- 评审成本转移到人（每次都要回忆 7 条 grep，并肉眼过滤误报）；
- 新成员无从得知哪些是"存量豁免"、哪些是"新违规"。

## 已落地的修复（本审计同批交付）

| 产物 | 内容 |
|---|---|
| `tools/ui-ux-lint.py` | 13 条规则 R01–R13，零依赖（stdlib），秒级 |
| 基线棘轮 | `.codestable/tools/ui-ux-lint-baseline.txt`（94 键）：**今天绿、新增即红** |
| 覆盖登记 | `.codestable/tools/ui-coverage-registry.txt`（R11 + 下限棘轮） |
| CI 接线 | `appveyor.yml` test_script 段（编码守卫之后） |
| 体系文档 | `docs/UI-UX-VERIFICATION.md`：SSOT 条款 → 门 的完整映射表 |

验证过端到端有效性：注入一个含 5 类违规的探针文件 → 5 条全部命中、exit 1；
删除后 → exit 0。

覆盖关系（每条规则都能追到条款）：

| 规则 | 条款 |
|---|---|
| R01/R02/R05 | DL §1.2 / §2.2 / §2.3，取代 §7.3 的三条手工 grep |
| R03/R04/R12/R13 | SC 规则 1 / 4 / 4c / 4d |
| R06/R07 | DL §4.1 / §9.3 |
| R08 | DL §9.3（非阻断提示用 Message） |
| R09 | DL §4.6（列表空态） |
| R10 | DL §2.1（两套字体分离） |
| R11 | UI-UX-VERIFICATION §5（覆盖登记） |

## 残留（本次未做）

- `R14`（AntdUI.Window 不叠 `FormFontPolicy.Apply`、手写窗体不设 `AutoScaleMode`）可实现但未做
  —— 现在靠注释约束（`SetupWizardForm.cs:77` 有正确示范）；
- 预提交钩子未装（CI 已覆盖，本地想更早拦可 `git config core.hooksPath`，未提供脚本）；
- 基线键不含行号，同文件内完全相同片段会折叠（107 命中 → 94 键），属已知弱化。

## 建议动作

已解决。后续维护：**修完存量后必须 `--write-baseline` 收紧**，这条写进 PR 模板。
