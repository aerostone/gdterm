---
doc_type: learning
track: pitfall
date: 2026-09-04
slug: freerdp-per-length-encoding-sec-exchange
component: FreeRDP 2.11.7 libfreerdp/core/rdp.c（rdp_write_header）
tags: [rdp, freerdp, mcs, per, sec-exchange, per-length, wire-protocol, redirect, target-server]
status: active
---

# FreeRDP `rdp_write_header` 恒用 2 字节 PER 长度编码，SEC_EXCHANGE 帧 95B vs mstsc 94B，目标服务器在 SEC_EXCHANGE 后 DPU 踢线

## 问题

FreeRDP 2.11.7 的 `rdp_write_header`（rdp.c:538-541）对 MCS SendData 的
`userData (OCTET_STRING)` PER 长度**无条件写 2 字节**（`length | 0x8000`），
而 mstsc 按 ASN.1 PER 规范在 payload <= 0x7F 时写 **1 字节**。

对 SEC_EXCHANGE（payload=80），这导致：

| 客户端 | MCS userData PER 长度 | SEC_EXCHANGE 帧大小 |
|--------|----------------------|--------------------|
| mstsc（c57179 golden） | `50`（1 字节，= 80） | **94B** |
| gdterm/FreeRDP（c64267 redirect） | `80 50`（2 字节，= 80\|0x8000） | **95B** |

内部 SEC data 逐字节相同：`01 02 00 00`（flags=0x0201 即
`SEC_EXCHANGE_PKT|SEC_LICENSE_ENCRYPT_SC`，LE）+ `48 00 00 00`（key_len=72 LE）+
64B RSA 加密 ClientRandom + 8B 尾部（gdterm 8B vs mstsc 7B，零填充差异）。

**目标 Windows Server（token 路由后的真正服务器）在收到 95B SEC_EXCHANGE 后，
立即回 `MCS Disconnect-Provider-Ultimatum`（9B：`03 00 00 09 02 f0 80 21 80`,
reason=0x80）**，Client Info 从未上 wire。而 mstsc 94B 的 SEC_EXCHANGE 被接受，
继续 Client Info（331B）→ 42B 服务器响应 → 453B 能力集 → 完整会话。

## 症状

- 带路由 token 的进程内 redirect 重连，发送 SEC_EXCHANGE 后立即被 DPU 踢出，
  **Client Info 帧从未出现在 rdp-dump 中**（此前误判为 Client Info 被拒）
- diag.log 中仍出现 `gdterm redirect client info pdu (4096 bytes)` hexdump —— 因为
  该 dump 是 `rdp_send` **发送前**由 `info.c` 打的，帧未真正发出也打印（误导性）
- 堡垒机（首连/fallback 路径）**容忍两种编码**：gdterm 首连 95B 成功、mstsc 94B 成功，
  所以仅 token 路由到目标服务器时才暴露

## 没用的做法（均未触及 PER 长度）

| 尝试 | 结果 |
|------|------|
| 凭据清除（AUTOLOGON=0） | 仍被踢 |
| 锁协议到 legacy RDP（proto=0x0） | 仍被踢 |
| 身份伪装（CS_CORE 全字段字节对齐） | 仍被踢（GCC 四块逐字节一致） |
| CS_CLUSTER flags=0x15/0 与 0x17/1 都试 | 两种都收到 LOGOFF_BY_USER / DPU |
| salted-checksum 清除（do_secure_checksum=FALSE, SaltedChecksum=FALSE） | 无效（v0.1.178 实测 wire=0x0048 仍被踢） |
| clientAddress 覆盖为真实 IP | 无效（v0.1.181 实测 wire 确认发 192.168.3.103 仍被踢） |
| Client Info 明文 300B/304B/308B 对齐 | 无效（大小差异非判别维度） |

## 解法

修改 `libfreerdp/core/rdp.c` 的 `rdp_write_header`，在写 MCS SendData 头时：
先计算 `perUserDataLen = length - RDP_PACKET_HEADER_MAX_LENGTH`（=15），
若 `<= 0x7F` 则 `length--`（TPKT 总长减 1），并用 `per_write_length(s, perUserDataLen)`
替代无条件 `Stream_Write_UINT16_BE(s, (length-15) | 0x8000)`。

