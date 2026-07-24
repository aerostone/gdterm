---
doc_type: feature-design
feature: 2026-07-24-logging-engine
requirement: null
roadmap: gdterm
roadmap_item: logging-engine
status: approved
summary: 实现日志引擎——结构化审计日志（连接/命令/密码使用/AI交互）、日志轮转（按大小+按天）、审计查询接口
tags: [logging, audit, rotation]
---

# logging-engine — 日志引擎

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 审计日志（Audit Log） | 结构化的操作记录（谁在何时对什么做了什么） | 不同于调试日志 |
| 日志轮转（Log Rotation） | 按大小/天数自动切换日志文件 | 防止单文件过大 |
| 审计条目（AuditEntry） | 一条审计记录 | 不同于 log4net 的日志事件 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Logging` 类库，实现 `IAuditLogger` 接口（roadmap 4.6 完全一致），提供结构化审计日志记录、日志轮转、审计查询能力。

**为谁**：所有模块（连接/命令/密码使用/AI交互/安全事件记录）

**成功标准**：
- 可记录连接、凭据使用、命令、AI交互、安全事件
- 日志按大小轮转（默认 10MB/文件，最多 10 个文件）
- 可按时间范围、连接Id、事件类型查询审计日志
- 日志文件为 JSON 行格式，可读可解析

**明确不做**：
- 远程日志服务器
- 日志加密
- 实时日志流推送

### 关键决策

1. **日志格式**：JSON Lines（每行一个 JSON 对象），便于解析和查询
2. **轮转策略**：按大小轮转（默认 10MB/文件），保留最近 N 个文件（默认 10）
3. **查询方式**：内存中加载最近 N 个文件，按条件过滤
4. **线程安全**：所有写入操作加锁

### 前置依赖

- `core-models`（done ✅）：无直接依赖，但为所有模块提供日志能力

## 2. 名词与编排

### 2.1 名词层

```csharp
// Gdterm.Logging.IAuditLogger — 公开接口（roadmap 4.6 完全一致）
public interface IAuditLogger
{
    void LogConnection(string connectionId, string host, string protocol, ConnectionAction action);
    void LogCredentialUse(string connectionId, string credentialRefId, CredentialAction action);
    void LogCommand(string connectionId, string command);
    void LogAiInteraction(string connectionId, string prompt, string response);
    void LogSecurityEvent(SecurityEvent evt, string detail);
    IList<AuditEntry> Query(AuditQuery query, int limit = 100);
}

// Gdterm.Logging.Models — 枚举和数据类
public enum ConnectionAction { Open, Close, Error, Timeout }
public enum CredentialAction { AutoFill, ManualCopy, Unlock, Lock }
public enum SecurityEvent { IdleLock, Unlock, WeakPasswordRejected, BruteForceDetected }
public class AuditEntry { Timestamp, ConnectionId, EventType, Detail }
public class AuditQuery { From, To, ConnectionId, EventType }
public class LogRotationConfig { MaxFileSizeBytes, MaxFileCount, RetentionDays }
```

### 2.2 编排层

```
模块记录日志 → IAuditLogger.LogConnection/LogCommand/...
  → 构建 AuditEntry → 序列化为 JSON → 追加到当前日志文件
  → 检查文件大小 → 超过阈值时轮转（创建新文件，删除最旧文件）

审计查询 → IAuditLogger.Query(query, limit)
  → 加载最近 N 个日志文件 → 逐行解析 JSON → 按条件过滤 → 返回结果
```

**流程级约束**：
- 所有写入操作加锁（线程安全）
- 日志文件命名为 `audit-YYYYMMDD-HHmmss.jsonl`
- 轮转时删除最旧文件（按文件名排序）
- 查询结果按时间倒序返回（最新在前）

### 2.3 挂载点清单

本 feature 不引入新挂入点。IAuditLogger 由所有模块通过 DI 消费。

### 2.4 推进策略

```
1. 创建 Gdterm.Logging 项目，引用 Core
   退出信号：项目编译通过
2. 实现枚举和数据类（ConnectionAction、CredentialAction、SecurityEvent、AuditEntry、AuditQuery、LogRotationConfig）
   退出信号：编译通过
3. 实现 IAuditLogger 接口
   退出信号：编译通过，接口与 roadmap 4.6 完全一致
4. 实现 AuditLogger 核心逻辑（日志写入、轮转、查询）
   退出信号：单元测试覆盖 LogConnection + Query + 轮转触发
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Logging/` 为新建目录

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | LogConnection(id, host, ssh, Open) | 审计日志写入 |
| 2 | Query(from, to) | 返回时间范围内条目 |
| 3 | Query(connectionId) | 返回指定连接条目 |
| 4 | 文件超过 10MB | 自动轮转到新文件 |
| 5 | 超过 MaxFileCount | 最旧文件被删除 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不做远程日志服务器 | 代码中无网络传输 |
| 2 | 不做日志加密 | 代码中无加密逻辑 |
| 3 | 不做实时日志流推送 | 代码中无推送/SignalR |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.Logging 作为日志基础设施
- **接口**：IAuditLogger → 所有模块的审计记录入口
