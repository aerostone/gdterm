---
doc_type: change
kind: audit
slug: 2026-11-28-ssh-jump-audit
mode: standard
scope: SSH 跳板独立凭据、配置往返、多跳转发、SFTP 路由及隧道复用
created: 2026-11-28
status: closed
total_findings: 9
---

# SSH 跳板功能审计

## 范围与总评

检查基线：`1fd9b97`，开始时工作区干净。本轮是独立功能审计，没有修改生产代码。

从此前 SSH 私钥入口检查继续向下检查跳板链：新建/编辑连接 → KeePass 凭据解析 → SSH.NET 认证 → 跳板转发 → SSH/SFTP 消费 → 断线与复用。RDP 仅核对同一隧道判断条件及无保存凭据的调用路径，不代表完整 RDP 审计。

确认 **9 项问题：7 项 P1、2 项 P2**。另有一项并发候选尚未复现，单列而不计入发现数。跳板功能目前不能视为完整可用：单跳私钥底层可工作，但配置入口、凭据隔离、两跳生命周期与关联功能存在断点。

风险地图：

- 入口：连接编辑器、连接树编辑保存、活动 SSH 会话打开 SFTP。
- 外部输入与持久化：连接主机/跳板链、KeePass 引用、已有连接 JSON；重点检查编辑往返不丢字段。
- 信任边界：目标和每个跳板分别认证，代理决定网络出口；主机密钥身份应使用真实主机地址。
- 异步与生命周期：建立隧道、共享端点、跳板断开、重试、最后一个标签关闭。
- 检查正确性、安全、性能、维护性与架构边界；不以文件长度或风格作为问题。

## 发现清单

| 编号 | 性质 | 严重度 | 置信度 | 问题 | 主要位置 |
|---|---|---|---|---|---|
| F01 | security | P1 | high | 跳板凭据失效时使用目标密码认证 | `src/Gdterm.Core/Models/CredentialPayload.cs:49` |
| F02 | bug | P1 | high | 启用跳板后忽略 HTTP/SOCKS 代理 | `src/Gdterm.Tunnel/Models/TunnelSession.cs:71` |
| F03 | bug | P1 | high | 第二跳认证后立即关闭承载它的转发 | `src/Gdterm.Tunnel/Models/TunnelSession.cs:79` |
| F04 | bug | P1 | high | 编辑保存丢失跳板凭据引用及后续节点 | `src/Gdterm.UI/Forms/ConnectionDialog.cs:1115` |
| F05 | bug | P1 | high | 跳板缺少独立密码/私钥配置入口 | `src/Gdterm.UI/Forms/ConnectionDialog.cs:370` |
| F06 | bug | P1 | high | SFTP 忽略仅有 JumpChain 的配置；RDP 同类 | `src/Gdterm.UI/Controls/SftpDualPanePanel.cs:181` |
| F07 | bug | P1 | high | 断开的跳板仍被视为活跃并复用 | `src/Gdterm.Tunnel/TunnelManager.cs:32` |
| F08 | bug | P2 | high | 未找到目标凭据时连跳板凭据也不解析 | `src/Gdterm.UI/Services/CredentialResolver.cs:57` |
| F09 | bug | P2 | high | 勾选跳板但地址留空时静默保存为直连 | `src/Gdterm.UI/Forms/ConnectionDialog.cs:1113` |

## 发现详情

### F01：失效的跳板凭据引用会回落到目标密码

- 触发：目标凭据有效，跳板的显式 `CredentialRefId` 指向已删除/读不到的条目；或该条目没有可用私钥且密码为空。
- 证据链：`CredentialResolver.cs:93` 读取失败/缺失后不填映射 → `CredentialPayload.cs:57` 查不到非空密码就返回目标 `Password` → `TunnelSession.cs:41` 将其交给 `SshConnectionInfoFactory` 对跳板认证。
- 影响：本应只用于目标主机的密码交给另一个认证主体；即使该跳板主机密钥可信，也不等于用户授权将目标密码发给它。还会造成错误账号密码尝试。
- 验证：源码链接探针确认失效引用、空密码两条路径；检查真实 SSH.NET 认证对象，确认其中保存的是测试目标密码。仅用合成密码，无用户秘密、无外网发送。
- 安全网：`CredentialPayloadTests.cs:12`、`:22` 反而把失效引用回落当成成功预期。主机密钥校验不能解决凭据归属错误。
- 建议：`cs-issue`；区分“明确选择共用凭据”和“独立引用失效”，后者应停止并提示具体跳板。

