---
doc_type: trick
type: technique
date: 2026-08-31
slug: rdp-tcp-proxy-wire-capture
topic: 内置 TCP 代理抓包 + Python 分析用于 RDP 协议问题排查
language: C# / Python
framework: .NET Framework 4.6.2 WinForms / FreeRDP 2.11.7
tags: [rdp, capture, proxy, tcp, dump, analysis, protocol]
status: active
---

# 内置 TCP 代理抓包 + Python 分析用于 RDP 协议问题排查

## 适用场景

- 需要对比两个 RDP 客户端（如 wfreerdp vs mstsc）的 wire 协议差异
- 堡垒机环境有零信任（零信任）限制，无法用 Wireshark 抓包
- 需要端到端验证协议补丁是否生效，而不仅仅是依赖客户端日志
- 排查 RDP 重定向重连、登录踢出等协议层问题

## 做法

### 1. 内置 TCP 代理（RdpTcpProxy）

在 gdterm 内部实现一个 TcpListener 代理，拦截 gdterm 到堡垒机的 TCP 连接：

- **启动**：`RdpDumpProxy.StartFor(host, port)` 在 127.0.0.1 上监听随机端口
- **转发**：每接受一个客户端连接，建立到真实堡垒机的上游连接，双向转发
- **记录**：每收到一个 TCP 数据块，写入 hex dump 文件（如 `rdp-dump-{ts}-c{port}.hex`）

C# 代码结构：

```
RdpDumpProxy.cs (src/Gdterm.Rdp/)
  - StartFor(host, port) → 选随机端口，启动 TcpListener
  - Stop() → 停止监听
  - 每连接：TcpClient → 上游 TcpClient → 双向 Task.Run 转发+记录
  - 输出：logs/rdp-dump/rdp-dump-{ts}-c{clientPort}.hex
           logs/rdp-dump/rdp-dump-{ts}-c{clientPort}-c2s.raw (原始二进制)
           logs/rdp-dump/rdp-dump-{ts}-c{clientPort}-s2c.raw (原始二进制)
```

**关键设计**：

- 代理工作是**透明的**：gdterm 的 wfreerdp 重定向重连会绕过代理直连堡垒机
  （因为重连是 wfreerdp 内部发起的，不是 gdterm 代理的），所以重连的 TCP 连接
  不会被 dump 捕获。这是合理的——重连的 X.224 CR、Connect Initial 等明文数据
  在重连 TCP 上。
- 代理必须**不修改连接配置**：连接 Host/Port 是用户设置的，不能篡改。
  gdterm 内部在 clone 后的配置上将 Host/Port 改为 127.0.0.1:proxyPort。
- 允许多个 TCP 连接：每个客户端连接（包括 LB probe 和 wfreerdp 主连接）产生
  独立的 hex dump 文件，文件名包含客户端端口区分。

### 2. Python 分析脚本

`tools/rdp-dump-analysis/` 目录包含以下工具：

**核心模块**：
- `cs_core.py` — 权威 TS_UD/CS_CORE 解析器，含 `walk_ud`、`parse_core`、`parse_sec`、`parse_cluster`、`parse_net` 函数

**分析脚本**：
- `parse_rdpdump.py` — 简化 hex dump 块解析器
- `parse_ci.py` — MCS Connect Initial 域参数 + TS_UD 块解析
- `core_diff.py` / `core_diff2.py` — CS_CORE/CS_SEC 字段提取和跨 dump 对比表
- `cr_raw.py` — X.224 CR 原始 cookie 提取
- `ud_parse.py` / `ud_parse2.py` — TS_UD 块遍历器

**CS_CORE 解析关键偏移**（230 字节 body，从 `01 c0 ea 00` 块头后开始）：

