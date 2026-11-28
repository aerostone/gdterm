---
doc_type: change
kind: audit
slug: 2026-11-28-sftp-audit
mode: standard
scope: SFTP 双栏文件浏览、管理、传输、预览、权限及会话生命周期
created: 2026-11-28
status: closed
total_findings: 13
---

# SFTP 功能审计

## 范围与总评

基线 `f00e88d`。本轮沿“SSH → 文件菜单 → SFTP 浏览器 → 双栏操作”检查，确认 **7 项 P1、6 项 P2**。覆盖传输、软链接和权限修改存在数据安全问题，不能判定 SFTP 功能完整可用。未修改业务代码，未提交或推送。

风险边界：远端文件名/链接进入本地文件系统；UI 值进入 SFTP 权限命令；普通传输进入自动续传；连接与后台任务跨越标签页生命周期。架构、维护性也做了范围内检查，没有把文件长、未使用工具类或纯风格作为独立缺陷。

## 发现清单

| ID | 性质 | 级别 | 置信度 | 问题 | 代表位置 |
|---|---|---|---|---|---|
| S01 | security | P1 | high | 权限重复转换，640 实际变成 416 | `src/Gdterm.Sftp/SftpService.cs:357` |
| S02 | bug | P1 | high | 删除/重命名软链接实际操作目标文件 | `src/Gdterm.Sftp/SftpService.cs:322` |
| S03 | bug | P1 | high | 普通传输盲目续传，造成跳过更新或内容拼接 | `src/Gdterm.Sftp/SftpService.cs:194` |
| S04 | bug | P1 | high | 权限窗口未回填，直接应用会写入 000 | `src/Gdterm.UI/Forms/SftpPermissionForm.cs:152` |
| S05 | security | P1 | medium | 远端文件名未约束 Windows 下载边界 | `src/Gdterm.UI/Controls/SftpDualPanePanel.cs:262` |
| S06 | bug | P1 | high | SFTP 建连同步阻塞 UI 线程 | `src/Gdterm.Sftp/SftpService.cs:43` |
| S07 | bug | P1 | medium | 关闭后连接可复活，后续 Dispose 不再清理 | `src/Gdterm.Sftp/SftpService.cs:378` |
| S08 | bug | P2 | high | 递归删除保留所选根目录 | `src/Gdterm.Sftp/SftpService.cs:313` |
| S09 | bug | P2 | high | 实际传输未接入进度、取消控件 | `src/Gdterm.UI/Controls/SftpDualPanePanel.cs:255` |
| S10 | bug | P2 | high | 同名文件并发预览共享临时文件 | `src/Gdterm.Sftp/SftpEnhancements.cs:25` |
| S11 | bug | P2 | medium | 拖拽数据无来源身份，可能读取错误服务端 | `src/Gdterm.UI/Controls/FilePaneControl.cs:135` |
| S12 | bug | P2 | high | 断线仍显示已连接，传输请求静默丢弃 | `src/Gdterm.UI/Controls/SftpDualPanePanel.cs:228` |
| S13 | performance | P2 | high | 预览 100 行先下载完整文件 | `src/Gdterm.Sftp/SftpEnhancements.cs:28` |

## 发现详情

### S01：权限重复转换，可能给其他用户开放写权限

- 触发：远端文件“属性”中输入 `640` 后应用。
- 证据链：`FilePaneControl.cs:496` 传十进制数字形式的八进制权限；`SftpService.cs:354`–`358` 转成位值 `416` 再传 SSH.NET。实际依赖的 `ChangePermissions(short)` 接受数字形式 `640`，会自行转换。
- 实测：`640 → 416 (r----xrw-)`，原本不应有权限的其他用户得到读写权限；`644 → 420`、`777 → 511`；`600/700/755` 抛 `ArgumentOutOfRangeException`。对照直接调用依赖的 `ChangePermissions(path, 640)` 得到正确 `640`。
- 安全网：现有权限单测验证两个纯函数的往返，没有执行服务 API；UI 校验 0–7 不能阻止这次重复转换。
- 影响：错误授权、文件不可执行或应用失败。high，真实 OpenSSH 实测。
- 建议：`cs-issue`，统一 UI→服务→SSH.NET 的权限口径，并逐项回读真实权限。

