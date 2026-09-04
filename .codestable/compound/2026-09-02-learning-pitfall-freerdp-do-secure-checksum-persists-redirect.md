---
doc_type: learning
track: pitfall
date: 2026-09-02
slug: freerdp-do-secure-checksum-persists-redirect
component: FreeRDP 2.11.7 libfreerdp/core 重连状态机
tags: [rdp, freerdp, redirect, reconnect, salted-checksum, sec_secure_checksum, do_secure_checksum, bug]
status: superseded
superseded-by: 2026-09-04-learning-pitfall-freerdp-per-length-encoding-sec-exchange
---

> **2026-09-04：本文结论已被否定并标记 superseded。** v0.1.178 实测证明清除
> `do_secure_checksum`+`SaltedChecksum` 后 Client Info 线缆 SEC flags=0x0048 与 mstsc
> 完全一致，**仍被 LOGOFF_BY_USER 踢线**。这不是 LOGOFF_BY_USER 的根因。
>
> 真正根因请见 `compound/2026-09-04-learning-pitfall-freerdp-per-length-encoding-sec-exchange.md`：
> **MCS SendData userData PER 长度编码**（FreeRDP 恒 2 字节使 SEC_EXCHANGE 95B，
> mstsc 1 字节 94B，目标服务器在 SEC_EXCHANGE 后即时 DPU）。
>
> 本文保留的**有价值硬事实**：FreeRDP `rdp->do_secure_checksum` 确实在 redirect 重连时
> 持久化 TRUE、确实让 Client Info 发 salted MAC（0x0848）；但该差异不影响踢线判定，
> 因为收到 0x0048 的实机仍被踢。这个状态机 bug 依然存在、依然影响线缆行为，
> 只是不是本次 LOGOFF_BY_USER 的成因。保留上述内容供参考。

# FreeRDP `rdp->do_secure_checksum` 在 redirect 重连时持久化 TRUE 导致 SEC_SECURE_CHECKSUM 误发

## 问题

FreeRDP 2.11.7 的 `rdp->do_secure_checksum` 在首次连接时被设为 TRUE（因 `SaltedChecksum` 默认 TRUE），
但在 `rdp_reset`（redirect 重连时调用）中**从不重置**。`rdp_client_establish_keys` 只在
`settings->SaltedChecksum` 为 TRUE 时**设置**该标志，从不清除。因此，当首次连接的能力协商
将 `settings->SaltedChecksum` 清为 FALSE 后，redirect 重连的 `SEC_EXCHANGE` 执行时
`if (SaltedChecksum)` 跳过，`do_secure_checksum` 保留 TRUE 值不变。

结果：redirect 重连的 `rdp_send_stream_init` 在 `rdp_security_stream_init`（rdp.c:273-275）中
无条件添加 `SEC_SECURE_CHECKSUM`（0x0800）到 `rdp->sec_flags`，使得 Client Info 的线缆
SEC flags 为 `0x0848 = SEC_INFO_PKT|SEC_ENCRYPT|SEC_SECURE_CHECKSUM`。

而 mstsc 的 Client Info 线缆标志为 `0x0048 = SEC_INFO_PKT|SEC_ENCRYPT`（无
`SEC_SECURE_CHECKSUM`）。目标服务器若不支持 `ENC_SALTED_CHECKSUM`（龙渊能力集），
使用标准 MAC 校验，与 gdterm 的 salted MAC 不匹配，在 Client Info 发送后约 52ms
以 `ERRINFO_LOGOFF_BY_USER` 踢出。

## 症状

- ForwRDP 进程内 redirect 重连（`rdp_client_redirect`）发送 Client Info 后约 52ms 被踢
- 诊断日志出现 `ERRINFO_LOGOFF_BY_USER`（0x1000C）
- 抓包显示 gdterm Client Info 线缆 SEC flags = 0x0848，mstsc 黄金样本 = 0x0048
- 仅影响 redirect 重连路径（`rdp_client_redirect`），不影响首次连接。
- 若首次连接的能力协商未发生（early redirect），则 `SaltedChecksum` 仍为默认 TRUE，
  该问题不会触发（但 redirect 发生在能力协商之后，所以总会触发）。

