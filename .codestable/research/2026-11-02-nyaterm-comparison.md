# NyaTerm 对比研究：可吸收的优点（2026-11-02）

> 来源：https://github.com/nyakang/nyaterm（README.zh-CN，2.x 时代功能面）
> 目的：gdterm 与 NyaTerm 逐项对比，列出值得吸收的优点 + 落点 + 不吸收清单。
> 状态：研究记录（非变更包），若立项走 cs-feat / cs-roadmap。

## 0. 定位差异（吸收决策的前提）

| | gdterm | NyaTerm |
|---|---|---|
| 技术栈 | WinForms / .NET Framework 4.6.2 | Rust / GPUI（Zed 系） |
| Windows | **Win7+（核心定位）** | 仅 Win10/11 x64/arm64 |
| 跨平台 | 否（Win 专精） | Win/macOS/Linux |
| RDP | AxMsRdpClient8 原生 COM（Win7 可用） | IronRDP helper 进程 |
| 数据 | 本地 JSON + KeePass .kdbx | 本地库 + 加密云同步 |

**结论**：gdterm 的护城河是 Win7 + 原生 Win 集成（RDP COM/KeePass/cmdkey/托盘/文件管理器拖拽），
不是跨平台。吸收原则：**补工作流深度，不追跨平台渲染**；所有候选项必须在 net462/WinForms 下可实现。

## 1. 能力面对照总表

