---
doc_type: learning
track: pitfall
date: 2026-08-31
slug: nsfocus-bastion-token-resume-identity-check
component: FreeRDP/gdterm RDP 连接
tags: [rdp, bastion, nsfocus, redirect, token-resume, logoff]
status: active
---

# NSFOCUS 堡垒机 token 恢复路径对客户端身份有静态指纹白名单校验

> **2026-09-04 根因重定位（推翻 09-02 定稿）**：09-02 的 "salted-checksum" 结论已被
> **v0.1.178 实测否定**（wire=0x0048 对齐 mstsc 后仍被踢）。真正的判别维度是
> **MCS SendData 的 userData PER 长度编码**：FreeRDP 恒写 2 字节（`0x8050`）使
> SEC_EXCHANGE 帧 95B，mstsc 写 1 字节（`0x50`）94B；目标 Windows Server 严格要求
> 1 字节，在 SEC_EXCHANGE 后立即 DPU 踢线（Client Info 从未上 wire，之前 "Client Info
> 后 52ms 被拒" 是发送前 hexdump 的误导）。完整分析见
> `compound/2026-09-04-learning-pitfall-freerdp-per-length-encoding-sec-exchange.md`。
> 修复 commit `fade82d`（rdp_write_header 改 per_write_length），待实测确认。
> 原 salted-checksum 文档已标 superseded：
> `compound/2026-09-02-learning-pitfall-freerdp-do-secure-checksum-persists-redirect.md`。

## 问题

gdterm 通过 FreeRDP 2.11.7 连接 NSFOCUS 堡垒机时，在堡垒机登录页输入密码后，
堡垒机发出 `LB_LOAD_BALANCE_INFO` 重定向 PDU，携带 `NSFVERIFYHASH` 格式的
`Cookie: msts=NSFVERIFYHASH=<32hex>` 路由 token。FreeRDP 按标准流程以该 token
重连到目标服务器，但总是在发送 Client Info 后约 0.1s 被服务器 `ERRINFO_LOGOFF_BY_USER`
(0x1000C) 踢出。mstsc 同样的 token 重连可以正常到达目标桌面。

> **2026-09-02 更新（根因定位）**：经逐字节比对与 FreeRDP 源码审计，最终把
> LOGOFF_BY_USER 的判别维度缩小到 **Client Info 线缆 SEC 标志位**：gdterm 携带
> `SEC_SECURE_CHECKSUM`(0x0848)，mstsc 不携带(0x0048)，根因是 FreeRDP 内部
> `rdp->do_secure_checksum` 在 redirect 重连时持久化 TRUE。完整分析与修复见
> `compound/2026-09-02-learning-pitfall-freerdp-do-secure-checksum-persists-redirect.md`。
> 修复（commit 5544989）已推送 master，CI 构建后待用户实测确认。

## 症状

- 堡垒机登录页正常渲染，输入密码后返回登录页（而非目标桌面）
- 诊断日志出现 `ERRINFO_LOGOFF_BY_USER` 0x1000C
- 后续 tokenless /auto-reconnect 重试直接渲染登录页（绕过踢出判断）
- 抓包显示：早期版本（v0.1.169 前）服务器在 token 重连的 `SEC_EXCHANGE` 后直接
  发送 `MCS Disconnect-Provider-Ultimatum` (reason 0x80)；v0.1.174 起（身份伪装+补丁集
  生效后）踢点推进到 Client Info 发送后 ~52ms（目标服务器以 LOGOFF_BY_USER 拒绝）

## 没用的做法

| 尝试 | 版本 | 容器 |
|------|------|------|
| 清除重连凭据（AUTOLOGON=0） | v0.1.150 | 包验证清除后仍被踢 |
| 补回路由 token 到 /auto-reconnect | v0.1.151 | 仍被踢 |
| 锁协议到 legacy RDP（proto=0x0） | v0.1.164 | X.224 CR 字节与 mstsc 一致后仍被踢 |
| 会话身份快照/恢复（CS_CORE） | v0.1.163 | 字节一致后仍被踢 |
| 重定向前发送 MCS ultimatum | v0.1.166 | 双向 ultimatum 后仍被踢 |
| 伪装为 mstsc 身份指纹 | v0.1.167 | 仍被踢（因 spoof 部分被覆盖） |
| 完整 mstsc 身份指纹（earlyCaps=0x07af, build=19041, connType=7, RdpVersion=0x8000d） | v0.1.169 | 仍被踢（FALSE 否定） |

## 解法（2026-09-04 状态：根因重定位为 PER 长度编码，修复待实测确认）

