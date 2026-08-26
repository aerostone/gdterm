---
doc_type: decision
category: convention
date: 2026-08-26
slug: connections-json-read-write-symmetry
status: active
area: Gdterm.Connections 持久层
tags: [connections.json, 序列化, metadata, serial, 保存]
---

# connections.json 扩展字段必须读写对称

## 背景

v0.1.119 用户报告「有保存按钮但无法保存」：连接编辑里的备注（notes）、RDP 高级选项、串口参数在重启后全部丢失。

根因：`ConnectionStoreJson` 的序列化器把 `metadata`（扁平 kv）和 `serial` 写进 `connections.json`，但 `DeserializeConnections` 从不读回这两个字段——写而不读等于没存。`serial` 在 0.1.119 前更是完全没落盘。

## 决定

- `connections.json` 的每个扩展字段必须**序列化/反序列化成对实现**，新增字段时两侧同步提交。
- 反序列化侧做防御性默认值：
  - serial 缺失保持 `null`；`PortName` 兜底 `"COM1"`；`BaudRate <= 0 → 9600`；`DataBits` 越界（非 5–8）`→ 8`
  - 枚举经 `ParseEnum<T>` 解析，失败回退默认值
- metadata 为扁平 string→string kv，值内引号/反斜杠经 `Escape`/`ReadQuotedString` 成对转义还原。

## 证据

- 写入：`src/Gdterm.Connections/ConnectionStoreJson.cs:214-233`（serial + metadata）
- 读回：`:344-356`（serial 含夹取）、`:362-365`（metadata 经 `ParseFlatStringObject` :386 + `ReadQuotedString` :413）
- 回归测试：`src/Gdterm.Tests/Connections/ConnectionStoreJsonTests.cs:54-75` 断言 metadata（含引号/反斜杠转义）与 serial（COM3/115200）经磁盘 round-trip 存活。

## 考虑过的替代方案

换 System.Text.Json / Newtonsoft —— 否决：net462 绿色版维持零外部 JSON 依赖，现有手写解析器已覆盖需求且测试兜底。

## 后果与生效范围

- 新增持久化字段（如未来 SSH agent forwarding 开关）时，PR 必须同时含序列化、反序列化与 round-trip 测试三处改动。
- 生效范围：Gdterm.Connections 持久层及项目内所有新增持久化字段。