### S02：软链接删除/重命名伤及目标文件

- 触发：列表中选择指向普通文件的软链接，点击删除或重命名。
- 证据链：`SftpService.cs:83`–`93` 丢弃链接类型；`FilePaneModel.cs:178`–`185` 继续只保留目录/文件二分；删除到 `SftpService.cs:308/322`，重命名到 `:344`。当前 SSH.NET 路径处理会解析链接目标，调用方未保护“操作链接本身”的语义。
- 实测：删除 `file-link → file-target` 后，`file-target` 不存在，`file-link` 目录项仍存在；重建目标后重命名链接，目标被移走，旧链接仍在，新名称对应普通文件。目录链接删除也报错。
- 受影响：单文件删除/重命名；递归删除中的 `SftpService.cs:408` 使用同一删除 API，也有越过所选目录删除链接目标的风险。
- 影响：用户确认删除链接，实际丢失原文件，目标可以位于所选目录外。high，单项删除和重命名均已真实复现；未额外执行越界目标删除。
- 建议：`cs-issue`，保留链接类型，核对依赖的 canonical-path 行为，保证删除、重命名和递归遍历不意外追随目标。

### S03：自动续传不能判定同名旧文件是否属于本次传输

- 触发：上传/下载到已有同名文件，或重复传整个目录。
- 证据链：`SftpDualPanePanel.cs:255/262/338/354` 无条件调用 Resume；`SftpService.cs:194/253` 等长即跳过，`:196/255` 较短即追加；面板 `:264` 不读取返回的“跳过”结果，仍计成功。
- 实测：等长 `NEW` 覆盖 `OLD`，仍是 `OLD`；源 `abcdef`、目标旧内容 `XYZ`，得到 `XYZdef`。上传和下载两方向都复现。
- 安全网：接口注释也把等长视为最新，没有内容身份/前缀校验或传输记录；UI 无覆盖/续传冲突选择。
- 影响：旧内容被当新内容、文件损坏且显示成功。high。
- 建议：`cs-issue`，区分覆盖与经过验证的续传，正确报告已传输/跳过/失败。

### S04：权限窗口的默认值没有加载实际权限

- 触发：选一个 `644` 文件打开属性，不修改，点击“应用”。
- 证据链：`FilePaneControl.cs:490`–`496` 提供原权限，`SftpPermissionForm.cs:22` 收到参数，但构造器结束前未调用 `ApplyRwx`。`OctalMode` 保持 `0`；点击应用 `:129`–`141` 返回 OK，服务收到 `0`。
- 证据：旧 Windows CI run `46113892779` 的 [截图](evidence/sftp-permission.png) 和 [控件树](evidence/sftp-permission.json) 显示九个复选框全部未选、数字 `0`；输入为 `rw-r--r--`。该窗体及冒烟调用代码从 `24c8ffa` 到当前 HEAD 没有差异。真实 SFTP `ChmodAsync(path, 0)` 回读 `---------`。
- 附带保护缺陷：即便补回初始化调用，当前 `ApplyRwx` 在 `_syncing=true` 内调用 `SyncFromChecks`，后者 `:187` 立即返回；需同时修正初始化联动，不能只补一行调用。
- 影响：未作修改却移除所有权限，目录可能因此无法进入。high；本轮未在 Windows 点击应用到真实服务器，结论由截图、提交调用链和服务实测组合确认。
- 建议：`cs-issue`，修复初始化和返回值同步，验证无修改应用/修改/取消/重开四条路径。

### S05：远端名称可逃出 Windows 下载目录