2026-09-02 的 salted-checksum 结论已被 v0.1.178 实测否定（清除 do_secure_checksum +
SaltedChecksum 后 wire=0x0048 与 mstsc 一致，仍被踢约 55ms 后）。完整排雷记录：
GCC 四块逐字节对齐、SEC_EXCHANGE 内容逐字节一致、CS_CLUSTER 0x15/0 与 0x17/1 都试、
clientAddress=真实 IP 192.168.3.103、salted-checksum 清除——**全部无效**。

v0.1.181 的 wire dump（c64267.hex）第一次逐帧定位：

1. **F#20 C2S SEC_EXCHANGE (95B)** 后立即 **F#21 S2C DisconnectProviderUltimatum**
   （9B：`03 00 00 09 02 f0 80 21 80`，reason=0x80）
2. **Client Info 帧从未上 wire**——diag.log 里的 Client Info hexdump 是 `rdp_send`
   发送前打的（误导性）
3. mstsc golden（c57179）94B SEC_EXCHANGE 被接受，继续 Client Info（331B）→ 42B → 453B
   → 完整会话

判别维度 = **MCS SendData userData PER 长度编码宽度**：

| 客户端 | PER 长度 | SEC_EXCHANGE 帧 | 目标服务器 | 堡垒机 |
|--------|---------|----------------|-----------|--------|
| mstsc | `0x50`（1 字节，=80） | 94B | 接受 | 接受 |
| gdterm | `0x8050`（2 字节，=80\|0x8000） | 95B | DPU 踢线 | 接受 |

**修复（commit fade82d，已推送 master）**：`rdp_write_header` 用 `per_write_length`
替代无条件 2 字节编码，payload <= 0x7F 时写 1 字节并同步减 TPKT 长度。CI 构建后
待实测：若 redirect 的 rdp-dump 出现 94B SEC_EXCHANGE 且越过 DPU 到达 Client Info，
则根因确认。

## 原因

本堡磐机（NSFOCUS 系列，识别特征为 `Cookie: msts=NSFVERIFYHASH=<32hex>` 格式的
路由 token）的 token 恢复路径在 `SEC_EXCHANGE` 帧后验证客户端编码。
早期版本（v0.1.169 前）在 SEC_EXCHANGE 阶段被踢，是因为身份指纹不匹配
（ClientBuild=18363、clientDigProductId 空、CS_CLUSTER 用 VERSION4、rdpdr 带
ENCRYPT_RDP、BER 3 字节编码）；补丁集对齐 mstsc 的 GCC 数据后踢点仍停留在
SEC_EXCHANGE —— v0.1.178/181 实测证明：即使 Client Info 的 SEC 标志位已对齐
（0x0048）、GCC 四块逐字节一致、clientAddress 真实 IP，目标 Windows Server 仍因
MCS userData PER 长度 2 字节编码（SEC_EXCHANGE 95B）不满足严格 PER 解析而在
SEC_EXCHANGE 后立即 DPU 踢线。

## 预防

- 在设计跨堡垒机 RDP 连接方案时，预留客户端身份伪装能力
- 首次连接与重连的 CS_CORE/CS_SEC/CS_CLUSTER/CS_NET 必须一致
- 使用内置抓包代理（RdpTcpProxy）对比 mstsc 黄金样本做字节级 diff
- **判定被拒帧必须以 wire dump 逐帧枚举为准**：diag.log 的发送前 hexdump 有误导性
- **某差异出现在所有连接不能排除**：先确认该差异落在堡垒机还是目标路径，
  分辨"容忍"与"严格要求"（堡垒机容忍 2 字节 PER，目标要求 1 字节）

## 证据

- 抓包黄金样本：`tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex`（mstsc 成功 token 重连）
  - Client Info 帧（offset=712 len=331）SEC flags = 0x0048
- 抓包问题样本：`tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61501.hex`（wfreerdp 被踢）
- gdterm 首连样本：`tmp/rdp-dump/rdp-dump-20260831-094735-c57024.hex`
  - Client Info 帧 SEC flags = 0x0848（有 SEC_SECURE_CHECKSUM）
- 诊断日志：v0.1.175.0 运行日志（GCC 四块与首连逐字节一致仍被踢；LOGOFF_BY_USER 在
  Client Info 后 52ms）
- 分析脚本：`tools/rdp-dump-analysis/`（cs_core.py 权威 TS_UD/CS_CORE 解析器）
- 调查报告：`docs/RDP-REDIRECT-INVESTIGATION.md`
- 根因文档：`compound/2026-09-02-learning-pitfall-freerdp-do-secure-checksum-persists-redirect.md`
- 修复 commit：`5544989`（salted-checksum 清除，**已否定**）→ `fade82d`（PER 长度编码，当前候选）
- 根因文档：`compound/2026-09-04-learning-pitfall-freerdp-per-length-encoding-sec-exchange.md`