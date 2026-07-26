---
doc_type: audit-index
audit: 2026-07-26-post-split-quality
mode: standard-plus
scope: post-finding-10 UI Services + critical session/security/build paths
date: 2026-07-26
status: open
---

# 审计：post-split 质量轮（2026-07-26）

## 范围

finding-10 拆分完成后的关键路径（约 40 文件）：

- `Gdterm.UI`：MainForm / TabContainer / TerminalControl / Program / Services/* / Diagnostics
- `Gdterm.Terminal`：session / renderer / health / auto-log
- `Gdterm.Tunnel` / `Gdterm.Security` / `Gdterm.KeePass` / `Gdterm.AI` / `Gdterm.Logging` / Tools 扫描
- 对照：`ARCHITECTURE.md`、`attention.md` 分层约束
- 并行 5 维 Explore agent + 主 agent 交叉验证

**本轮 cs-audit 只发现不定修。** 同提交批次另含质量打磨（DiagLog / 测试骨架 / 打包脚本 / sln 修复），不计入审计“修发现”。

## 总评

上一轮 wiring-runtime 后预估 **7.5–8.0**。本轮在拆分收口后继续挖运行时/安全/构建问题，综合约 **6.0–6.5/10**（新 P0 拉低安全与 bug）。架构分层文档对齐度明显好于上一轮。

## 维度评分（1–10）

| 维度 | 分 | 说明 |
|------|----|------|
| Bug 隐患 | 4.5 | 2×P0（async 关签竞态、危险命令 fail-open）+ 分屏/跳板/DoEvents |
| 安全 | 3.5 | 主密码单次 SHA256；锁屏不擦会话凭据；gdk1/明文 ApiKey 回退 |
| 性能 | 5.0 | 重连 UI 阻塞 20s；暂停标签仍 BeginInvoke；逐字符 Span 分配 |
| 可维护性 | 6.5 | 无自动化测试（本轮已补骨架，审计时点仍记债）；MainForm 21 参组合根 |
| 架构偏离 | 7.5 | 分层约束大体遵守；UI 仍下转 TerminalSession.UnderlyingClient |

**综合（权重 安全×2 + bug×1.5 + 架构×1.5 + 性能×1 + 维护×1）≈ 6.2/10**

## 发现清单

| # | 性质 | 严重度 | 置信度 | 标题 | 文件 |
|---|------|--------|--------|------|------|
| 1 | bug | P0 | high | TerminalControl.Connect async void 关签竞态 | [finding-01.md](finding-01.md) |
| 2 | bug | P0 | high | 危险命令检测 fail-open（异常即放行） | [finding-02.md](finding-02.md) |
| 3 | security | P0 | high | 主密码仅 SHA256(salt‖pwd)，无慢 KDF | [finding-03.md](finding-03.md) |
| 4 | security | P0 | high | 锁屏不清除 TabSessionState.Credential | [finding-04.md](finding-04.md) |
| 5 | bug | P1 | high | 分屏后 GetActiveTerminalControl 恒 null | [finding-05.md](finding-05.md) |
| 6 | bug | P1 | high | 跳板 hop.CredentialRefId 被忽略 | [finding-06.md](finding-06.md) |
| 7 | performance | P0 | high | 重连 Wait 阻塞 UI + DoEvents 重入 | [finding-07.md](finding-07.md) |
| 8 | performance | P1 | high | 暂停标签仍 UI 线程泵输出 | [finding-08.md](finding-08.md) |
| 9 | security | P1 | high | ApiKey gdk1/明文回退仍可写读 | [finding-09.md](finding-09.md) |
| 10 | security | P1 | medium | SecretScan 详情明文展示匹配内容 | [finding-10.md](finding-10.md) |
| 11 | arch-drift | P1 | high | UI 下转 TerminalSession.UnderlyingClient | [finding-11.md](finding-11.md) |
| 12 | maintainability | P1 | high | 无自动化测试 / 构建 sln 残缺（审计时点） | [finding-12.md](finding-12.md) |

## 按维度分布

- bug: 01, 02, 05, 06
- security: 03, 04, 09, 10
- performance: 07, 08
- arch-drift: 11
- maintainability: 12

## Dead Code 清单（不占维度上限）

| 文件:行 | 名称 | 类型 |
|---------|------|------|
| TabContainerControl | GetActiveSshClient | 未用别名 |
| ActiveSessionBridge | GetTerminalControl / GetTerminalSession | 无引用 |
| TabContainerControl ctor | aiService 参数 | 丢弃 `_ = aiService` |

## 同批次已做的质量打磨（非 audit 修复）

| 项 | 说明 |
|----|------|
| `DiagLog` | 关签/关闭/Program dispose 空 catch → crash.jsonl `swallowed:*` |
| `gdterm.sln` | 13 项目均补 Debug/Release Build.0 |
| Terminal GUID | `DEFG` → `DEFA`（合法 hex） |
| Logging.csproj | 补 `CommandHistoryStore.cs` Compile |
| `Gdterm.Tests` | 零 NuGet runner：DefaultPorts / LogSanitizer / ConnectionStoreJson |
| `tools/pack-release.ps1` + `docs/BUILD.md` | 绿色版打包与构建说明 |

## 下一步建议

### P0 立刻开 `cs-issue`

1. **finding-01**：Connect 后 await 检查 `_disposed`，dispose 时取消/丢弃会话  
2. **finding-02**：危险命令检测 fail-closed（异常 → 拒绝或二次确认）  
3. **finding-03**：主密码改 PBKDF2/慢哈希（注意便携迁移）  
4. **finding-04**：Lock 时清空各 tab Credential + 停自动重连用缓存密文  
5. **finding-07**：重连等待改 async/Timer，去掉 UI 线程 Sleep+DoEvents  

### P1 本迭代

- finding-05 分屏后活动终端解析  
- finding-06 跳板凭据  
- finding-08 暂停标签丢弃/合并输出  
- finding-09/10 ApiKey / SecretScan UI  
- finding-11 UnderlyingClient 上移到 Terminal 门面  

### 架构治理

- 组合根只留 Program；去掉 MainForm/TabContainer 的 `?? new XxxFactory()`  
- 扩展 `Gdterm.Tests` 覆盖 CredentialResolver / LogSanitizer 全 CLI 规则 / DangerousCommandDetector  

---

**证据来源**：5× Explore agent（bug/security/performance/maintainability/arch）+ 主 agent 读 Program/sln/csproj/Lock/TabReconnect/TabSplit 交叉验证。
