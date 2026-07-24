---
doc_type: feature-design
feature: 2026-07-24-keepass-integration
requirement: null
roadmap: gdterm
roadmap_item: keepass-integration
status: approved
summary: 实现 KeePass 密码库管理——.kdbx 文件读写、密码条目 CRUD、连接关联映射、自动填充凭据、密码强度校验
tags: [keepass, password, security]
---

# keepass-integration — KeePass 密码库管理

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 密码库（KeePass Database） | .kdbx 文件，存储加密的密码条目 | 不同于连接配置文件 |
| 条目（Entry） | 一个密码记录（用户名+密码+URL+备注） | 不同于 ConnectionConfig |
| 主密码（Master Password） | 解锁 .kdbx 的密码 | 不同于连接密码 |
| 凭据引用（CredentialRefId） | ConnectionConfig 中引用 KeePass 条目的 UUID | 用于关联映射 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.KeePass` 类库，实现 `IKeePassService` 接口（roadmap 4.3 完全一致），基于 KeePassLib 实现 .kdbx 文件读写。

**为谁**：Terminal/RDP 模块（自动填充凭据）、UI 模块（密码库管理界面）

**成功标准**：
- 可通过主密码解锁 .kdbx 文件
- 可根据 CredentialRefId 获取凭据用于自动填充
- 可创建/更新密码条目（自动校验密码强度）
- 可列出所有条目（不含密码明文）

**明确不做**：
- 密码生成器
- 密码库文件的创建/删除
- 多密码库同时打开

### 关键决策

1. **KeePassLib 依赖**：使用 KeePassLib（KeePass 2.x 官方库），.NET 4.6.2 兼容
2. **密码强度校验**：最小 12 字符，含大写+小写+数字+特殊字符，不含常见弱密码
3. **内存安全**：Lock() 时清除内存中的明文密码
4. **密码库路径**：由调用方传入，本模块不管理文件路径

### 前置依赖

- `core-models`（done ✅）：ConnectionConfig、CredentialPayload

## 2. 名词与编排

### 2.1 名词层

```csharp
// Gdterm.KeePass.IKeePassService — 公开接口（roadmap 4.3 完全一致）
public interface IKeePassService : IDisposable
{
    Task<bool> UnlockAsync(string masterPassword);
    void Lock();
    bool IsUnlocked { get; }
    CredentialPayload GetCredential(string credentialRefId);
    KeePassEntry CreateEntry(KeePassEntry entry);
    void UpdateEntry(KeePassEntry entry);
    IList<KeePassEntrySummary> ListEntries();
}

// Gdterm.KeePass.Models.KeePassEntry — 完整条目（含密码）
public class KeePassEntry { Id, Title, Username, Password, Url, Notes, GroupPath }

// Gdterm.KeePass.Models.KeePassEntrySummary — 条目摘要（不含密码）
public class KeePassEntrySummary { Id, Title, Username, GroupPath }

// Gdterm.KeePass.Models.WeakPasswordException — 密码强度不足异常
public class WeakPasswordException : Exception { Violations }

// Gdterm.KeePass.PasswordStrengthValidator — 密码强度校验器
internal class PasswordStrengthValidator
{
    ValidationResult Validate(string password);
    bool IsCommonWeakPassword(string password);
}
```

### 2.2 编排层

```
用户解锁密码库 → UI 调用 IKeePassService.UnlockAsync(masterPassword)
  → KeePassLib 读取 .kdbx 文件 → 解密 → 返回 true/false

连接时自动填充 → Terminal/RDP 调用 IKeePassService.GetCredential(credentialRefId)
  → 查找匹配条目 → 返回 CredentialPayload(username, password)

创建条目 → UI 调用 IKeePassService.CreateEntry(entry)
  → PasswordStrengthValidator.Validate(entry.Password)
  → 强度不足 → 抛出 WeakPasswordException
  → 强度通过 → 写入 .kdbx → 返回 KeePassEntry

锁定 → UI 调用 IKeePassService.Lock()
  → 清除内存中的明文密码 → IsUnlocked = false
```

**流程级约束**：
- 密码库未解锁时调用 GetCredential 抛出 InvalidOperationException
- CreateEntry/UpdateEntry 内部强制校验密码强度
- ListEntries 不返回密码明文
- Lock() 清除内存中的明文密码

### 2.3 挂载点清单

本 feature 不引入新挂入点。IKeePassService 由 Terminal/RDP/UI 模块通过 DI 消费。

### 2.4 推进策略

```
1. 创建 Gdterm.KeePass 项目，引用 KeePassLib + Core
   退出信号：项目编译通过
2. 实现 KeePassEntry、KeePassEntrySummary、WeakPasswordException 模型
   退出信号：编译通过
3. 实现 PasswordStrengthValidator（密码强度校验逻辑）
   退出信号：编译通过，覆盖校验规则
4. 实现 IKeePassService 接口
   退出信号：编译通过，接口与 roadmap 4.3 完全一致
5. 实现 KeePassService 核心操作（Unlock/Lock/GetCredential）
   退出信号：单元测试覆盖 Unlock + GetCredential + Lock
6. 实现 KeePassService CRUD 操作（Create/Update/List + 密码强度校验集成）
   退出信号：单元测试覆盖 CreateEntry + UpdateEntry + ListEntries
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.KeePass/` 为新建目录

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | UnlockAsync(正确密码) | IsUnlocked=true |
| 2 | UnlockAsync(错误密码) | IsUnlocked=false |
| 3 | GetCredential(有效Id) | 返回 CredentialPayload |
| 4 | GetCredential(未解锁) | 抛出 InvalidOperationException |
| 5 | CreateEntry(弱密码) | 抛出 WeakPasswordException |
| 6 | CreateEntry(强密码) | 条目创建成功 |
| 7 | ListEntries | 不含密码明文 |
| 8 | Lock | IsUnlocked=false，内存清除 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不做密码生成器 | 代码中无生成逻辑 |
| 2 | 不做密码库创建/删除 | 代码中无文件创建逻辑 |
| 3 | 不做多密码库同时打开 | 代码中无多库管理 |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.KeePass 作为密码管理子系统
- **接口**：IKeePassService → 跨模块消费契约（Terminal、RDP、UI）
