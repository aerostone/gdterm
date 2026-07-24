---
doc_type: feature-design
feature: 2026-07-24-security-idle-lock
requirement: null
roadmap: gdterm
roadmap_item: security-idle-lock
status: approved
summary: 实现安全锁定——闲时无操作检测（可配置超时）、自动锁定密码库和活动会话、主密码管理（设置/修改/验证）、密码强度策略引擎
tags: [security, idle, lock, password]
---

# security-idle-lock — 闲时锁定

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 闲时锁定（Idle Lock） | 用户无操作超过阈值后自动锁定 | 不同于手动锁定 |
| 主密码（Master Password） | 解锁应用/密码库的密码 | 不同于连接密码 |
| 锁定状态（Locked State） | 应用被锁定，需输入主密码才能继续操作 | UI 应禁用交互 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Security` 类库，实现 `ISecurityManager` 接口（roadmap 4.8 完全一致），提供闲时锁定、手动锁定/解锁、主密码管理、密码强度校验。

**为谁**：UI 模块（锁定状态管理）、KeePass 模块（锁定联动）

**成功标准**：
- 用户无操作超过阈值后自动触发锁定
- 手动锁定/解锁功能正常
- 主密码可设置/修改（需校验强度）
- 锁定状态变化通过事件通知 UI

**明确不做**：
- 密码加密存储（由 KeePass 管理）
- 多因素认证
- 生物识别

### 关键决策

1. **计时器机制**：使用 System.Timers.Timer，每秒检查一次空闲时间
2. **主密码存储**：哈希存储（SHA256 + salt），不存明文
3. **密码强度校验**：复用 KeePass 的 PasswordStrengthValidator 逻辑
4. **锁定联动**：锁定时触发事件，UI 订阅后调用 KeePass.Lock()

### 前置依赖

- `core-models`（done ✅）
- `keepass-integration`（done ✅）：密码强度校验逻辑复用

## 2. 名词与编排

### 2.1 名词层

```csharp
// Gdterm.Security.ISecurityManager — 公开接口（roadmap 4.8 完全一致）
public interface ISecurityManager : IDisposable
{
    void ResetIdleTimer();
    void LockNow();
    bool Unlock(string masterPassword);
    void SetMasterPassword(string oldPassword, string newPassword);
    event EventHandler<LockStateChangedEventArgs> LockStateChanged;
    bool IsLocked { get; }
    TimeSpan IdleTimeout { get; set; }
}

// Gdterm.Security.Models.LockStateChangedEventArgs
public class LockStateChangedEventArgs : EventArgs
{
    public bool IsLocked { get; set; }
    public string Reason { get; set; }  // "idle" / "manual" / "unlock"
}

// Gdterm.Security.Models.MasterPasswordConfig
public class MasterPasswordConfig
{
    public string PasswordHash { get; set; }   // SHA256 哈希
    public string Salt { get; set; }           // 随机 salt
    public DateTime? LastChanged { get; set; }
}
```

### 2.2 编排层

```
用户操作 → UI 调用 ISecurityManager.ResetIdleTimer()
  → 重置最后操作时间

空闲超时 → Timer 触发
  → ISecurityManager.LockNow()
  → 触发 LockStateChanged 事件（IsLocked=true, Reason="idle"）
  → UI 订阅后调用 KeePass.Lock() + 禁用界面

手动锁定 → UI 调用 ISecurityManager.LockNow()
  → 同上

解锁 → UI 调用 ISecurityManager.Unlock(masterPassword)
  → 验证密码哈希 → 成功则 IsLocked=false
  → 触发 LockStateChanged 事件（IsLocked=false, Reason="unlock"）

设置主密码 → UI 调用 ISecurityManager.SetMasterPassword(old, new)
  → 验证旧密码 → 校验新密码强度 → 生成新 salt + 哈希 → 保存
```

**流程级约束**：
- ResetIdleTimer 在 UI 的 MouseMove/KeyDown/Click 事件中调用
- 超时触发时自动调用 LockNow
- 主密码强度校验复用 PasswordStrengthValidator 逻辑
- 主密码哈希存储，不存明文

### 2.3 挂载点清单

本 feature 不引入新挂入点。ISecurityManager 由 UI 模块通过 DI 消费。

### 2.4 推进策略

```
1. 创建 Gdterm.Security 项目，引用 Core
   退出信号：项目编译通过
2. 实现数据类（LockStateChangedEventArgs、MasterPasswordConfig）
   退出信号：编译通过
3. 实现 ISecurityManager 接口
   退出信号：编译通过，接口与 roadmap 4.8 完全一致
4. 实现 SecurityManager 核心逻辑（计时器、锁定/解锁、主密码管理）
   退出信号：单元测试覆盖 ResetIdleTimer + LockNow + Unlock + SetMasterPassword
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Security/` 为新建目录

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | ResetIdleTimer 后超时 | 自动触发 LockNow |
| 2 | LockNow | IsLocked=true，事件触发 |
| 3 | Unlock(正确密码) | IsLocked=false，事件触发 |
| 4 | Unlock(错误密码) | IsLocked 保持 true |
| 5 | SetMasterPassword(弱密码) | 抛出 WeakPasswordException |
| 6 | SetMasterPassword(强密码) | 密码更新成功 |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不做密码加密存储 | 哈希存储，非加密 |
| 2 | 不做多因素认证 | 代码中无 MFA 逻辑 |
| 3 | 不做生物识别 | 代码中无生物识别 |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.Security 作为安全子系统
- **接口**：ISecurityManager → UI 模块消费契约
