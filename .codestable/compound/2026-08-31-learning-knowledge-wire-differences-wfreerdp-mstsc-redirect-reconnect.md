---
doc_type: learning
track: knowledge
date: 2026-08-31
slug: wire-differences-wfreerdp-mstsc-redirect-reconnect
component: FreeRDP / gdterm RDP 连接
tags: [rdp, redirect, reconnect, wire-protocol, mstsc, wfreerdp, cs_core, ber, cs_cluster]
status: active
---

# wfreerdp 与 mstsc 在 RDP 重定向重连的明文差异

## 背景

通过内置抓包代理（RdpTcpProxy）捕获 FreeRDP 2.11.7 和 mstsc 的 RDP 重定向重连
wire 数据，对比 mstsc 黄金样本（成功连接）与 wfreerdp 问题连接，发现以下明文差异。

## 指导原则

1. **重定向重连的 TCP 连接是独立的**，不通过抓包代理。
2. **kick 决策在 SEC_EXCHANGE 之前**：服务器在 token 重连的 SEC_EXCHANGE 后约 0.1s
   发送 MCS 断开 ultimatum，所以 kick 决策只基于 X.224 CR + Connect Initial CS_CORE +
   SEC_EXCHANGE，不涉及 Client Info 内容。
3. **Client Info 内容被 RC4 加密**，无法从 wire dump 直接比较，依赖 FreeRDP 的
   `info.c` 预加密 hex dump 诊断。
4. **身份差异必须全局一致**：首段连接和重连的 CS_CORE、CS_SEC、CS_CLUSTER、CS_NET
   身份字段必须相同，因为堡垒机做的是**绝对校验**而非**前后一致性校验**。

## 已确认的明文差异（wfreerdp vs mstsc）

### 1. CS_CORE 身份字段

| 字段 | 偏移 (body) | wfreerdp (默认) | mstsc (黄金样本) | 修复方式 |
|------|------------|---------------|-----------------|----------|
| RdpVersion | 0 | 0x0008000c (RDP 10.7) | 0x0008000d (RDP 10.8) | 快照块设 0x0008000D |
| DesktopWidth | 4 | 2296 | 2292 | 快照/恢复 |
| ClientBuild | 16 | 18363 | 19041 | 恢复块设 19041 |
| earlyCapabilityFlags | 140 | 0x04a3 | 0x07af | SupportStatusInfoPdu/GraphicsPipeline/DynamicTimeZone=TRUE + 直接 OR 0x0008 |
| clientDigProductId | 142 (64B UTF-16) | 全零 | `b87232e6-fe1b-44a2-9802-02b5a49` | ClientProductId 设该值 |
| connectionType | 206 | 6 | 7 | 设 CONNECTION_TYPE_AUTODETECT |

earlyCaps 0x07af 位分解：`0x0001|0x0002|0x0004|0x0008|0x0020|0x0080|0x0100|0x0200|0x0400`
= ERRINFO_PDU | WANT_32BPP | STATUSINFO | STRONG_ASYMMETRIC_KEYS |
VALID_CONNECTION_TYPE | NETCHAR_AUTODETECT | DYNVC_GFX | DYNAMIC_TIME_ZONE | HEARTBEAT。

注意：`0x0040`（MONITOR_LAYOUT）不在 mstsc 的 0x07af 中，所以不能设 `SupportMonitorLayoutPdu=TRUE`。

### 2. CS_CLUSTER flags

| 客户端 | 首连 | 重连 |
|--------|------|------|
| wfreerdp | 0x0d | 0x0f |
| mstsc | 0x15 | 0x17 |

差异：wfreerdp 使用 `REDIRECTION_VERSION4` (0x03 << 2 = 0x0c)，mstsc 使用
`REDIRECTION_VERSION5` (0x04 << 2 = 0x10) 外加 `REDIRECTED_VERSION` 0x04 位。
重连时双方都加 `REDIRECTED_SESSIONID_FIELD_VALID` (0x02)。

### 3. CS_NET rdpdr 通道选项

| 客户端 | 通道选项 |
|--------|---------|
| wfreerdp | 0xc0800000 = INITIALIZED \| ENCRYPT_RDP \| COMPRESS_RDP |
| mstsc | 0x80800000 = INITIALIZED \| COMPRESS_RDP |

wfreerdp 额外设置了 `CHANNEL_OPTION_ENCRYPT_RDP` (0x40000000) 位。去掉该位后
通道数据退回到 MCS 层加密，不影响功能。

### 4. MCS Connect Initial BER 编码

| 维度 | wfreerdp | mstsc |
|------|---------|-------|
| Connect Initial 大小 | 451 字节 | 446 字节 |
| 域参 BER 编码 | 02 03 00 ff ff（3 字节） | 02 02 ff ff（2 字节） |
| 域参 BER 编码 | 02 03 00 fc 17（3 字节） | 02 02 fc 17（2 字节） |

差异来自 `ber_write_integer` 对值 >= 0x8000 的编码：FreeRDP 保守地加前导零字节
以保证正数编码，但 MCS 域参解码器视为无符号整数，2 字节编码（MSB=1）同样正确且与 mstsc 一致。

### 5. 已确认无差异的字段

- **X.224 CR 大小与形状**：80 字节，61 字节 token + 8 字节 RDP_NEG_REQ proto=0x0
- **CS_SEC 加密方法**：0x1b 全套，字节一致
- **ClientInfo 安全头**：flags 0x02015080（wfreerdp 常数）vs 0x00020150（mstsc 常数），但都是客户端常数且首连/重连一致
- **ClientAddress**：通过代理时都是 127.0.0.1，不通过代理时都是真实 IP
- **首段连接结束序列**：wfreerdp 和 mstsc 都是 42B 指针 PDU + 重定向 PDU + S2C ultimatum + C2S ultimatum

## 适用边界

- 本知识仅适用于 `LB_LOAD_BALANCE_INFO` 重定向（flags=0x2）场景
- 本知识仅适用于 NSFOCUS 系列堡垒机（`NSFVERIFYHASH` 路由 token）
- 非 LB 重定向（到不同目标）的差异可能不同
- mstsc 版本不同（Win10 19041 vs 其他）可能会有不同的身份字段

## 参考

- 权威 TS_UD/CS_CORE 解析器：`tools/rdp-dump-analysis/cs_core.py`
- Gcc.c 写入函数：`gcc_write_client_core_data` (gcc.c:919-1010)
- `gcc.c:1714` — `gcc_write_client_cluster_data` flags 公式
- `rdpdr_main.c:2215` — rdpdr 通道选项
- `ber.c:430` — `ber_write_integer` 整数编码
- 调查报告：`docs/RDP-REDIRECT-INVESTIGATION.md`