---
doc_type: feature-design
feature: 2026-07-24-connections-storage
requirement: null
roadmap: gdterm
roadmap_item: connections-storage
status: approved
summary: 连接配置 JSON 存储、树形分组管理、连接 CRUD
tags: [connections, storage, json, crud]
---

# connections-storage — 连接配置 JSON 存储

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| connections.json | 连接配置持久化文件，JSON 格式，存储 ConnectionConfig 列表 | 不同于 config.json（全局配置） |
| GroupPath | 树形分组路径，如 "Jump/Web"，用 `/` 分隔层级 | 存储在 ConnectionConfig.GroupPath |
| ConnectionStore | 本 feature 新增的服务类，封装 connections.json 的读写 | 不是数据库，是简单 JSON 文件 |

## 1. 决策与约束

### 需求摘要

**做什么**：实现 `Gdterm.Connections` 模块，提供连接配置的 JSON 文件持久化、树形分组管理、连接 CRUD 操作。
**为谁**：UI 层（树形连接面板）、Terminal/RDP/SFTP 模块（读取连接配置）。
**成功标准**：可以增删改查连接配置，connections.json 文件随绿色版走，重启不丢数据。
**明确不做**：
- 密码存储——密码只存 CredentialRefId，由 KeePass 模块管理
- 连接历史/最近使用——后期 feature
- 连接导入导出——后期 feature
- 连接测试/连通性检查——归各协议模块

### 复杂度档位

走 .NET 类库默认档位，无偏离。

### 关键决策

1. **存储格式**：单个 `connections.json` 文件，数组结构。
   - 替代方案：每个连接一个 JSON 文件。被拒：文件管理复杂，不便于树形分组展示。
2. **分组实现**：分组信息存在 ConnectionConfig.GroupPath 字段，不在文件系统建目录。
   - 理由：简化文件管理，GroupPath 用 `/` 分隔层级即可。
3. **并发安全**：文件锁（FileShare.ReadWrite）保护读写，WinForms 单线程 UI 为主。
   - 理由：v1 不考虑多实例同时写入，文件锁足够。
4. **ID 生成**：使用 `Guid.NewGuid().ToString()`，不依赖外部库。

### 前置依赖

- `core-models`（已 done）

## 2. 名词与编排

### 2.1 名词层

**现状**：Core 模块已定义 ConnectionConfig 等数据模型，无持久化。

**变化**：新增以下类型。

```csharp
// Gdterm.Connections.IConnectionStore — 连接存储接口
public interface IConnectionStore
{
    /// <summary>
    /// 加载所有连接配置（从 connections.json）
    /// </summary>
    IList<ConnectionConfig> LoadAll();

    /// <summary>
    /// 保存所有连接配置（写入 connections.json）
    /// </summary>
    void SaveAll(IList<ConnectionConfig> connections);

    /// <summary>
    /// 添加连接，自动生成 Id，持久化
    /// </summary>
    ConnectionConfig Add(ConnectionConfig connection);

    /// <summary>
    /// 更新连接（按 Id 匹配），持久化
    /// </summary>
    /// <exception cref="KeyNotFoundException">Id 不存在时抛出</exception>
    void Update(ConnectionConfig connection);

    /// <summary>
    /// 删除连接（按 Id），持久化
    /// </summary>
    /// <exception cref="KeyNotFoundException">Id 不存在时抛出</exception>
    void Delete(string connectionId);

    /// <summary>
    /// 按 Id 查询单个连接
    /// </summary>
    ConnectionConfig GetById(string connectionId);

    /// <summary>
    /// 获取树形分组结构（按 GroupPath 分层）
    /// </summary>
    IList<GroupNode> GetGroupTree();
}

// Gdterm.Connections.GroupNode — 树形分组节点
public class GroupNode
{
    public string Name { get; set; }           // 当前层级名称（如 "Web"）
    public string FullPath { get; set; }       // 完整路径（如 "Jump/Web"）
    public IList<GroupNode> Children { get; set; }  // 子分组
    public IList<ConnectionConfig> Connections { get; set; }  // 本组连接
}
```

```csharp
// Gdterm.Connections.ConnectionStoreJson — IConnectionStore 的 JSON 文件实现
public class ConnectionStoreJson : IConnectionStore
{
    private readonly string _filePath;  // connections.json 路径
    
    public ConnectionStoreJson(string filePath);
    // 所有方法内部：读文件 → 操作内存对象 → 写文件
}
```

### 2.2 编排层

**现状**：无持久化逻辑。

**变化**：新增 CRUD 流程。

主流程（以 Add 为例）：

```
调用方 → Add(connection)
  → 生成 Guid Id（若调用方未设）
  → 读取 connections.json（LoadAll）
  → 追加到列表
  → 写回 connections.json（SaveAll）
  → 返回带 Id 的 ConnectionConfig
```

流程级约束：
- **幂等性**：Add 会自动生成新 Id，即使重复调用也是新增条目
- **错误语义**：文件不存在时自动创建空文件；文件损坏时抛出 IOException
- **顺序约束**：CRUD 操作内部串行（读-改-写），UI 单线程调用

### 2.3 挂载点清单

| 挂载位置 | 动作 |
|---|---|
| connections.json 文件路径（exe 同目录） | 新增 |
| config.json → `connections.filePath`（可选自定义路径） | 新增 |

### 2.4 推进策略

```
1. 创建 Gdterm.Connections 项目，添加 IConnectionStore 接口
   退出信号：编译通过
2. 实现 ConnectionStoreJson（LoadAll/SaveAll 基础读写）
   退出信号：手动调用可读写 connections.json
3. 实现 Add/Update/Delete/GetById
   退出信号：CRUD 操作可验证
4. 实现 GetGroupTree（树形分组解析）
   退出信号：输入带 GroupPath 的连接列表返回正确树结构
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新模块）
- 目录级 — `src/Gdterm.Connections/` 为新建目录，预计 2-3 个文件，不构成摊平

##### 结论：不做

全新模块，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | 调用 Add 添加新连接 | 连接写入 connections.json，返回带 Id 的对象 |
| 2 | 调用 LoadAll 加载 | 返回 connections.json 中所有连接 |
| 3 | 调用 Update 更新已有连接 | connections.json 中对应条目更新 |
| 4 | 调用 Delete 删除连接 | connections.json 中对应条目移除 |
| 5 | 调用 GetById 查询 | 返回匹配的连接配置 |
| 6 | connections.json 不存在时调用 LoadAll | 返回空列表，自动创建文件 |
| 7 | 多个连接有不同 GroupPath | GetGroupTree 返回正确的树形结构 |
| 8 | Update/Delete 传入不存在的 Id | 抛出 KeyNotFoundException |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不存储密码 | connections.json 中无 Password 字段 |
| 2 | 不做数据库 | 无 SQL/SQLite 依赖，仅 JSON 文件 |
| 3 | 不做连接历史 | 无 LastUsed/History 字段 |

## 4. 与项目级架构文档的关系

acceptance 阶段需提炼：
- **模块**：Gdterm.Connections 作为数据持久层 → 在 ARCHITECTURE.md 记录
- **接口**：IConnectionStore → 在模块接口描述中记录
