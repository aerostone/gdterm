# RDP 堡垒机 LOGOFF_BY_USER 重连踢出调查（2026-08-31）

gdterm 通过 FreeRDP 连接 NSFOCUS 堡垒机。在堡垒机登录页输入密码后，堡垒机发出
LB_LOAD_BALANCE_INFO 重定向 PDU 让客户端携带 NSFVERIFYHASH token 重连到目标服务器。
但 FreeRDP 的 token 重连总是在发送 Client Info 后约 0.1s 被服务器
`ERRINFO_LOGOFF_BY_USER` (0x1000C) 踢出，而 mstsc 的 token 重连可以正常到达目标桌面。

本目录（tools/rdp-dump-analysis/）的 Python 脚本用于解析内置抓包代理
（RdpTcpProxy，`rdp_tcp_dump` 元数据开关）生成的 wire hex dump。

## 最终结论

**堡垒机 token 恢复路径对客户端身份有静态指纹白名单校验**，识别到 FreeRDP 默认的
clientBuild=18363、空 clientDigProductId 就会在 token 恢复时拒绝。

mstsc 的身份指纹（来自黄金样本 c57179 / c57136）：

| 字段 | mstsc (黄金) | FreeRDP (默认) |
|---|---|---|
| RdpVersion | 0x0008000d | 0x0008000c |
| DesktopWidth | 2292 | 2296 |
| ClientBuild | 19041 | 18363 |
| earlyCapabilityFlags | 0x07af | 0x04a3 |
| connectionType | 7 | 6 |
| clientDigProductId | `b87232e6-fe1b-44a2-9802-02b5a49`（31 字符，MachineGuid 截断） | 全零 |
| CS_CLUSTER flags | 0x17 (V5) | 0x0f (V4) |

mstsc 身份伪装补丁已提交为 commit `f7e7426`（CI build #167 = v0.1.167 success），
运行时验证是否解决踢出仍未确认。

## 已排除的根因

| 根因 | 提交/版本 | 结果 |
|---|---|---|
| 重连携带旧凭据 (AUTOLOGON=1) | b00c29e, v0.1.150 | ❌ 包验证清除凭据后仍被踢 |
| 路由 token 丢失（/auto-reconnect 重置 nego） | aae9509, v0.1.151 | ❌ X.224 验证 token 正确携带后仍被踢 |
| X.224 CR requestedProtocols 不匹配 | 071bc31, v0.1.164 | ❌ CR 字节与 mstsc 黄金样本一致后仍被踢 |
| 会话身份指纹（CS_CORE）不匹配 | b3bba94/e412278, v0.1.163 | ❌ CS_CORE 字节与已连接的第一段一致后仍被踢 |
| 旧会话未干净关闭（MCS ultimatum） | 6002ea1, v0.1.166 | ❌ 双向 ultimatum 后仍被踢 |
| 加密套件 / CS_SEC 不匹配 | 多个补丁 | ❌ 字节一致后仍被踢 |
| 客户端 ClientInfo 内容差异 | 信息量分析 | ❌ 首段连接受影响后仍被踢 |

## 调查方法

1. **内置抓包代理 (RdpTcpProxy)**：gdterm 内置 TCP 代理（`rdp_tcp_dump` 元数据开关，
   由 工具→调试模式 控制可见性），在 `127.0.0.1` 上监听随机端口，转发到真实堡垒机，
   双向记录 C2S/S2C 原始字节到 `logs/rdp-dump/rdp-dump-{ts}-c{port}.hex`。
2. **Wire Dump 分析**：Python 工具在 `tools/rdp-dump-analysis/`，对 hex dump 做
   TPKT 分帧、X.224/MCS 解析、TS_UD 块遍历、CS_CORE/CS_SEC/CS_CLUSTER/CS_NET 字段解析。
3. **FreeRDP 源码 DIAG 补丁**：在 appveyor.yml 中对 FreeRDP 2.11.7 注入 WLog_INFO
   诊断（CR 字节、token、nego 状态、Client Info、重定向事件），以及 CS_CORE 身份
   快照/恢复补丁。
4. **字节级对比**：用 mstsc 黄金样本（成功连接）与 FreeRDP 问题连接做字节级 diff。

## 关键代码路径