## 没用的做法

修复前尝试了以下方案均无效，因为它们都未触及 `do_secure_checksum` 标志：

| 尝试 | 版本 | 为什么无效 |
|------|------|-----------|
| 清除重连凭据 | v0.1.150 | 不影响 MAC 计算 |
| 锁定协议到 legacy RDP | v0.1.164 | 不影响 SEC 标志位 |
| 身份伪装（CS_CORE 全字段） | v0.1.169 | 不影响 SEC 标志位 |
| CS_CLUSTER flags 修复 | v0.1.175 | 不影响 SEC 标志位 |
| GCC 数据块逐字节对齐 | v0.1.175 | GCC 字节一致后仍被踢 |

## 解法

在 `rdp_client_redirect` 的清除块（`$clearBlock`，位于 `rdp_client_connect` 之前）中
添加：

```c
/* 清除持久化的 salted-checksum 标志 */
rdp->do_secure_checksum = FALSE;
settings->SaltedChecksum = FALSE;
```

**原理**：`rdp->do_secure_checksum` 控制 `rdp_security_stream_init` 是否在 `sec_flags` 中
添加 `SEC_SECURE_CHECKSUM`（0x0800）。设为 FALSE 后，Client Info 的线缆 SEC flags 变为
`0x0048`，与 mstsc 一致。`settings->SaltedChecksum = FALSE` 为双重保险，确保
`rdp_client_establish_keys` 的条件检查不会重新置位。

**同时添加诊断**：

```c
WLog_INFO(TAG, "gdterm redirect: clear do_secure_checksum=%d do_crypt=%d SaltedChecksum=%d",
    (int)rdp->do_secure_checksum, (int)rdp->do_crypt, (int)settings->SaltedChecksum);
```

用于确认修复前的持久化 TRUE 状态。预期输出：`do_secure_checksum=1 SaltedChecksum=0`
（修复前 do_secure_checksum 仍为 1，SaltedChecksum 已为 0）。

**参考实现**：commit `5544989`，`tools/build-freerdp.ps1` 的 `$clearBlock` 末尾。
另在 `rdp_send_client_info`（info.c）中添加 SEC flags 诊断：

```c
WLog_INFO(TAG, "gdterm redirect client info sec: wire=0x%04x do_crypt=%d do_secure_checksum=%d SaltedChecksum=%d clientAddr=%s",
    (SEC_INFO_PKT | (rdp->do_crypt ? SEC_ENCRYPT : 0) | (rdp->do_secure_checksum ? SEC_SECURE_CHECKSUM : 0)),
    (int)rdp->do_crypt, (int)rdp->do_secure_checksum, (int)rdp->settings->SaltedChecksum,
    rdp->settings->ClientAddress ? rdp->settings->ClientAddress : "(null)");
```

## 原因

### 根本原因

FreeRDP 的 `rdp_reset` 函数（rdp.c:1869）在重新初始化连接状态时**不负责任何
`rdpRdp` 结构体层面的布尔标志**。它只释放和重建底层对象（transport/mcs/nego/license/fastpath），
不清除 `do_crypt`、`do_secure_checksum`、`sec_flags` 等字段。

`rdp_client_establish_keys`（connection.c:691-694）的设计也有问题——它只条件性地
**设置** `do_secure_checksum`（当 `SaltedChecksum` 为 TRUE 时），但从不主动清除它。
因此一旦设过，除非 `rdp_new` 重新 calloc 整个结构体，永远不会回到 FALSE。

### 状态流

