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

## 症状

- 堡垒机登录页正常渲染，输入密码后返回登录页（而非目标桌面）
- 诊断日志出现 `ERRINFO_LOGOFF_BY_USER` 0x1000C
- 后续 tokenless /auto-reconnect 重试直接渲染登录页（绕过踢出判断）
- 抓包显示：服务器在 token 重连的 `SEC_EXCHANGE` 后直接发送 `MCS Disconnect-Provider-Ultimatum` (reason 0x80)

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

## 解法（待验证的完整修复方向）

本 pitfall 记录的是**已知的** token 恢复路径身份校验行为，但全体 mstsc 身份指纹伪装
（v0.1.169 已验证到 `earlyCaps=0x07af` `build=19041` `connType=7` `RdpVersion=0x8000d`）
仍被踢出，说明该堡磐机在 token 恢复路径上还有额外检查维度。

已知的剩余明文差异（v0.1.170 已提交但未验证）：

1. **ClientBuild / digProductId 恢复位置**：快照设置但被 `gcc_read_client_core_data`
   覆盖，需在恢复块中重设
2. **CS_CLUSTER flags**：`REDIRECTION_VERSION4`（0x0d）→ `VERSION5` + 0x04（0x15）
3. **rdpdr 通道选项**：去掉 `CHANNEL_OPTION_ENCRYPT_RDP` 位（0x80800000）
4. **BER 整数编码最小化**：域参 BER 从 3 字节降到 2 字节（446B 对齐 mstsc）

## 原因

本堡磐机（NSFOCUS 系列，识别特征为 `Cookie: msts=NSFVERIFYHASH=<32hex>` 格式的
路由 token）的 token 恢复路径会在 `SEC_EXCHANGE` 完成前验证客户端身份指纹。
只有当客户端身份（ClientBuild、clientDigProductId、earlyCapabilityFlags、
connectionType、RdpVersion、CS_CLUSTER flags、rdpdr 通道选项、BER 编码）
与已保存的首段连接指纹一致时，才能通过 token 恢复。

## 预防

- 在设计跨堡垒机 RDP 连接方案时，预留客户端身份伪装能力
- 首次连接与重连的 CS_CORE/CS_SEC/CS_CLUSTER/CS_NET 必须一致
- 使用内置抓包代理（RdpTcpProxy）对比 mstsc 黄金样本做字节级 diff
- 前 7 个补丁方向（凭据清除、token 补回、协议锁、身份恢复、ultimatum、
  身份伪装、全链路对齐）全部打年后再测

## 证据

- 抓包黄金样本：`tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex`（mstsc 成功 token 重连）
- 抓包问题样本：`tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61501.hex`（wfreerdp 被踢）
- 诊断日志：v0.1.169.0 运行日志（`gdterm identity: earlyCaps=0x07af build=19041 connType=7` 确认伪装生效，但仍被踢）
- 分析脚本：`tools/rdp-dump-analysis/`（cs_core.py 权威 TS_UD/CS_CORE 解析器）
- 调查报告：`docs/RDP-REDIRECT-INVESTIGATION.md`