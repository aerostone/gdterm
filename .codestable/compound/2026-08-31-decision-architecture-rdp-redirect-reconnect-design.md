---
doc_type: decision
category: architecture
date: 2026-08-31
slug: rdp-redirect-reconnect-design
status: active
area: FreeRDP 重定向重连路径（libfreerdp/core/connection.c + C# FreeRdpClient.cs）
tags: [rdp, redirect, reconnect, credential, identity, protocol-lock, architecture]
---

# FreeRDP 重定向重连架构设计

## 背景

gdterm 通过 FreeRDP 2.11.7 连接 NSFOCUS 堡垒机时，堡垒机在用户完成登录后发出
`LB_LOAD_BALANCE_INFO` 重定向 PDU，要求客户端携带 `NSFVERIFYHASH` 路由 token
重连到目标服务器。但 FreeRDP 的 token 重连总是被踢出。

## 决定

### 设计原则

1. **零配置自协商**：用户不需要手动设置任何选项，gdterm 自动做 mstsc 兼容
2. **首段连接保持凭据**：首段连接保持 keepass 自动登录（AUTOLOGON=1），让用户尽快
   看到堡垒机登录页
3. **重定向重连自协商**：重连时清除凭据、恢复身份、锁定协议，让目标服务器呈现其登录页
4. **补丁只影响重定向路径**：不修改 FreeRDP 首段连接或 /auto-reconnect 路径的行为

### 架构组件

#### 1. 凭据清除（connection.c:rdp_client_redirect）

在 `rdp_client_redirect` 中，在 `rdp_client_connect` 之前：

- 清除 `settings->Username`（除非 `LB_USERNAME` 标志）
- 清除 `settings->Domain`（除非 `LB_DOMAIN` 标志）
- 清除 `settings->Password`（除非 `LB_PASSWORD` 标志）
- 设置 `AutoLogonEnabled = FALSE`

**原理**：MS-RDPBCGR 2.2.13.1 规定重定向 PDU 中带有 `LB_*` 标志的字段才是
服务器授权的目标凭据。不携带 `LB_PASSWORD` 标志时，密码应被清除，让重连
自行协商。

#### 2. 身份快照/恢复（connection.c:rdp_client_connect / rdp_client_redirect）

在首段 `rdp_client_connect` 入口（`RedirectionFlags == 0`）快照以下字段：

- `RdpVersion`
- `DesktopWidth` / `DesktopHeight`
- `ColorDepth`
- `EncryptionMethods`

在 `rdp_client_redirect` 的 `rdp_client_connect` 前恢复。

**原因**：`rdp_reset` 释放了 server 证书但不会重置这些字段，但 `gcc_read_server_core_data`
和 `gcc_read_server_secuirty_data` 会覆盖它们（将 RdpVersion 从 10.7 降级为 5+、
将 EncryptionMethods 从 0x1b 缩小为 0x01）。

#### 3. 身份伪装（connection.c + gcc.c）

在重连前重设以下身份字段，使其匹配 mstsc 的指纹：

- `ClientBuild = 19041`（Win10 20H1）
- `ClientProductId = "b87232e6-fe1b-44a2-9802-02b5a49"`（31 字符 MachineGuid 截断）
- `RdpVersion = 0x0008000D`（RDP 10.8）
- `ConnectionType = 7`（CONNECTION_TYPE_AUTODETECT）
- `SupportStatusInfoPdu = TRUE`
- `SupportGraphicsPipeline = TRUE`
- `SupportDynamicTimeZone = TRUE`
- `earlyCapabilityFlags |= 0x0008`（STRONG_ASYMMETRIC_KEYS）
- `CS_CLUSTER flags = VERSION5 + 0x04`（REDIRECTED_VERSION）
- `rdpdr 通道选项`去掉 `ENCRYPT_RDP` 位
- `ber_write_integer` 对 0x8000-0xFFFF 用 2 字节最小编码

**原因**：NSFOCUS 堡垒机的 token 恢复路径对客户端身份有静态指纹白名单校验。