### F02：配置了代理，跳板首跳仍直连

- 触发：同一个 SSH 连接同时设置有效形状的 `Proxy` 和 `JumpChain`。
- 证据链：连接编辑器可同时保存两项 → `TunnelManager.cs:220` 只传 hop/payload → `TunnelSession.cs:71` 调用不带代理的工厂重载；对比直连路径 `TunnelSession.cs:117` 会传 `config.Proxy`。
- 影响：必须经代理才能到达的跳板连接失败；若跳板可直达，实际出口与用户配置不符。
- 验证：临时本机 SSH 跳板可连接；将 HTTP 代理设为本机不可用端口后，跳板及转发到目标的命令依然成功。证明代理在该路径未被使用。
- 建议：`cs-issue`；将配置的出口代理应用于第一跳，后续跳板继续经上一跳。

### F03：多跳在第二跳认证成功后失去底层通道

- 触发：链中至少两个跳板。
- 证据链：`TunnelSession.cs:52` 建立第一跳到第二跳的本地转发 → 第二跳经该端口完成 SSH 认证 → `finally` 在 `:85` 停止、移除、释放承载第二跳连接的转发 → `TunnelManager.cs:232` 仍取该第二跳 client 继续使用。
- 影响：两跳及以上无法稳定执行命令/建立最终转发；认证成功不代表链路可用。
- 验证：以同一临时 SSH 服务充当两跳，建立两个独立 SSH 连接；第一跳命令返回 `hop-one`，第二跳 `ConnectHop` 返回时 `IsConnected=False`，执行命令抛出 `SshConnectionException: Client not connected.`。
- 建议：`cs-issue`；将中间转发纳入隧道会话生命周期，使用结束后按反向依赖顺序释放。

### F04：编辑连接会破坏已有跳板配置

- 触发：加载已有 hop 凭据引用或多个跳板的连接，只改名称/备注后保存。
- 证据链：`ConnectionDialog.cs:926` 只载入第一跳的主机、端口、用户名 → `:1115` 重建只包含一个新 hop 的列表，未写 `CredentialRefId` → `ConnectionTreeControl.cs:833` 更新连接存储。
- 影响：第一跳凭据绑定丢失，第二跳及后续节点消失；原本可用的连接被普通编辑破坏。
- 排除：`ConnectionStoreJson.cs:255`、`:343` 有成对读写引用；`ConnectionConfigCopy.cs:47` 会复制每个 hop 及引用。问题发生在表单保存转换，不是 JSON 不支持或复制模型缺字段。
- 建议：`cs-issue`；完整保留未编辑字段和所有节点，并覆盖真实编辑保存往返。

### F05：用户无法给跳板选择独立密码或私钥

- 触发：在连接编辑器中勾选 SSH 跳板，跳板和目标使用不同密码，或跳板只接受私钥。
- 证据：`ConnectionDialog.cs:374` 至 `:379` 只有启用、主机、端口、用户名、保活；没有跳板凭据选择/新建或私钥入口，保存也不产生 hop 引用。
- 影响：前一轮新增的“使用 SSH 私钥…”属于目标凭据，无法配置跳板身份。没有 hop 引用时 `ResolveHopPrivateKey` 明确不继承目标私钥，因此即使两边使用相同密钥也不能靠目标入口完成配置。
- 排除：模型、解析器和 SSH.NET 工厂有每跳私钥支持，临时单跳实连成功；缺口在用户入口，而非底层完全没有私钥认证。
- 建议：`cs-issue`；为每跳接入现有 KeePass 凭据选择/密钥编辑能力，展示认证方式与绑定状态。

### F06：SSH 的跳板配置没有传递到 SFTP 路由

- 触发：在 UI 创建只含 `JumpChain`、`Tunnel=null` 的 SSH 连接，再从活动终端打开 SFTP。
- 证据链：`ConnectionOpenCoordinator.cs:93` 把同一配置交给 SFTP → `SftpDualPanePanel.cs:181` 仅判断 `Tunnel != null` → `SftpService.cs:33` 直接连接目标地址。SSH 终端在 `TerminalControl.cs:1144` 会同时检查 `JumpChain`，所以同一连接的两种行为不同。
- 影响：只能经跳板到达的服务器能开终端，却不能浏览/传输文件；没有沿用用户选择的网络路径。
- 同类已确认位置：`ProtocolTabOpener.cs:324` 的 RDP 也只检查 `Tunnel`。RDP 当前表单隐藏 SSH 跳板区，因此该同类主要影响已导入/已有的仅 `JumpChain` 配置，不能表述为 RDP 表单直接可建该配置。
- 建议：`cs-issue`；统一“需要隧道”的条件并覆盖终端→SFTP的真实入口；RDP 同类一并修正。