- 触发：用户下载包含 `..\outside.txt` 等名称的远端目录。Unix 允许反斜杠作为文件名字符，远端目录内容可以由其他用户控制。
- 证据链：生产 `ListDirectoryAsync` 原样返回 `Name`（已用 OpenSSH 验证）；`SftpDualPanePanel.cs:262/345/354` 直接 `Path.Combine`；`SftpService.cs:260/285` 追加或创建文件，没有确认最终路径位于用户选择的下载根内。
- 例：Windows 目标 `C:\Downloads\target` 加名称 `..\outside.txt`，最终落在 `C:\Downloads\outside.txt`。名称不等于 `..`，现有点目录过滤无效。
- 影响：远端目录数据越过本地保存范围创建/覆盖文件；权限限于运行应用的用户。medium：已确认真实服务传递名称和完整代码链；Windows 路径例仅以 `ntpath` 展示语义，未在 Windows 执行最终写入。
- 建议：`cs-issue`，拒绝非法/根路径/分隔符/设备名等名称，规范化后验证根目录边界，并考虑 Windows 名称碰撞。

### S06：打开 SFTP 时握手阻塞主界面

- 触发：通过菜单打开响应缓慢的 SFTP 服务端。
- 证据链：`ConnectionOpenCoordinator.cs:93` → `ProtocolTabOpener.cs:462` → 面板构造器 `SftpDualPanePanel.cs:90` → `BeginConnect`；服务 `SftpService.cs:43/67` 同步 `_client.Connect()` 后才返回 `Task.CompletedTask`。
- 实测：回环服务接收 TCP 后延迟关闭，`ConnectAsync` 在调用者线程阻塞 **706 ms** 后同步抛异常，没有先返回未完成任务。真实超时时长取决于网络和连接超时配置。
- 安全网：目录读取/文件传输有 `Task.Run`，连接入口没有；仅写 `await` 不能解除同步方法的阻塞。
- 影响：网络握手期间整个 WinForms UI 无响应，不能正常切换标签/取消。high，服务阻塞实测及 UI 线程入口静态确认。
- 建议：`cs-issue`，将握手纳入可取消异步生命周期，连接结果返回 UI 时检查存活状态。

### S07：关闭后的异步连接可复活并泄漏

- 触发顺序：同一连接仍有其他 SSH 标签；SFTP 等待 `EstablishAsync`；关闭 SFTP 标签；共享隧道随后就绪。
- 证据链：面板 `:183` await 使用 `CancellationToken.None`；Dispose `:369/370` 释放服务。`TabCloseService.cs` 按剩余标签保留共享隧道。await 恢复后 `:184` 无关闭状态检查，服务 `ConnectViaTunnelAsync` 也不检查 `_disposed`，重新赋值连接；服务 Dispose `:380` 以后直接返回。
- 实测服务层：`Dispose → ConnectAsync → Dispose` 后 `IsConnected=true`；传入已取消 token 也仍能连接。探针最后显式 Disconnect 清理。
- 影响：关闭标签后仍连接远端、使用已释放控件，连接可能不再被清理。medium：服务行为真实复现；上述共享隧道 UI 交错经代码复核，未做 Windows 时序复现。
- 建议：`cs-issue`，使用面板生命周期 token、禁止 disposed 服务重新连接，并清理迟到的连接结果。

### S08：删除目录只删内部内容

- 触发：选择任何远端目录执行删除，包括空目录。
- 证据链：`FilePaneModel.cs:202` 传 `recursive=true`；`SftpService.cs:313` 只调用递归函数；`:403/404` 删除子目录，没有删除最外层目录。
- 实测：包含 `child/leaf` 的目录删除后变空但仍存在；再次删除仍存在。UI 成功分支会显示完成并刷新。
- 影响：用户无法通过当前删除入口移除目录。high。
- 建议：`cs-issue`，递归成功后删除选中的目录本身，同时保护链接语义。

### S09：传输进度/取消组件没有接到实际传输

