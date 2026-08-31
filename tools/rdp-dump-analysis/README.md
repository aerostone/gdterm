# RdpTcpProxy 抓包分析工具

gdterm 内建的抓包代理（`rdp_tcp_dump` 元数据开关）将 RDP 流量写入 `logs/rdp-dump/` 目录，
格式为 `rdp-dump-{ts}-c{clientEphemeralPort}.hex`。本目录下的 Python 脚本用于离线分析这些 hex dump。

## 文件列表

| 脚本 | 用途 |
|---|---|
| `analyze_dump.py` | 基础解析：hex dump 分块 (C2S/S2C)、TPKT 帧拆分、X.224 CR/MCS 概要 |
| `cs_core.py` | **权威模块**：TS_UD 块遍历、CS_CORE / CS_SEC / CS_CLUSTER / CS_NETWORK 字段解析 |
| `core_diff.py` | 跨连接 CS_CORE 全字段对比表（默认对比 ver166 + mstsc 黄金样本） |
| `core_diff2.py` | 简化的跨连接对比（依赖 core_diff.py） |
| `cr_raw.py` | 打印 X.224 Connection Request 原始字节 + token 文本 |
| `extract_ci.py` | 提取 Connect Initial TPKT 帧，解析 TS_UD 块 |
| `parse_ci.py` | 解析 MCS Connect Initial 的 BER domain parameters 和 GCC userData |
| `parse_rdpdump.py` | 命令行入口：打印每个 dump 的 PDU 概要 |
| `ud_parse.py` | TS_UD 块遍历 + 关键字段（CS_SEC/CS_CLUSTER/CS_NET） |
| `ud_parse2.py` | 简化版 TS_UD 块遍历（依赖 ud_parse.py） |

## 用法

```bash
# 基础分析
python3 tools/rdp-dump-analysis/parse_rdpdump.py tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61442.hex

# CR token 原始字节
python3 tools/rdp-dump-analysis/cr_raw.py tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex

# TS_UD 块对比
python3 tools/rdp-dump-analysis/ud_parse.py

# CS_CORE 全字段对比
python3 tools/rdp-dump-analysis/core_diff.py
```

## 关键数据结构

### TS_UD 块遍历（已验证，自 `cs_core.py`）

```
'Duca'/$key + 2 字节 PER 长度（81 3a）之后是 TS_UD 块序列。
块头 = LE type(2) + LE len(2)，len 含 4 字节头本身
=> body = ud[p+4 : p+bl]，下一个块 p += bl
```

已知类型：
- `0xC001` — CS_CORE（核心数据，body=230B）
- `0xC002` — CS_SEC（安全：encryptionMethods）
- `0xC003` — CS_NET（频道：rdpdr/cliprdr/rdpsnd/drdynvc）
- `0xC004` — CS_CLUSTER（flags + sessionId）
- `0xC005` — CS_MONITOR（显示器布局）

### CS_CORE body 布局（230B，gcc_write_client_core_data 写入顺序）

| 偏移 | 大小 | 字段 |
|---|---|---|
| 0 | 4 | RdpVersion |
| 4 | 2 | DesktopWidth |
| 6 | 2 | DesktopHeight |
| 8 | 2 | ColorDepth（RNS_UD_COLOR_8BPP=0xCA01，忽略） |
| 10 | 2 | SASSequence (0x0001) |
| 12 | 4 | KeyboardLayout |
| 16 | 4 | ClientBuild |
| 20 | 32 | clientName (UTF-16, 截断 15 字符) |
| 52 | 4 | KeyboardType |
| 56 | 4 | KeyboardSubType |
| 60 | 4 | KeyboardFunctionKey |
| 64 | 64 | imeFileName (全零) |
| 128 | 2 | postBeta2ColorDepth (RNS_UD_COLOR_8BPP=0xCA01) |
| 130 | 2 | clientProductID (硬编码 1) |
| 132 | 4 | serialNumber (0) |
| 136 | 2 | highColorDepth (MIN(ColorDepth, 24)) |
| 138 | 2 | supportedColorDepths (15|16|24|32bpp 标志) |
| 140 | 2 | earlyCapabilityFlags |
| 142 | 64 | clientDigProductId (UTF-16, 截断 31 字符) |
| 206 | 1 | connectionType |
| 207 | 1 | pad1octet |
| 208 | 4 | serverSelectedProtocol |
| 212 | 4 | desktopPhysicalWidth |
| 216 | 4 | desktopPhysicalHeight |
| 220 | 2 | desktopOrientation |
| 222 | 4 | desktopScaleFactor |
| 226 | 4 | deviceScaleFactor |

### earlyCapabilityFlags 位定义

```
0x0001  RNS_UD_CS_SUPPORT_ERRINFO_PDU
0x0002  RNS_UD_CS_WANT_32BPP_SESSION
0x0004  RNS_UD_CS_SUPPORT_STATUSINFO_PDU
0x0008  RNS_UD_CS_STRONG_ASYMMETRIC_KEYS
0x0020  RNS_UD_CS_VALID_CONNECTION_TYPE
0x0040  RNS_UD_CS_SUPPORT_MONITOR_LAYOUT_PDU
0x0080  RNS_UD_CS_SUPPORT_NETCHAR_AUTODETECT
0x0100  RNS_UD_CS_SUPPORT_DYNVC_GFX_PROTOCOL
0x0200  RNS_UD_CS_SUPPORT_DYNAMIC_TIME_ZONE
0x0400  RNS_UD_CS_SUPPORT_HEARTBEAT_PDU
```

## 数据来源

原始抓包 hex dump 位于 `tmp/rdp-dump/`（未纳入版本控制，因为可包含敏感令牌）：
- `tmp/rdp-dump/` 根目录 — 2026-08-31 08:03 日志（含 mstsc 黄金样本）
- `tmp/rdp-dump/ver165/` — v0.1.165 运行日志
- `tmp/rdp-dump/ver166/` — v0.1.166 运行日志