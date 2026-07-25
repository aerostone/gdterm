---
doc_type: brainstorm
title: 运维工具箱
date: 2026-07-25
status: confirmed
scope: 运维工具模块：后连接运维工具 + 网络侦察工具，内建模块方式集成到 gdterm UI
related_features: []
related_requirements: []
---

# 运维工具箱 brainstorm

## 背景与真问题

gdterm 已有 SSH/RDP/SFTP/Terminal 能力，但运维人员还需要大量"围绕机器"的辅助操作：装证书、配源、校时、扫端口、发现资产。目前这些操作要么跳出去用别的工具，要么手动 SSH 执行命令。用户希望 gdterm 成为一站式运维入口。

## 已敲定的设计点

### 1. 不做插件系统，做内建工具模块

- 每个工具一个 `Gdterm.Tools.*` 项目，实现统一 `IToolModule` 接口
- UI 工具菜单统一注册展示
- .NET 4.6.2 + WinForms 环境下，插件 DLL 动态加载器成本过高且无第三方插件需求
- 加工具 = 加项目 + 实现接口 + UI 注册，零额外基础设施

参考：Royal TS 内建插件（Rebex Terminal / PuTTY 终端都是一等公民，不走动态加载）

### 2. 工具分类

**第一梯队：后连接运维（A，必须有）**

| 工具 | 功能 | 实现方式 |
|---|---|---|
| 证书安装器 | 本地/远程（SSH 推送）安装 SSL 证书 | 本地 certutil + 远程 `cp + update-ca-certificates` |
| 时间同步 | 本地/远程 NTP 校时 | 本地 w32tm + 远程 `ntpdate/chrony` |
| 仓库配置 | 配置 yum/apt/zypper 源 | 远程 SSH 写 repo 文件 |
| 端口扫描 | 探测指定主机的开放端口 | TcpClient 异步连接扫描 |

**第二梯队：网络侦察（B，去掉 CVE）**

| 工具 | 功能 |
|---|---|
| IP 扫描 | ICMP + TCP 发现网段内活跃主机 |
| DNS 查询 | 正反向解析、A/MX/TXT/CNAME 记录 |
| Traceroute | 路由追踪 + 延迟可视化 |
| Ping 监控 | 持续监控主机可达性，报警 |
| 子网计算器 | CIDR/掩码/范围/可用主机数 |
| WHOIS 查询 | 域名/IP 归属查询 |

**第三梯队：暂不做**

- SNMP Walk（需 MIB 库，重）
- LLDP/CDP 发现（需原始套接字）
- CVE 检查（用户明确去掉）
- X11 Server（Win7 上不现实）

### 3. 所有工具支持配置文件定制

- 证书安装器：可配置证书存储路径、信任链策略、远程安装脚本模板
- 时间同步：可配置 NTP 服务器列表（公网/内网）、同步间隔、偏移阈值
- 仓库配置：可配置源 URL 列表、GPG key 路径、目标路径模板
- 端口扫描：可配置端口范围、超时、并发数
- IP 扫描：可配置子网范围、扫描方式、排除列表
- 所有配置存放在 `data/config/tools/` 目录下，JSON 格式
- 工具自带合理默认值，配置文件可选

### 4. 远程执行模式

后连接运维工具（证书/时间/仓库）通过已建立的 SSH 会话在目标机器上执行命令，不是本地操作。

- 证书：先传文件到远程，再执行远程安装命令
- 时间：在远程执行 `ntpdate` 或 `chronyc makestep`
- 仓库：在远程写 `/etc/yum.repos.d/` 或 `/etc/apt/sources.list.d/`

## 开放问题

- 端口扫描和 IP 扫描是否需要结果持久化（导出 CSV/JSON）？
- 工具执行结果是否需要审计日志（接入 IAuditLogger）？
- 是否需要工具执行历史记录？

## 下一步

移交 `cs-roadmap` 拆解为子 feature，接口契约在 roadmap 层定义。