- 触发：发起大文件或多目录传输，想查看百分比或取消。
- 证据链：面板 `:255/262/338/354` 一律 `progress=null, CancellationToken.None`；底部只有 `_queueLabel`。`TransferProgressDialog/TransferProgressAdapter` 没有生产构造调用，只有测试直接构造；`TransferCenterPanel` 只保存字符串记录。
- 影响：实际 SFTP 没有百分比/字节进度和单任务取消入口；只能等完或关标签断连接。已有进度窗截图不能证明接线完成。high，生产调用与 UI 构造链已核对。
- 建议：`cs-issue`，让真实任务拥有进度、取消、结果状态，并让界面消费它们。

### S10：同名文件并发预览相互覆盖/删除

- 触发：连续预览不同目录或连接的同名文本文件，前一次尚未完成。
- 证据链：`FilePaneControl.cs:437` 每次启动独立任务，没有预览互斥；`SftpEnhancements.cs:25` 固定使用 `gdterm_preview_ + basename`；`:46` 各自删除同一路径。
- 实测：两次真实 SFTP 下载间用接口包装器控制调度，第二个预览完成并删除临时文件，首个恢复后抛 `FileNotFoundException`。包装器仅延迟 Download 返回，不替代生产下载或预览代码。
- 影响：预览失败，另一交错下可能显示错误文件内容。high；已复现失败交错，未把错误内容交错说成已实测。
- 建议：`cs-issue`，每次预览使用独立临时路径及对应清理责任。

### S11：拖拽未携带来源，方向仅靠目标面板推断

- 触发：同侧拖放，或从另一 SFTP 标签拖到当前本地栏。
- 证据链：`FilePaneControl.cs:135` 数据只有 `FileEntry[]`，`:142` 一律接受；模型没有来源 provider/连接；面板 `:118/197/233` 仅按落点决定上传/下载，始终使用当前 `_sftp`。
- 具体坏结果：把服务器 A 的 `/tmp/report.txt` 拖到服务器 B 标签的本地栏，实际下载 B 的 `/tmp/report.txt`；若不存在则错误。拖回本侧也会被误当跨本地/远端传输。
- 影响：错误来源的文件被传输，或无意义报错。medium，代码与数据契约已确认，未执行 Windows 跨标签鼠标拖拽。
- 建议：`cs-issue`，拖拽携带可信来源身份，只接受支持的源目标组合。

### S12：连接失效后状态与操作反馈不更新

- 触发：SFTP 已连接后被服务器断开，用户再次发起传输。
- 证据链：`SftpDualPanePanel.cs:191` 只在首次成功设置“已连接”，没有断线订阅/监测；`:228` 发现未连接直接 return；`ProtocolTabOpener.cs:466`–`474` 也未给 SFTP 接入终端健康监控。重新建连方法仅构造器调用。
- 影响：仍显示已连接，点击/拖放传输没有错误或重连提示；刷新目录才会看到读取失败，恢复需要关闭后重开。
- 置信度：high，状态和操作分支静态确认；本轮没有重新执行 Windows 断网交互。
- 建议：`cs-issue`，同步断线状态，提供可见失败与重连入口，恢复后重新读取目录。

### S13：有限行数的文本预览承担整个文件的下载成本

- 触发：F3/双击预览较大的 `.log/.txt` 文件。
- 证据链：`FilePaneControl.cs:441` 请求 100 行且 token 为 None；`SftpEnhancements.cs:28` 先完整下载，之后 `:34` 才限制读取行数。
- 实测：8,388,608 字节、131,072 行文件，预览下载完整 8 MiB，只展示不足 7,000 字符。临时文件与网络用量随完整文件大小增长。
- 影响：查看日志头部也要等待整个日志下载，占用带宽和临时磁盘。high，实际下载大小已测；未声称测过 GB 文件或弱网延迟。
- 建议：`cs-issue`，在远端读取流上限制字节/行数，同时支持超时与取消。

## 已核查的正常路径与覆盖边界