| 能力域 | gdterm 现状 | NyaTerm | 差距 |
|---|---|---|---|
| 会话类型 | SSH/本地/串口/Telnet/RDP | SSH/本地/串口/Telnet/RDP/**VNC** | VNC 缺 |
| 工作区 | 标签+分屏+标签状态恢复（OpenTabs/ActiveTab） | 标签+分屏+**标签拖拽停靠**+布局恢复 | 标签拖拽缺 |
| 命令面板 | 仅 ConnectionQuickJumpForm（连接跳转） | **全局命令面板**（动作+会话+连接+新建入口） | 缺 |
| 终端增强 | 搜索/高亮/自动日志/补全/历史/keepalive | 搜索跳转+历史+**行号/时间戳 gutter**+**动作链接**+**关键词 JSON 导入**+**大输出保护**+**在线搜索/翻译** | 见 A 组 |
| Zmodem | 接收（ZmodemReceiver/Handler） | 收发+**记录本地路径/资源管理器定位** | 小缺口 |
| SFTP | 双栏/传输中心/进度/预览/权限 | 全套+**本地编辑回传（watcher 自动上传）**+多格式只读预览（JSON/MD/CSV/PDF/图片，解码离渲染线程）+**资源管理器拖入上传** | 回传流程缺 |
| AI | Ask 问答+命令门（Run this→危险命令闸）+流式 | Ask+**Agent 模式**（观察-决策-执行循环、审批执行、内联输出回写、@会话提及、最近输出入口、风险等级命令卡片） | Agent 缺 |
| 认证 | 密码/密钥/KeePass 深度集成/OTP 无 | +**OTP 管理**（TOTP/HOTP/二维码导入/SSH 自动填充）+正则终端自动填充 | OTP 缺 |
| 代理/网络 | 多跳链/SOCKS5/隧道/keepalive | +**HTTP 代理/ProxyCommand**+X11 转发+环路阻止跳板 | HTTP 代理/ProxyCommand 缺 |
| 同步备份 | **无**（本地 JSON） | **WebDAV/S3/Gist 加密同步**+定时备份保留策略+冲突处理+.nya 导入导出 | 整域缺 |
| 导入 | mRemoteNG XML/SecureCRT CSV/自身 JSON | **+Xshell/MobaXterm/WindTerm** | 导入面窄 |
| 监控 | HealthMonitor（本地）+工具集（证书/时同步/端口/网络扫描） | **SSH 会话内资源/GPU/NPU/进程/Docker 监控**+资产监控工作区 | 远程监控缺 |
| 外观 | 主题/字体/DPI | +**背景图**（cover/contain/stretch/tile+双层不透明度） | 外观项（低优先） |
| 更新/诊断 | AppVeyor 分发，无自更新 | 签名更新清单+**诊断包导出**+日志管理 | 诊断包缺 |
| 分发 | Win 单平台 | +**便携版**（数据在旁侧 data/） | 便携版缺（小） |

gdterm 已有且优于/不同于 NyaTerm 的（保持，不追）：
KeePass 深度集成（auto-type/健康检查/cmdkey 注入）、危险命令闸（且已接 AI 命令门）、
宏录制/批处理/登录脚本/命令模板、Secret 扫描器、工具集（证书/时同步/端口/网络扫描）、
Win7 兼容、RDP 原生 COM、串口 Backspace（待确认）、多跳链自动端口。

## 2. 可吸收的优点（按优先级分组）

### A 组：快速收益（单文件~小模块，1-3 天级）

| # | 吸收点 | NyaTerm 能力 | gdterm 落点 | 价值 |
|---|---|---|---|---|
| A1 | **命令面板**（Ctrl+Shift+P） | 动作/已开会话/保存连接/新建会话统一可搜索入口 | 新 CommandPaletteForm，复用 SnippetSearchPanel 的搜索框交互 + GlobalHotkeyController；数据源：MainFormCommandRouter 已有动作表 | 现代终端标配，减少菜单层数；gdterm 菜单 5 层深，面板可显著提效 |
| A2 | **导入源扩展** | Xshell/MobaXterm/WindTerm 导入 | ConnectionImporterExporter 加 3 个 DetectFormat 分支（Xshell=INI/目录、MobaXterm=.ini、WindTerm=.json） | 这三个是运维存量主力，导入=获客；纯解析代码，无 UI 风险 |
| A3 | **终端动作链接** | 自动识别 IPv4/host:port/压缩包文件名可点击 | TerminalControl 渲染后处理 + 右键/单击菜单（复制、在线搜索、打开 host:port 连接）；与现有高亮规则正交 | 高频微交互；配合 A5 在线搜索形成闭环 |
| A4 | **在线搜索/翻译** | 选中文本→浏览器搜索/翻译 | TerminalControl 右键菜单两项，Process.Start 默认浏览器 URL；零依赖 | 成本极低，体验补齐 |
| A5 | **关键词高亮 JSON 导入/导出** | 内置预设+自定义规则+JSON 导入 | HighlightRulePanel + HighlightStore 加 Import/Export（序列化现有规则模型） | 跨机共享规则；配合 A12 同步前置 |
| A6 | **时间戳/行号 gutter**（可选） | 可选行号+时间戳侧栏 | TerminalControl 左侧 gutter 绘制（渲染层已有行模型）；设置开关 | 日志阅读/客服排障定位行号 |
| A7 | **诊断包导出** | 一键收集版本+脱敏日志+配置 | 菜单入口；复用 Gdterm.Logging 的 sanitizer + JSON Lines 滚动日志；zip 输出 | 用户报障自助化；已有脱敏组件，成本极低 |
| A8 | **Zmodem 完成文件定位** | 记录本地路径+资源管理器显示 | ZmodemReceiver 落盘处记事件→NotificationCenterPanel 通知→"在文件夹中显示" | 小改动闭环现有功能 |
| A9 | **SFTP 资源管理器拖入上传** | Windows 文件管理器拖文件到文件浏览器 | SftpDualPanePanel 设 AllowDrop，Drop 时解析 FileDrop 路径走现有上传队列 | WinForms 原生支持，Win 平台独享便利 |
| A10 | **大输出保护** | 洪泛输出保护+可配置回滚缓冲 | VtTerminalEngine 渲染批次节流（超阈值丢帧/合并批），回滚上限已可配置则补 UI | 防大文件 cat 卡 UI；需在 CI 冒烟加洪泛用例 |
| A11 | **标签拖拽+停靠** | 标签拖拽重排/停靠 | TabContainerControl 加 DragDrop（参照 ConnectionTreeControl 已有实现）；布局恢复扩 SplitState 进 SessionState | 分屏工作流完整性 |
| A12 | **便携版** | 便携 zip，数据在旁侧 data/ | 启动时探测 exe 旁 data/ 优先（现有存储路径逻辑收口一处） | 运维 U 盘场景；Win7 环境常见 |

### B 组：中期模块（新模块/跨文件，1-2 周级）

| # | 吸收点 | 说明 | 落点 |
|---|---|---|---|
| B1 | **OTP 管理**（TOTP/HOTP） ✅ 已实现（CI 待验证） | 二维码导入（zxing.net 4.x 支持 net462）、时间码列表、SSH 密码后自动填充 | 已完成：Gdterm.Security/Otp 模块（RFC 4226/6238 标准向量验证）+ OtpStoreJson（主密码 AES 落盘）+ 工具菜单「OTP 动态码管理」；二维码导入/SSH 自动填充留 v2 |
| B2 | **加密同步/备份** | 主密码加密快照→WebDAV/S3/Gist；定时备份+保留策略+冲突处理 | gdterm 已有主密码+XOR 加密日志基础设施；升级为 AES-GCM（System.Security.Cryptography net462 内置）+ 同步引擎模块；UI 走 AppearanceSettingsForm 同级的 SyncSettingsForm |
| B3 | **AI Agent 模式** | 观察-决策-执行循环、审批门槛执行、终端内联输出回写（行数可配）、@会话提及、最近输出入口、风险等级命令卡片 | AiAssistantService 加 Agent 状态机；执行通道复用 AiCommandGateBinder 的危险命令闸（天然审批点）；@提及用 ActiveSessionBridge 拉多会话上下文 |
| B4 | **远程主机监控面板** | SSH 会话内资源（CPU/内存/磁盘/网络）/GPU（nvidia-smi）/进程/Docker | 会话内 exec 采集（agentless，5s 轮询+可配）→新 RemoteMonitorPanel 侧栏；工具集已有网络扫描可复用展示组件 |
| B5 | **本地编辑回传**（watcher 自动上传） | 远程文件→本地编辑→保存后 FileSystemWatcher 触发自动上传回原路径+冲突提示 | SftpDualPanePanel 加"编辑"动作：下载临时文件→打开系统编辑器→watcher→md5 变更确认→上传；冲突时提示覆盖/放弃 |
| B6 | **HTTP 代理/ProxyCommand** | SOCKS5 之外补 HTTP CONNECT 代理与 ProxyCommand（跳板场景刚需） | Gdterm.Tunnel 代理层扩展（SSH.NET 的 SocketFactory 支持自定义） |
| B7 | **VNC 会话** | direct TCP + VNC Auth + Tight/ZRLE + 剪贴板交换 | 纯 managed 实现无 OS 依赖（Win7 安全）；参照 C1 的 helper 进程模式隔离协议栈；优先级低于 B1-B5 |
| B8 | **多格式只读预览升级** | JSON/MD/CSV/TSV/图片/PDF 多标签预览+大小上限+超限明确提示 | SftpEnhancements 预览扩展；解码走 Task.Run（现有 async 签名已具备）；PDF 可用 PdfPig（netstandard2.0） |

### C 组：架构模式（不是功能，是写法）

| # | 模式 | NyaTerm 做法 | gdterm 可借鉴处 |
|---|---|---|---|
| C1 | **协议隔离 helper 进程** | RDP/VNC 各跑独立 helper 进程+类型化 IPC，协议栈崩溃不拖死 UI | gdterm RDP 走 AxMsRdpClient8 COM（进程内，插件崩=主窗崩）；vendor/freerdp 调查已在做，可升级为独立进程架构（RDP 崩溃恢复） |
| C2 | **解码离渲染线程+大小上限+明确报错** | 预览加载/解码在渲染线程外，按格式设上限，超大给明确提示 | 现有 SFTP 预览/高亮导入/大文件打开路径统一套此防御模式 |
| C3 | **签名更新清单** | Help→检查更新走签名 manifest | gdterm 分发走 AppVeyor，可加 sha256 manifest 校验下载包（防中间人），与 A7 诊断包同批做 |
| C4 | **Backspace Mode 显式切换** | Telnet/串口 Ctrl+H vs 0x7F 切换 | 确认 gdterm 串口会话是否已有；无则串口设置加一个 bool（极小） |
| C5 | **防抖自动推送+详细状态更新** | 本地变更后防抖同步+状态条 | 任何 B2 同步/自动回传功能的状态呈现规范：状态栏事件流而非弹窗 |

## 3. 不吸收 / 暂缓清单（及理由）

| 项 | 理由 |
|---|---|
| 跨平台（macOS/Linux） | 与 Win7 定位互斥，技术栈不同（GPUI vs WinForms），追之即丢护城河 |
| 背景图/壁纸 | 纯外观，运维场景价值低；如做应排在 B 组之后 |
| 资产监控工作区（卡片/面包屑/GPU-NPU 总览大屏） | 依赖 B4 单会话监控先行；大屏形态对 Win7 老硬件不友好 |
| Notes 面板 | gdterm 有命令模板/快捷命令/宏覆盖大部分场景；独立 Notes 与 .kdbx/外部笔记职责重叠 |
| X11 转发 | 依赖本地 X server（Xming/VcXsrv），Win7 目标用户群重合度低；按需再接 |
| 会话录制（asciinema 式） | gdterm 已有自动日志（主机+时间命名+轮转）覆盖排障场景；asciinema 回放为锦上添花，暂缓 |
| Discord/微信社区徽章等运营项 | 非产品能力 |

## 4. 建议的落地顺序（若立项）

### 第一批完成状态（2026-11-02，4 个提交）

| # | 状态 | 提交 | 说明 |
|---|---|---|---|
| A1 命令面板 | ✅ | 26ffb96 | Ctrl+Shift+O（P 被片段搜索占用）；~50 条命令 8 分组；标签/连接动态项 |
| A2 导入扩展 | ✅ | 84e9409 | Xshell/MobaXterm/WindTerm 解析器 + 多选批量导入；GBK 编码支持；37 断言 |
| A3 动作链接 | ⚠️ lite | 20e938b | 选中 host:port → 打开对应连接（事件链）；完整可点击链接（渲染层）待立项 |
| A4 在线搜索/翻译 | ✅ | 20e938b | 终端右键「在线」：Bing 搜索/翻译 + ip138 IP 查询 |
| A7 诊断包 | ✅ | 51c9abd | 帮助→导出诊断包：版本+脱敏日志+脱敏配置 zip；排除 .kdbx/命令历史 |

CI 验证：0.1.352/353 两轮红均为新增代码触发（csproj 路径转义、ui-ux-lint 3 新增违规），
逐轮定点修复后 **0.1.354 全绿**：测试 231/231（含新增 54）、UI smoke 7/7、
ui-ux-lint 基线外零新增、gen-tokens/编码守卫过。门禁体系按设计捕获了新代码的 3 类问题。

## B1 实现记录（2026-11-02）

**范围**：TOTP/HOTP 生成器 + otpauth:// URI + 加密落盘 + 管理器窗口（不含二维码扫描与 B2 同步）。

| 文件 | 说明 |
|---|---|
| `Gdterm.Security/Otp/OtpAlgorithm.cs` | SHA1/256/512 |
| `Gdterm.Security/Otp/OtpGenerator.cs` | RFC 4226 HOTP + RFC 6238 TOTP + 周期剩余秒（纯函数） |
| `Gdterm.Security/Otp/Base32.cs` | RFC 4648（net462 无 BCL Base32）；解码容忍大小写/空格/连字符/填充 |
| `Gdterm.Security/Otp/OtpAuthUri.cs` | `otpauth://totp|hotp/Issuer:account?secret=...` 解析/生成 |
| `Gdterm.Security/Otp/OtpEntry.cs` | 条目 POCO；`CodeAt()` 锁定（gdo1:）时返回 null |
| `Gdterm.Connections/OtpStoreJson.cs` | 与 ConnectionStoreJson 同构（原子写/版本/备份）+ protector 钩子接主密码 AES |
| `Gdterm.UI/Forms/OtpManagerForm.cs` | 列表 + 1s 倒计时刷新 + 复制/添加/删除；空态引导；未解锁显示「已锁定」 |
| `Gdterm.UI/Forms/AddOtpForm.cs` | 粘贴 URI 自动预填 + 手动字段 + 即时校验（AntdUI 错误态） |
| `MainFormMenuBuilder.cs` | 工具→「OTP 动态码管理…」 |
| `Program.cs` | `WireOtpSecretProtection`（WireSecretProtector 同款模式） |

**验证**：RFC 4226 附录 D 全 10 向量 + RFC 6238 附录 B SHA1/256/512×8 位（Python 标准库 + openssl 双重对账）。
⚠️ 教训：RFC 向量必须以官方原文为准——本会话凭记忆的 Base32 表与 HOTP SHA256 列均与原文不符，已用 `python3 base64`/`openssl` 交叉验证修正。

**安全**：密钥落盘主密码 AES（gdo1: 前缀）；诊断包文件名白名单补 otp/password 排除（双保险：文件本在 data 根而非 config/ 收集范围）。
**工程**：Gdterm.Connections 新增 Gdterm.Security 引用（Security→Core 单向，无环）。
**已知边界（v2）**：管理器打开期间应用被锁定→码不自动恢复（重开窗口即可）；HOTP counter 不自动递增；无二维码扫码（手动粘贴 URI/密钥）。

1. **第一批（A 组挑 4 项，1 个迭代）**：A1 命令面板 → A2 导入扩展 → A4 在线搜索/翻译（含 A3 动作链接）→ A7 诊断包。
   理由：纯增量、无破坏性、直接提升日常效率与报障效率。
2. **第二批（B 组前三）**：B1 OTP → B2 加密同步（用户换机刚需，主密码基础设施已就绪）→ B3 AI Agent（差异化卖点，危险命令闸已是审批骨架）。
3. **第三批**：B4 远程监控 → B5 回传 → B8 预览升级（SFTP 工作流深度三件套）。
4. **架构债**：C1 helper 进程随 RDP 稳定性问题暴露时再做（vendor/freerdp 调研延续）。

每批走 cs-feat（涉及公开行为/安全/数据变更的走 standard），A2 导入解析需补 fixture 回归（.codestable/changes 惯例）。
