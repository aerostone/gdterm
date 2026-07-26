---
doc_type: audit-finding
audit: 2026-07-26-post-split-quality
finding_id: "maint-01"
nature: maintainability
severity: P1
confidence: high
suggested_action: cs-issue
status: partial
---

# Finding 12：无测试 + 构建 sln 残缺（审计时点）

## 速答

审计扫描时：仓库无测试项目；`gdterm.sln` 仅 Core+Tools 有 Build.0；Terminal GUID 含非法 `DEFG`；`CommandHistoryStore` 未进 Logging.csproj。

## 关键证据

- 维护度 agent 报告 + 主 agent 读 sln/csproj  

## 同批次缓解（partial）

已在质量打磨中：

- 重写 sln，13 项目均含 Build.0  
- Terminal GUID → `DEFA`  
- Logging 补 CommandHistoryStore Compile  
- 新增 `Gdterm.Tests` 零 NuGet runner + pack-release 脚本  

仍 open：测试覆盖面极窄；Windows MSBuild 未实跑验证。

## 建议动作

继续扩测试；Windows 上跑 `tools/pack-release.ps1`。