### F07：隧道断线后仍显示活跃、重试复用死端点

- 触发：已建立的跳板连接断开，其他标签仍引用相同连接，随后再次获取隧道或重连。
- 证据链：`TunnelSession.cs:149` 设置 `IsActive=true` 后没有订阅跳板断线/错误事件，也不根据 client/forward 状态计算；`SetError` 无实际调用者 → `TunnelManager.cs:36`、`:72`、`:148` 只读该标记。
- 影响：状态错误，重试拿回失效的本地端口而不重建链路。
- 验证：真实单跳及转发命令成功后断开 hop，实际 `IsConnected=False`，`GetStatus.IsActive=True`；再次 `EstablishAsync` 返回原端口，hop 仍断开。
- 已核对安全网：`TabSessionLifecycle.cs:181` 只在最后一个引用消失后关闭隧道。因此不能笼统声称“所有手动重连都失败”；关闭全部使用该连接的标签可以清除旧会话，其他引用还在时问题仍可达。
- 建议：`cs-issue`；让活跃状态反映实际链路健康，失效会话清理后再允许复用/重建。

### F08：目标没有保存凭据时跳板凭据也被跳过

- 触发：KeePass 已解锁、跳板有有效独立引用，目标没有显式、继承或自动匹配条目。例如目标 RDP 预期显示远端登录页，或目标允许空密码认证。
- 证据链：`CredentialResolver.cs:57` 在未找到目标条目时直接返回，`:72` 的跳板解析完全不执行；消费者以空凭据继续，跳板私钥不会被注入。
- 影响：跳板身份被无关的目标凭据是否存在所阻断，无法进入目标后续认证阶段。不是声称本项目已有 SSH 手输密码弹窗。
- 验证：测试库仅保留有效 hop 条目时，解析结果为 null，hop 凭据读取次数为 0。
- 建议：`cs-issue`；分别解析目标和跳板，允许仅含 hop 凭据的运行时载荷，并独立提示各节点认证问题。

### F09：已勾选跳板却允许空地址静默变成直连

- 触发：目标字段有效，勾选“使用 SSH 隧道（跳板机）”，跳板主机留空后保存。
- 证据链：`ConnectionDialog.cs:998` 的校验只覆盖目标地址/端口 → `:1113` 要求跳板地址非空，否则 `:1130` 写入 `JumpChain=null` → 终端无其他 Tunnel 配置时走直连。
- 影响：用户看到启用跳板但保存后实际没有启用，也没有就地报错说明原因。
- 建议：`cs-issue`；启用跳板时校验其必要字段，失败留在表单，避免静默改变连接路径。

## 待确认线索（不计入发现数）

`TunnelManager.cs:48` 把 `EstablishCoreAsync` 的副作用放在 `ConcurrentDictionary.GetOrAdd` 的 value factory 内。该 factory 在竞争时允许执行多次；存在 A/B 同时启动建立任务、只缓存其中一个 task、另一个继续写 `_sessions` 的交错路径。

本机 8 个线程同时请求同一连接，本次只观察到创建 1 个 SSH client，**没有动态复现重复建立/资源泄漏**。保留为需要受控调度验证的并发线索，不把该次结果报成失败或已确认缺陷。

## 已核查并排除

1. “底层不支持 hop 私钥”：不成立。映射齐全时解析器和工厂传递私钥/口令，实际单跳密钥认证与转发命令成功。
2. “JSON/复制层不保存跳板凭据”：不成立。读写和深拷贝支持全部 hop 及引用，F04 是编辑器覆盖造成。
3. “跳板没有接主机密钥校验”：不成立。`TunnelSession.cs:73` 按 hop 原始地址调用 guard；`SftpService.cs:65` 按目标原始地址校验；`Program.cs:288` 装配 known_hosts 和提示器。此项为接线核查，没有在本轮重跑完整主机密钥交互测试。

## Verification Evidence