```
首次连接:
  rdp_new (calloc)
    → do_secure_checksum = FALSE (calloc 零初始化)
    → SaltedChecksum = TRUE (settings.c:337 默认)
  → rdp_client_establish_keys (connection.c:693)
    → do_crypt = TRUE
    → if (SaltedChecksum = TRUE) do_secure_checksum = TRUE
  → Client Info SEC flags = 0x0040 | 0x0008 | 0x0800 = 0x0848 ✓
  → 能力协商 (capabilities.c:191)
    → SaltedChecksum = FALSE (服务器无 ENC_SALTED_CHECKSUM)

redirect 重连:
  rdp_client_disconnect_and_clear → rdp_reset (rdp.c:1869)
    → 只清 transport/mcs/nego/license/fastpath，不清 do_secure_checksum
  → rdp_client_establish_keys (connection.c:693)
    → do_crypt = TRUE
    → if (SaltedChecksum = FALSE) 跳过 ← 不清除 do_secure_checksum!
    → do_secure_checksum 仍是 TRUE ❌
  → Client Info SEC flags = 0x0040 | 0x0008 | 0x0800 = 0x0848 ❌
  → 目标服务器：不支持 salted MAC，标准 MAC 校验失败
  → 52ms 后 LOGOFF_BY_USER
```

### Wire 数据

| 客户端 | 连接类型 | Client Info 线缆 SEC flags | 信息包明文大小 | 是否成功 |
|--------|---------|--------------------------|---------------|---------|
| mstsc | 首连 (c57136) | 0x0048 (无 SECURE_CHECKSUM) | 304 字节 | 成功 |
| mstsc | 黄金重连 (c57179) | 0x0048 | 304 字节 | 成功 |
| gdterm (FreeRDP) | 首连 (c57024) | 0x0848 (有 SECURE_CHECKSUM) | 300 字节 | 成功（堡垒机接受） |
| gdterm | redirect 重连 | 0x0848（预测，修复前） | 300 字节 | 被踢（目标服务器拒绝） |

注意：gfdterm 与 mstsc 的 4 字节信息包明文差异（300 vs 304）独立于 SEC 标志位，
但该差异不影响踢出判断（若修复成功则两个差异均解决）。

## 预防

- 任何在 `rdp_client_establish_keys` 或类似函数中条件性设置的 `rdpRdp` 状态标志，
  若在 `rdp_reset` 中未明确清除，则必须在 `rdp_client_redirect` 和 `rdp_client_reconnect`
  的清除块中手动清除。
- 线缆分析时，SEC 标志位（特别是 `SEC_SECURE_CHECKSUM` 0x0800）是重要的诊断维度，
  不应仅关注信息包内容。
- 使用 `rdp-dump` 抓包对比 mstsc 黄金样本时，应检查 Client Info 线缆帧的 SEC 标志位
  （帧偏移 15-18 字节），而不仅是信息包内容。

## 影响范围

- 适用于所有使用 FreeRDP 2.11.7 及以下版本、且通过 `rdp_client_redirect` 进行
  进程内重定向重连的场景。
- 不适用于首次连接、`rdp_client_reconnect`（/auto-reconnect 路径），以及
  NLA/TLS 安全协议（无 SEC 层，不使用 MAC 校验）。
- 本 bug 仅影响 legacy RDP 安全协议（`UseRdpSecurityLayer=TRUE`，`PROTOCOL_RDP`）。

## 参考

- `settings.c:337` — `SaltedChecksum = TRUE` 默认值
- `connection.c:693-694` — `do_secure_checksum = TRUE` 条件设置
- `capabilities.c:191-192` — `SaltedChecksum = FALSE` 清除
- `rdp.c:273-275` — `rdp_security_stream_init` 添加 `SEC_SECURE_CHECKSUM`
- `rdp.c:584-588` — `security_salted_mac_signature` vs `security_mac_signature` 分支
- `rdp.c:1869` — `rdp_reset` 函数，不重置 `do_secure_checksum`
- `rdp.h:69` — `SEC_SECURE_CHECKSUM 0x0800` 定义
- `tools/build-freerdp.ps1` — `$clearBlock` 修复位置
- Wire 数据：`tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex`（mstsc 黄金重连）
- Wire 数据：`tmp/rdp-dump/rdp-dump-20260831-094735-c57024.hex`（gdterm 首连）