```c
int perUserDataLen = (int)(length - RDP_PACKET_HEADER_MAX_LENGTH);
if (perUserDataLen <= 0x7F)
    length--;
mcs_write_domain_mcspdu_header(s, MCSPDU, length, 0);  /* TPKT 长度同步减 1 */
...
per_write_length(s, perUserDataLen);  /* userData (OCTET_STRING): <=0x7F 时 1 字节 */
```

`per_write_length`（per.c:66-73）：length > 0x7F 时写 2 字节 `0x8000|len`（与原行为一致），
<= 0x7F 时写 1 字节。仅改慢路径 PDU 的所有 `rdp_write_header` 调用点（rdp.c:651/681/718/761、
connection.c:708），但数据 >127 的帧（Client Info、能力集、通道数据等）仍走 2 字节，行为不变。

**提交**：`fade82d`（`tools/build-freerdp.ps1` 新增 PER 长度补丁块）。

## 原因

### 根本原因

FreeRDP 的 `rdp_write_header` 注释明说 "We always encode length in two bytes, even
though we could use only one byte if length <= 0x7F. It is just easier that way"。
这虽然是合法的字节序列（`0x8050` 与 `0x50` 数学上等价），但**目标 Windows Server 的
RDP 协议解析器要求严格 PER 编码**：MCS SendData 的 userData (OCTET_STRING) 长度
<= 0x7F 必须是 1 字节。2 字节形式导致解析错位，服务器在 SEC_EXCHANGE 帧层面即拒绝。

### 为什么之前没发现

1. **踢线位置被 diag.log 误导**：`rdp_send` 前打的 Client Info hexdump 让它看似
   "Client Info 被拒"，实际 Client Info 根本没上 wire。直到 v0.1.181 的 wire dump
   （c64267.hex）才见真相：F#20 C2S SEC_EXCHANGE (95B) → F#21 S2C DPU。
2. **95/94 差异出现在所有连接**（包括成功首连 c63337），曾被当成"gdterm 的
   MCS/PER 编码常量，非 redirect 特有"而排除——但堡垒机容忍 ≠ 目标容忍。
3. **路由 split 认知晚**：直到 v0.1.181 的 `gdterm server security enc` 诊断
   （method=0x1 bastion vs method=0x2 target）才 wire-confirmed 首连/fallback 走
   堡垒机、token redirect 走目标服务器。只有 redirect 才到达目标，只有目标严格要求。

### 路由 split（wire 确认）

| 连接 | 目的地 | 服务器方法/级别 |
|------|--------|----------------|
| 首连（c63337） | 堡垒机 | method=0x1 level=1（40-bit RC4） |
| fallback（c64272） | 堡垒机 | method=0x1 level=1 |
| token redirect（c64267） | 目标服务器 | method=0x2 level=2（128-bit RC4） |

## 预防

- **wire dump 是 ground truth，diag.log 的应用层日志有盲区**：发送前打的 hexdump
  不代表帧真正发出；判定"哪一帧被拒"必须以 rdp-dump 的逐帧枚举为准。
- 对比 mstsc 黄金样本时，不仅要看 SEC 数据内容，还要看 **MCS 头部 PER 长度编码宽度**。
- 凡"某差异出现在所有连接"不能就此排除——必须先确认该差出现在哪个路由路径上
  （堡垒机 vs 目标），分辨"容忍"与"要求"。
- 排查次序：wire 帧级定位（哪一帧被拒）→ 字节级 diff → 源码审计 → 只改被拒帧相关的编码。

## 影响范围

- 所有使用 FreeRDP 2.11.7 及以下、且目标服务器严格 PER 解析的场景
- 堡垒机（NetScaler 系）容忍 2 字节不暴露；目标 Windows Server 严格要求 1 字节
- 修复后 gdterm 所有连接的 SEC_EXCHANGE 从 95B 变 94B（与 mstsc 完全一致）

## 参考

- `tmp/rdp-dump/ver181/c64267.hex`（gdterm v0.1.181 token redirect：95B SEC_EXCHANGE 后被 DPU）
- `tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex`（mstsc golden：94B SEC_EXCHANGE 成功）
- `tmp/rdp-dump/ver181/c63337.hex`（gdterm 首连：95B SEC_EXCHANGE 经堡垒机成功）
- `ver181/c64272.hex`（fallback：95B 经堡垒机成功）
- FreeRDP 源码：`rdp.c:511-539`（rdp_write_header）、`per.c:66-73`（per_write_length）
- `connection.c:708`（SEC_EXCHANGE 调 rdp_write_header）
- 修复 commit：`fade82d`