- 产物：[本机探针输出](evidence/probe-output.txt)。临时探针工程仍在 `/tmp/gdterm-jump-audit-jk0qye3o/`，其私钥已清除，重跑实连需重新创建临时密钥与 sshd。
- 运行：`/home/aerostone/dotnet/dotnet run --project /tmp/gdterm-jump-audit-jk0qye3o/Probe.csproj --configuration Release --verbosity quiet`。最终退出码 **0**；其中多跳失败是被捕获、记录的产品缺陷，退出码不代表产品通过全部功能验收。
- 首版临时探针有适配 API 的编译错误，扩展并发探针时曾读取已释放 client 导致退出 134；均修正探针后重跑，最终输出取最后一次运行，不计为生产代码缺陷。
- 凭据探针链接当前生产 `CredentialResolver`、模型、认证工厂源码，KeePass 边界用合成条目替身；确认 7 条正常/异常数据流事实。
- 网络探针链接当前生产 `TunnelSession`、`TunnelManager`、`SshConnectionInfoFactory`、`SshKeepAlive`，使用已安装 SSH.NET **2026.0.0 net8.0** 与本机 OpenSSH，临时生成私钥，仅监听 `127.0.0.1`。
- 网络结果：单跳及目标转发命令成功；两跳断开、代理被忽略、断开后活跃标记不变和死端点复用均重现。并发探针未复现候选。
- 边界替身：仅用简单 L10n/日志/串口类型适配；网络探针的主机密钥工厂直接信任隔离的临时服务，故这些结果**不证明**生产 known_hosts、提示 UI 的动态行为。未连接用户服务器、未读取用户密钥或真实密码库。
- 清理：已停止临时 sshd、删除临时私钥与公钥；未改系统 sshd 配置、用户 authorized_keys 或项目业务配置。
- 运行限制：这是 Linux 上链接同一生产源码的 .NET 8 诊断探针，非 Windows/.NET Framework 4.6.2 全应用验收。本轮没有运行原生 WinForms、完整项目单测、重新推送或触发 CI。
- 测试安全网：检查 `CredentialPayloadTests`、`SshKeyAuthenticationTests`，并检索测试目录中的 JumpChain/Tunnel/hop 覆盖；已有模型复制测试不等于编辑器往返、多跳实连或 SFTP 路由覆盖。
- 范围：**32 个源码文件**读取相关实现/调用片段；大型 UI 文件按链路切片，不宣称逐行审完。另对测试目录做定向检索；没有审计 SFTP 文件操作、完整 RDP、终端显示或全仓架构。
- 主 agent 回读清单：
  - UI（10）：`Forms/ConnectionDialog.cs`；`Controls/ConnectionTreeControl.cs`、`TerminalControl.cs`、`SftpDualPanePanel.cs`；`Services/CredentialResolver.cs`、`ProtocolTabOpener.cs`、`ConnectionOpenCoordinator.cs`、`TabReconnectService.cs`、`TabSessionLifecycle.cs`；`Program.cs`。
  - Core（8）：`Models/ConnectionConfig.cs`、`CredentialPayload.cs`、`JumpHop.cs`、`JumpChainConfig.cs`、`TunnelConfig.cs`、`ProxyConfig.cs`、`ConnectionConfigCopy.cs`、`SerialConfig.cs`。
  - Connections（2）：`ConnectionStoreJson.cs`、`IFolderCredentialStore.cs`。
  - KeePass（3）：`IKeePassService.cs`、`Models/KeePassEntry.cs`、`Models/PasswordHealthReport.cs`。
  - Tunnel（6）：`Models/TunnelSession.cs`、`TunnelManager.cs`、`SshConnectionInfoFactory.cs`、`SshHostKeyGuard.cs`、`SshKeepAlive.cs`、`Exceptions/TunnelException.cs`。
  - SFTP（1）：`SftpService.cs`；Tests（2）：`Core/CredentialPayloadTests.cs`、`Security/SshKeyAuthenticationTests.cs`。
- 架构对照：`.codestable/attention.md`；`.codestable/architecture/ARCHITECTURE.md` 中跳板、凭据及同连接共享隧道部分。没有更新 current architecture。
- Agent：0 个子 agent，主 agent 完成核查。
- 候选复核：13 项 → 排除 3 项、保留待确认 1 项、去重后正式发现 9 项。

## 下一步

优先通过 `cs-issue` 修复凭据隔离、保存丢配置与多跳生命周期，再补齐每跳凭据入口、代理/SFTP 路由和断线重建；一起补对应失败路径及 Windows 实际表单往返验证。F08/F09 纳入同一链路回归。并发候选先做受控复现，不按已确认缺陷直接扩大修改。