#### 4. 协议锁（connection.c + nego.c）

当 `SelectedProtocol == PROTOCOL_RDP` 时，在重连前：

- 设置 `RdpSecurity = TRUE`
- 设置 `TlsSecurity = NlaSecurity = ExtSecurity = FALSE`
- 在 `nego.c:907` 的 `sendNegoData` 条件上增加 `|| (nego->RoutingTokenLength > 0)`
  确保 token 重连的 X.224 CR 写入 8 字节 RDP_NEG_REQ

**原因**：FreeRDP 的 `nego_init` 重新计算 `RequestedProtocols`，如果连接协商为
legacy RDP，重连必须锁定在 legacy RDP 上，否则会重新请求 0x3（NLA|TLS）导致协议不匹配。

#### 5. MCS ultimatum（connection.c:rdp_client_redirect）

在 `rdp_client_disconnect_and_clear` 前发送 MCS Disconnect-Provider-Ultimatum：

```c
mcs_send_disconnect_provider_ultimatum(rdp->mcs);
```

**原因**：mstsc 在重定向前先发送 C2S ultimatum 干净关闭旧会话。FreeRDP 的默认
行为是静默断开，可能让堡垒机认为旧会话仍在 active 状态。

### 6. 会话级产物不持久化（C# FreeRdpClient.cs）

- 自动捕获的 `rdp_loadbalance` 和 `rdp_negotiated_proto` 不写入 metadata
- 只在 `CurrentOptions` 和 `_negotiatedProtocol` 中存活（同实例重启保持）
- 用户手动填写的 `rdp_loadbalance`（ConnectionDialog 的 负载均衡 TextBox）仍然持久化

**原因**：NSFVERIFYHASH 路由 token 由堡垒机每连接重定向 PDU 签发，session 销毁后
token 即失效。持久化死 token 只会让重连被踢。

### 7. LB probe 进程级禁用

- 首次 `cc:no-token` 后，进程级别的 `_lbProbeDisabled` 设为 true
- 后续连接跳过 probe

**原因**：该堡垒机不在 Connection Confirm 中发放路由 token（只在重定向 PDU 中发），
所以每次 probe 都是无用的额外连接，且会触发堡垒机速率限制。

## 考虑过的替代方案

| 替代方案 | 否决原因 |
|---------|---------|
| 在 C# 层每次重连用 `/sec:rdp` 参数 | 无法控制 FreeRDP 内部重定向重连的参数 |
| 用 mstscax ActiveX 替代 FreeRDP | 失去嵌入能力、无法控制通道和日志 |
| 重连时用新进程启动 wfreerdp | 需要完全重写协议栈对接 |
| 不做任何修改，直接 retry 绕过 | 用户否决：mstsc 不踢，gdterm 就不该踢 |

## 后果

- 重定向重连路径完全改写（6 个 FreeRDP 源文件补丁 + C# 协调）
- 首段连接行为不变（keepass 自动登录正常）
- 非 LB 重定向（到不同目标）保留凭据，不受影响
- 增加的 WLog_INFO 诊断：`gdterm redirect:`、`gdterm redirect nego done:`、
  `gdterm identity:`、`gdterm restore:`、`gdterm cluster:`
- CI 构建时间增加约 5 分钟（FreeRDP 源码编译）

## 生效范围

- 生效分支：`origin/master` 所有版本的 FreeRDP 构建
- 约束：仅在 `LB_LOAD_BALANCE_INFO` 重定向场景下触发
- 不适用：非 LB 重定向、/auto-reconnect 路径、首段连接

## 参考

- 补丁文件：`appveyor.yml` 的 `install:` 阶段 16 个 FreeRDP 补丁段
- 调查报告：`docs/RDP-REDIRECT-INVESTIGATION.md`
- Wire 差异：`compound/2026-08-31-learning-knowledge-wire-differences-wfreerdp-mstsc-redirect-reconnect.md`
- 抓包工具：`tools/rdp-dump-analysis/`