| 操作 | 证据与结果 |
|---|---|
| 新文件上传/下载 | 真实服务往返，内容一致 |
| 普通文件重命名/删除 | 真实服务执行成功；软链接另见 S02 |
| 列目录、权限展示 | 真实 listing 可用，显示与服务端实际权限一致；写入转换另见 S01 |
| 目录创建、过滤、排序 | 创建调用已核对；过滤/排序由 ListShell 处理，未误判为无事件；本轮未做 Windows 输入验证 |
| 密码/密钥、主机指纹、跳板 | 复用已验收的连接工厂与 host-key guard；本轮公钥直连真实通过，旧跳板路由测试已回读；未重新全测认证矩阵 |
| 权限取消 | 调用方只在 DialogResult.OK 下写入；当前取消分支不调用 chmod；未执行真实 ESC 键输入 |
| 文件夹“同步” | Planner/Runner 源码已读，Planner 测试执行；没有生产入口，不能把内部 runner 的问题宣称为可达 UI 缺陷，也不能宣称用户已有同步功能 |
| UI 完整性 | 既有目录明确跳过真实 SFTP 双栏；布局矩阵及功能图的 100% 不代表覆盖真实文件操作 |

## Verification Evidence

- 实际回读 **32 个源码文件**：22 个全文、10 个沿边界取样；完整清单与 SHA-256 见 [verification.json](evidence/verification.json)。另读 2 份项目文档（attention、architecture 的 SFTP/隧道条款）、功能图，以及既有验证记录和隔离测试工具。
- 维度：正确性、安全、性能、维护性、架构；重点是数据写入、链接/路径边界、权限契约、异步关闭与 UI 接线。子 agent 使用数 **0**。
- 归一化登记候选 20：正式 13；筛除 6（3 个较低优先级 P2 正确性候选：同主机不同身份跟随、忙碌时丢弃跟随、空态层拖放；2 个无生产入口候选：同步执行器、旧 GetPermissionInfo；1 个共享 ListShell 分页线索留在其模块）；1 个待确认（本地 junction/循环链接的目录上传策略）。未把它们混入发现统计。P2 正确性保留 5 项，性能保留 1 项。
- [真实服务探针日志](evidence/run.log)：最终退出 **0**，22 个明确观察断言；包括正常操作和“确认存在缺陷”的断言，**退出 0 不代表产品无缺陷**。复用现有两组 SFTP 纯逻辑测试：**28 passed / 0 failed**。
- 探针链接未修改的生产源码，在 Linux/net8.0/SSH.NET 2026.0.0 下执行；临时 OpenSSH 只监听回环，所有文件、密钥均在隔离目录，结束清理。它不等同于产品 net462/Windows 端到端测试。3 条 CA1416 是探针调用 Linux 权限回读 API 的平台提示。
- 测试搭建阶段修正过探针的 Assert 属性名；后续预期正常的 chmod、目录链接检查暴露新缺陷，已保留在最终问题清单。未把早期失败当最终通过结果。
- UI：本轮检查旧 Windows CI `46113892779` / `24c8ffa` 的真实截图和树，核对相关源码与 HEAD 无差异；不是新 Windows 运行。截图中权限初始值 `0` 已目视确认。
- 工具：本机 Astrolabe 未暴露 `initial_instructions`；`find_refs(SftpService)` 明确 C# language server 未安装，按项目规则回退源码、rg 与项目工具。
- 未执行：全量应用构建/全量单测、新 CI、真实用户服务器、Windows 下载边界写入、鼠标拖拽、窗口关闭交错、系统 DPI。审计没有产品代码变更，不用旧全绿 CI 代替这些验证。

## 下一步

先通过 `cs-issue` 修复 S01–S07，加入真实 SFTP 权限回读、链接操作、同名文件覆盖和关闭竞争验证；再完成 S08–S13，并在 Windows 走真实双栏与权限表单往返。本文 `closed` 仅表示审计完成，以上问题仍待修复。
