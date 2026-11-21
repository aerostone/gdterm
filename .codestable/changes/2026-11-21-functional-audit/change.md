---
doc_type: change
kind: audit
slug: 2026-11-21-functional-audit
mode: standard
scope: 全部用户可见页面与主菜单功能的静态功能性审查
created: 2026-11-21
status: closed
total_findings: 0
---

# 功能完整性审计报告

## 范围与总评

审查范围是用户可见功能入口、页面契约和页面/动作登记关系。契约登记共 70 项：46 个页面和 24 个菜单动作；入口树包含 6 个一级菜单、57 个二级入口。功能矩阵检查通过，未发现未登记入口、页面契约缺口或必需能力格缺口；本次静态审查未确认可复现的功能缺陷。

结论只覆盖静态证据。当前环境没有 .NET SDK、MSBuild 或 Mono，不能运行 Windows 单测、WinForms 页面交互冒烟或真实 SSH/RDP/SFTP/AI 联调，因此不能据此宣称运行时功能已全部验收。

## 发现清单

未发现满足证据门槛的问题。

## 发现详情

无。

## 下一步

建议在 Windows 桌面环境执行项目 CI 中的 Release 构建、`Gdterm.Tests.exe`、`--ui-smoke` 和 `--ui-matrix`，再按 `docs/UI-UX-VERIFICATION.md` 完成真实会话流程验收。运行时验证前，本报告不作为发布验收结论。

## Verification Evidence

- 范围：页面与功能登记 70 项（46 页面、24 动作）；UI 规范门扫描 127 个 C# 文件。页面级完整性通过入口树、功能契约、测试目录和 UI 覆盖登记进行核对；服务实现为定向静态抽查，未逐行审阅整个 105k LOC C# 索引。
- 维度：功能入口可达性、页面登记/契约完整性、必需能力格、失败态与 UI 覆盖证据；同时运行仓库已有 i18n、安全输入闸门、项目文件清单及 UI 规范静态检查。
- 高风险区域：连接/配置持久化、密码库与 OTP、危险命令输入、扫描插件、文件传输及终端会话集成；本次仅做入口/契约和相关测试存在性核对，没有执行外部连接或真实凭据流程。
- agent：0。
- 候选：0 个可确认缺陷候选 / 0 个验证淘汰 / 0 个 finding。
- 主 agent 回读：`.codestable/attention.md`、`docs/FEATURE-MAP.md`、`docs/feature-contracts.json`、`docs/UI-UX-VERIFICATION.md`、`tools/feature-map.py`、`src/Gdterm.Tests/Ui/UiPageCatalog.cs`、`src/Gdterm.Tests/Ui/DialogsSmoke.cs`、`src/Gdterm.Tests/Program.cs`、`appveyor.yml`。
- 架构对照：未读取；本次是页面功能完整性审查，不评估架构偏离。
- 动态验证限制：`dotnet`、`msbuild`、`mono`、`xbuild` 均不可用；Windows Forms 和外部服务交互测试未运行。
- 静态验证：`python3 tools/feature-map.py --check` 通过（46 页面缺口 0、未登记入口 0）；`python3 tools/check-csproj-files.py` 通过；`python3 tools/check-sec-gate.py` 通过；`python3 tools/check-i18n.py` 通过；`python3 tools/ui-ux-lint.py` 通过（127 个 C# 文件命中 0 条）。
- 额外边界：UI 目录文档标注 terminal、AI chat、SFTP 双栏依赖真实会话或流式服务，不在轻量页面构造矩阵里；它们没有在本环境做动态验证。
