---
doc_type: feature-design
feature: 2026-07-24-sftp-browser
requirement: null
roadmap: gdterm
roadmap_item: sftp-browser
status: approved
summary: 实现 SFTP 文件浏览器——SSH.NET SFTP 操作（连接、列目录、上传、下载、删除、创建目录、重命名）
tags: [sftp, file-browser, ssh]
---

# sftp-browser — SFTP 文件浏览器

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| SFTP 会话（SftpSession） | 一个 SFTP 连接的生命周期 | 不同于 TerminalSession（交互式 shell） |
| 远程文件信息（SftpFileInfo） | 远程文件/目录的元数据 | 不同于 System.IO.FileInfo |
| 传输进度（FileTransferProgress） | 文件上传/下载的进度信息 | 用于 UI 进度条绑定 |

## 1. 决策与约束

### 需求摘要

**做什么**：创建 `Gdterm.Sftp` 类库，实现 `ISftpService` 接口（roadmap 4.5 完全一致），基于 SSH.NET SftpClient 实现文件操作。

**为谁**：UI 模块（文件浏览器面板）、AI 模块（文件操作能力）

**成功标准**：
- 可通过 SSH 连接后浏览远程目录
- 可上传/下载文件（带进度报告）
- 可删除文件/目录、创建目录、重命名

**明确不做**：
- 断点续传
- 批量队列管理
- 远程文件编辑

### 关键决策

1. **SFTP 客户端**：使用 SSH.NET 内置的 SftpClient，纯托管代码
2. **跳板模式**：通过 ITunnelManager 建立端口转发后连接 localhost:forwarded_port
3. **进度报告**：通过 IProgress<FileTransferProgress> 报告进度
4. **排序规则**：目录优先，同类型按名称排序

### 前置依赖

- `core-models`（done ✅）：ConnectionConfig、CredentialPayload
- `ssh-tunnel`（done ✅）：ITunnelManager（跳板模式）

## 2. 名词与编排

### 2.1 名词层

```csharp
// Gdterm.Sftp.ISftpService — 公开接口（roadmap 4.5 完全一致）
public interface ISftpService : IDisposable
{
    Task ConnectAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct);
    Task<IList<SftpFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct);
    Task UploadAsync(string localPath, string remotePath, IProgress<FileTransferProgress> progress, CancellationToken ct);
    Task DownloadAsync(string remotePath, string localPath, IProgress<FileTransferProgress> progress, CancellationToken ct);
    Task DeleteAsync(string remotePath, bool recursive, CancellationToken ct);
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct);
    void Disconnect();
    bool IsConnected { get; }
}

// Gdterm.Sftp.Models.SftpFileInfo
public class SftpFileInfo
{
    public string Name { get; set; }
    public string FullPath { get; set; }
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string Permissions { get; set; }
    public string Owner { get; set; }
    public string Group { get; set; }
}

// Gdterm.Sftp.Models.FileTransferProgress
public class FileTransferProgress
{
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
    public TimeSpan Elapsed { get; set; }
}
```

### 2.2 编排层

```
用户请求列目录 → UI 调用 ISftpService.ListDirectoryAsync(remotePath)
  → SftpClient.ListDirectory(remotePath) → 返回文件列表（排序：目录优先 + 名称）
  → UI 渲染文件列表

用户请求上传 → UI 调用 ISftpService.UploadAsync(localPath, remotePath, progress)
  → SftpClient.UploadFile(localPath, remotePath, progress callback)
  → IProgress<FileTransferProgress> 报告进度 → UI 更新进度条

用户请求下载 → 类似上传的反向路径

用户请求删除 → UI 调用 ISftpService.DeleteAsync(remotePath, recursive)
  → SftpClient.Delete(remotePath) 或递归删除

用户请求重命名 → UI 调用 ISftpService.RenameAsync(oldPath, newPath)
  → SftpClient.RenameFile(oldPath, newPath)
```

**流程级约束**：
- ListDirectoryAsync 按类型（目录优先）+ 名称排序
- 上传/下载通过 IProgress<FileTransferProgress> 报告进度
- 不支持断点续传——中断后重新开始整个传输
- 跳板模式下 SFTP 通过 ITunnelManager.EstablishAsync 建立端口转发后连接 localhost:forwarded_port

### 2.3 挂载点清单

本 feature 不引入新挂入点。ISftpService 由 UI 模块通过 DI 消费。

### 2.4 推进策略

```
1. 创建 Gdterm.Sftp 项目，引用 SSH.NET + Core + Tunnel
   退出信号：项目编译通过
2. 实现 SftpFileInfo 和 FileTransferProgress 模型
   退出信号：编译通过
3. 实现 ISftpService 接口
   退出信号：编译通过，接口与 roadmap 4.5 完全一致
4. 实现 SftpService 核心操作（连接、列目录、上传、下载）
   退出信号：单元测试覆盖 ConnectAsync + ListDirectoryAsync + UploadAsync + DownloadAsync
5. 实现 SftpService 辅助操作（删除、创建目录、重命名、断开）
   退出信号：单元测试覆盖 DeleteAsync + CreateDirectoryAsync + RenameAsync + Disconnect
```

### 2.5 结构健康度与微重构

##### 评估

- 文件级 — 无现有文件需改动（全新项目）
- 目录级 — `Gdterm.Sftp/` 为新建目录

##### 结论：不做

全新项目，无现有代码需重构。

## 3. 验收契约

### 关键场景清单

| # | 场景 | 期望结果 |
|---|---|---|
| 1 | ListDirectoryAsync("/") | 返回根目录文件列表，目录在前 |
| 2 | UploadAsync + progress | 进度回调触发，Percentage 从 0 到 100 |
| 3 | DownloadAsync + progress | 进度回调触发 |
| 4 | DeleteAsync(file, false) | 文件被删除 |
| 5 | DeleteAsync(dir, true) | 目录递归删除 |
| 6 | CreateDirectoryAsync | 目录创建成功 |
| 7 | RenameAsync | 文件/目录重命名成功 |
| 8 | Disconnect 后操作 | 抛出 InvalidOperationException |

### 明确不做的反向核对项

| # | 不做 | 反向核对 |
|---|---|---|
| 1 | 不支持断点续传 | 代码中无续传逻辑 |
| 2 | 不做批量队列管理 | 代码中无队列逻辑 |
| 3 | 不做远程文件编辑 | 代码中无编辑逻辑 |

## 4. 与项目级架构文档的关系

**acceptance 阶段需提炼回 architecture：**
- **模块**：Gdterm.Sftp 作为文件传输子系统
- **接口**：ISftpService → 跨模块消费契约（UI、AI）
