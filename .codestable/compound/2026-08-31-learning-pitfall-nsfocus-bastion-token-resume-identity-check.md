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

## 解法（2026-09-02 状态：根因已定位为 salted checksum，修复待实测确认）

2026-09-02 的逐项排查把所有已知明文差异（X.224 CR、GCC 四块、SEC_EXCHANGE 编码、
Client Info 帧大小、CS_CLUSTER 0x17/1 vs 0x15/0、clientAddress）逐一排除后，
最终在 **Client Info 线缆 SEC 标志位** 上定位到唯一剩余判别维度：

1. **mstsc（黄金样本，成功）**：SEC flags = `0x0048`（`SEC_INFO_PKT|SEC_ENCRYPT`，无 `SEC_SECURE_CHECKSUM`）
2. **gdterm（FreeRDP，被踢）**：SEC flags = `0x0848`（多了 `SEC_SECURE_CHECKSUM` 0x0800）

根因是 FreeRDP 内部 bug：`rdp->do_secure_checksum` 首次连接置 TRUE 后，`rdp_reset`
从不重置，redirect 重连的 `rdp_client_establish_keys` 只在 `SaltedChecksum` 为 TRUE 时
设置、从不清除，导致 salted MAC 标志在重连后仍生效（详见
`compound/2026-09-02-learning-pitfall-freerdp-do-secure-checksum-persists-redirect.md`）。

**修复（commit 5544989，已推送 master）**：在 redirect 清除块（`$clearBlock`）中
`rdp_client_connect` 之前追加 `rdp->do_secure_checksum = FALSE;` 与
`settings->SaltedChecksum = FALSE;`，并在 `rdp_send_client_info` 添加 SEC flags 诊断。
CI 构建后待用户实测：若 diag.log 出现 `gdterm redirect client info sec: wire=0x0048`
且重连不再被踢，则根因确认。

> 历史上 v0.1.170 提交但未验证的四个方向（ClientBuild/digProductId 恢复位置、
> CS_CLUSTER VERSION5+0x04、rdpdr 去 ENCRYPT_RDP、BER 最小编码）已在 v0.1.171-175
> 中全部验证落地，但它们只对齐了 GCC 数据块，未触及 Client Info 的 SEC 标志位，
> 所以仍被踢——这反过来证实了 salted-checksum 才是真正的判别维度。

## 原因

本堡磐机（NSFOCUS 系列，识别特征为 `Cookie: msts=NSFVERIFYHASH=<32hex>` 格式的
路由 token）的 token 恢复路径会在 `SEC_EXCHANGE` 完成后验证客户端身份指纹与
加密参数。早期版本（v0.1.169 前）在 SEC_EXCHANGE 阶段即被踢，是因为身份指纹
不匹配（ClientBuild=18363、clientDigProductId 空、CS_CLUSTER 用 VERSION4、
rdpdr 带 ENCRYPT_RDP、BER 3 字节编码）；补丁集对齐 mstsc 的 GCC 数据后踢点
推进到 Client Info 阶段，此时判别维度变为 Client Info 的 salted MAC 标志位
（FreeRDP 的 `do_secure_checksum` 持久化 bug）。目标服务器不支持 `ENC_SALTED_CHECKSUM`，
按标准 MAC 校验无法验证 gdterm 的 salted MAC，故以 LOGOFF_BY_USER 拒绝。

## 预防

- 在设计跨堡垒机 RDP 连接方案时，预留客户端身份伪装能力
- 首次连接与重连的 CS_CORE/CS_SEC/CS_CLUSTER/CS_NET 必须一致
- 使用内置抓包代理（RdpTcpProxy）对比 mstsc 黄金样本做字节级 diff
- 前 7 个补丁方向（凭据清除、token 补回、协议锁、身份恢复、ultimatum、
  身份伪装、全链路对齐）全部打年后再测

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
- 修复 commit：`5544989`（`tools/build-freerdp.ps1` $clearBlock）