### FreeRDP 首段连接（被接受的基线）

```
X.224 CR (80B: 61B NSFVERIFYHASH token + RDP_NEG_REQ proto=0x0)
→ X.224 CC (11B li=6, 无 NEG_DATA → 协商为 legacy RDP)
→ MCS Connect Initial (451B wfreerdp / 446B mstsc)
→ erectDomain, attachUser, 6 通道 join
→ SEC_EXCHANGE (95B wfreerdp / 94B mstsc, 客户端随机 RSA 加密)
→ Client Info (327B / 331B, RC4 加密)
→ 正常 RDP 会话（渲染堡垒机登录页）
→ 用户在堡垒机输入密码
→ 服务器发送 SEC_REDIRECTION_PKT (104B, flags=0x0c00, 61B LBI token)
→ 服务器 S2C MCS ultimatum
→ 客户端 C2S MCS ultimatum（patch 6002ea1 添加）
→ TCP 关闭
```

### FreeRDP token 重连（被踢）

```
X.224 CR (80B, 字节与 mstsc 黄金样本一致)
→ X.224 CC (11B li=6)
→ MCS Connect Initial (451B, 字节与首段一致)
→ erectDomain, attachUser, 6 通道 join
→ SEC_EXCHANGE (95B)
→ 服务器 S2C MCS ultimatum (02f080 21 80) ← 踢出位置
→ (Client Info 未发送)
```

### mstsc token 重连（被接受，黄金样本）

```
X.224 CR (80B, 字节与 wfreerdp 一致)
→ X.224 CC (11B li=6)
→ MCS Connect Initial (446B, CS_CORE 字段不同)
→ SEC_EXCHANGE (94B)
→ Client Info (331B, 含 license/security 交换)
→ 服务器 42B 响应 → 正常会话
```

## 工具使用

```bash
# 基础分析
python3 tools/rdp-dump-analysis/parse_rdpdump.py tmp/rdp-dump/ver166/rdp-dump-20260831-135754-c61442.hex

# CR token 原始字节
python3 tools/rdp-dump-analysis/cr_raw.py tmp/rdp-dump/rdp-dump-20260831-080357-c57179.hex

# TS_UD 块对比
python3 tools/rdp-dump-analysis/ud_parse.py

# CS_CORE 全字段对比
python3 tools/rdp-dump-analysis/core_diff.py

# Connect Initial 详细解析
python3 tools/rdp-dump-analysis/parse_ci.py
```

## 原始抓包数据

抓包 hex dump 位于 `tmp/rdp-dump/`（未纳入版本控制，因包含敏感令牌）：

| 目录/文件 | 来源 | 说明 |
|---|---|---|
| `tmp/rdp-dump/` | v0.1.161, 08:03 | 含 mstsc 黄金样本 (c57179) |
| `tmp/rdp-dump/ver165/` | v0.1.165, 13:16 | 含 CS_CORE 字节一致确认 |
| `tmp/rdp-dump/ver166/` | v0.1.166, 13:57 | 含 ultimatum 验证 + 完整 TS_UD 对比 |

## 相关提交

| 提交 | 内容 |
|---|---|
| `b00c29e` | 清除重定向重连凭据（AUTOLOGON=0） |
| `aae9509` | /auto-reconnect 补回路由 token |
| `b3bba94` / `e412278` | CS_CORE 身份快照/恢复 |
| `071bc31` | 协议锁（legacy RDP 锁定） |
| `6002ea1` | 重定向前发送 MCS ultimatum |
| `7c60755` | 会话级产物不再持久化 + 零配置自协商 |
| `5df7e0a` / `0bbd7c7` | FreeRDP WLog_INFO 诊断批量补丁 |
| `f7e7426` | **伪装 mstsc 客户端指纹（build 19041 + digProductId + RDP 10.8）** |

## 参考标准

- [MS-RDPBCGR] Remote Desktop Protocol: Basic Connectivity and Graphics Remoting
- [MS-RDPERP] Remote Desktop Protocol: Remote Programs Virtual Channel Extension
- T.124 (02/98) — Generic Conference Control
- T.125 (02/98) — Multipoint Communication Service Protocol Specification
- FreeRDP 2.11.7 source: `libfreerdp/core/`