| 字段 | 偏移 | 类型 | 说明 |
|------|------|------|------|
| version | 0 | UINT32 | RDP 版本 |
| desktopWidth | 4 | UINT16 | 桌面宽度 |
| desktopHeight | 6 | UINT16 | 桌面高度 |
| colorDepth | 8 | UINT16 | 色深 |
| sas | 10 | UINT16 | 安全访问序列 |
| kbdLayout | 12 | UINT32 | 键盘布局 |
| clientBuild | 16 | UINT32 | 客户端构建号 |
| clientName | 20 | 32B UTF-16 | 客户端名称 |
| kbdType | 52 | UINT32 | 键盘类型 |
| ... | | | |
| highColorDepth | 136 | UINT16 | 高位色深 |
| supportedColorDepths | 138 | UINT16 | 支持的色深位掩码 |
| earlyCapabilityFlags | 140 | UINT16 | 早期能力标志 |
| clientDigProductId | 142 | 64B UTF-16 | 数字产品 ID（31 字符截断） |
| connectionType | 206 | UINT8 | 连接类型 |
| selectedProtocol | 208 | UINT8 | 选择的协议 |

### 3. FreeRDP 源码 DIAG 补丁

在 appveyor.yml 中用 PowerShell 对 FreeRDP 2.11.7 源文件注入 WLog_INFO 诊断，
打到 wire 上验证不了的客户端内部状态：

- `connection.c:rdp_client_redirect` — 重定向事件、RedirectionFlags、身份恢复
- `connection.c:rdp_client_reconnect` — 重连诊断、路由 token 恢复
- `nego.c:nego_send_negotiation_request` — X.224 CR 字节和 token 状态
- `info.c:rdp_send_client_info` — Client Info 标志、凭据、ARC cookie 长度
- `gcc.c:gcc_write_client_core_data` — 身份指纹（earlyCaps、build、connType）
- `gcc.c:gcc_write_client_cluster_data` — CS_CLUSTER 标志

## 为什么有效

1. **零信任友好**：代理在 gdterm 进程内，不依赖外部抓包工具
2. **字节级精度**：hex dump 完整记录 TCP 数据流，不依赖协议解析器
3. **可对比性**：mstsc 也通过代理连接到同一个目标，产生相同格式的 dump
4. **可复现**：Python 分析脚本是代码化的知识，可反复运行验证
5. **补丁验证**：FreeRDP 源码 DIAG 补丁在 CI 中编译，运行时自动输出，无需额外配置

## 示例

```bash
# 基础分析单个 hex dump
python3 tools/rdp-dump-analysis/parse_rdpdump.py tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61442.hex

# 提取 X.224 CR 路由 token
python3 tools/rdp-dump-analysis/cr_raw.py tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex | head -20

# 跨 dump 对比 CS_CORE 字段
python3 tools/rdp-dump-analysis/core_diff.py

# 解析 MCS Connect Initial 的 TS_UD 块
python3 tools/rdp-dump-analysis/parse_ci.py tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61442.hex
```

## 何时不适用

- 协议加密层（如 TLS/NLA）下看不到明文 payload，只能看到 X.224 头和 token
- 重定向重连的 TCP 连接绕过代理（wfreerdp 内部发起），需补 FreeRDP 源码 DIAG
- 代理本身引入 127.0.0.1 ClientAddress，影响基于 IP 的身份校验场景
- 高吞吐场景下代理性能可能成为瓶颈

## 已知坑

1. **重连绕过代理**：wfreerdp 的 redirect 重连是内部发起的，不经过 gdterm 的代理
   TCP 连接。所以重连的 CS_CORE 字节不在 dump 中，只能靠 FreeRDP 源码 DIAG 确认。
2. **CS_CORE 偏移验证**：`cs_core.py` 中的字段偏移必须从 `gcc.c:941-1010` 的写入顺序
   逐字节追踪，不能靠猜测。特别要注意 `imeFile` 64 字节和 `clientDigProductId` 64 字节
   的偏移量。
3. **TS_UD 块遍历**：块长度（bl）包含 4 字节头，所以 `body = ud[p+4:p+bl]`，下一块在
   `p += bl`。记成 `p += 4 + bl` 会导致只找到第一个块。
4. **PowerShell 补丁锚点**：appveyor.yml 的 FreeRDP 源码替换必须用 `Contains()` 检查
   锚点唯一性，且锚点文本不包含前导空白（Replace 会保留前导空白）。
5. **CI 编译顺序模拟**：新增补丁前必须用完整 CI 顺序模拟来验证锚点匹配和花括号平衡，
   因为后补丁可能依赖前补丁的产出。