# SFTP 双栏浏览器 — 技术评估（暂不开发）

> 状态：**评估稿**。结论先行：不需要引入任何新 SDK，现有 `Gdterm.Sftp`（SSH.NET 2026.0.0）
> 已覆盖全部协议能力；双栏 UI 建议自绘（WinForms SplitContainer + ListView），参考对象与
> 可借鉴的开源实现见下文。

## 1. 现有资产盘点（已具备 80%）

| 组件 | 状态 | 说明 |
|---|---|---|
| `Gdterm.Sftp.ISftpService` | ✅ 完整 | List/Upload/Download/Delete/Mkdir/Rename 全套，含隧道模式（`ConnectViaTunnelAsync` 走 `ITunnelManager`，堡垒机场景免额外端口） |
| SSH.NET 2026.0.0 | ✅ 已引入 | `SftpClient` 支持递归上传（`UploadDirectory`）、断点续传（`UploadFile` offset）、进度回调（`IProgress`） |
| `SftpBrowserPanel` | ⚠️ 单栏 | 现有 ListView 列表 + 上传/下载按钮，无双栏、无拖拽 |
| `TransferCenterPanel` / `TransferProgressDialog` | ✅ 已有 | 传输中心已有挂载点，双栏产生的队列可直接复用 |
| 凭据/隧道/KeePass 集成 | ✅ 已有 | `SshConnectionInfoFactory` 已对接 `CredentialPayload` |

**缺口只有 UI 层**：双栏布局、本地侧文件系统视图、两侧拖拽、目录递归传输的进度聚合。

## 2. 候选开源参考（按借鉴价值排序）

### 2.1 WindTerm Explorer Pane（设计蓝本，非代码）
- 仓库：kingToolbox/WindTerm（C，不直接可用）
- **设计最值得抄**：双栏 local/remote、拖拽、递归目录传输、远端文件右键编辑
- gdterm 的 QuickBarPanel 已经在抄 WindTerm 的 Quick Bar，视觉语言一致

### 2.2 xSSH-File-Transfer-Client（最接近的技术形态）
- 仓库：xsukax/xSSH-File-Transfer-Client — **PowerShell + WinForms 双栏**
- 用 Posh-SSH（SSH.NET 的 PowerShell 封装），UI 是纯 WinForms 双栏
- 优点：代码量小（单文件 ps1），双栏交互逻辑可直接翻译成 C#
- 许可：MIT

### 2.3 Rebex WinFormClient 样例（WinForms 惯用法教科书）
- https://rebex.net/sample/sftp-winform-client/ — 完整 WinForms SFTP 客户端样例
- 覆盖：异步目录浏览、传输中断、代理、带宽限制（Rebex 自家库**商业许可**，只借鉴 UI 惯用法，不引依赖）

### 2.4 sshmanager / MagicTerm（功能清单参考）
- tomertec/sshmanager（Electron）：双栏 local/remote、拖拽、远端文本编辑、传输进度面板、可调侧栏
- D3FVLT/MagicTerm（commit 78221ab）：SFTP 双面板模式与终端会话分离的设计——**与 gdterm 的标签页架构天然契合**（SFTP 面板作为一种 tab，不是独立窗口）

### 2.5 StorageHub / AutomatizeFTP（架构警示）
- .NET 10 / Avalonia 系，绑定现代运行时——gdterm 锁 .NET Framework 4.6.2（Win7 兼容），**不可直接依赖**，仅证明 CL.Storage 抽象 provider（local/sftp/s3 同一接口）的设计可行

## 3. 推荐方案（到开发时执行）

```
┌─ SftpDualPanePanel : UserControl ─────────────────────────┐
│ ┌─────────── SplitContainer.FixedPanel ───────────────┐   │
│ │ ┌── 本地栏 ──┐ │ splitter │ ┌── 远端栏 ──┐          │   │
│ │ │ 路径回退+框 │           │ 路径框+刷新  │          │   │
│ │ │ ListView   │           │ ListView    │          │   │
│ │ │ (Details)  │           │ (Details)   │          │   │
│ │ └────────────┘           └─────────────┘          │   │
│ └─────────────────────────────────────────────────────┘   │
│  底部：传输队列条（复用 TransferCenterPanel 的行渲染）      │
└───────────────────────────────────────────────────────────┘
```

- **协议层零新增**：两侧统一走 `ISftpService`；本地侧直接 `DirectoryInfo/FileInfo`
- 交互：双栏互拖（`AllowDrop` + `DoDragDrop`）、F5 刷新、F7 新建目录、Del 删除——Norton/Midnight Commander 肌肉记忆
- 递归传输：`SftpClient.UploadDirectory/DownloadDirectory`（SSH.NET 原生），进度聚合进现有 `FileTransferProgress`
- 挂载方式：跟随 MagicTerm 的模式——作为 tab 的一种（`TabContainerControl` 加 SFTP tab 类型），不弹独立窗口；复用现有 `OpenSftpFromActive` 入口换面板
- 工作量预估：UI 骨架 1 天、拖拽+队列 1 天、递归/边界（symlink、权限错误、超时重试）1 天

## 4. 明确不做

- 不引入 Rebex / Chilkat（商业许可）
- 不迁 Avalonia/WPF 重写宿主（与 Win7 目标冲突）
- 不做 S3/FTP 泛化（StorageHub 式 provider 抽象）——需求出现再议
