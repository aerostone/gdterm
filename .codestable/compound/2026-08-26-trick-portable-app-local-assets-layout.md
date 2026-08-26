---
doc_type: trick
type: pattern
date: 2026-08-26
slug: portable-app-local-assets-layout
topic: 绿色便携应用的日志/第三方工具落位约定
framework: .NET Framework 4.6.2 WinForms
tags: [绿色版, 便携, vendor, logs, PATH]
status: active
---

# 便携应用本地资产布局约定

## 适用场景

gdterm 绿色版（U盘便携、免安装、单文件夹分发）。

## 做法

运行时可写数据与第三方工具一律放**程序根目录相对路径**，不用用户目录：

| 资产 | 位置 | 依据 |
|---|---|---|
| 日志 | `BaseDirectory\logs\`（diag.log / commands\ / terminal\） | `src/Gdterm.UI/Program.cs:26-28` |
| CLI 工具 | `vendor\{fzf,fd,freerdp}\`，启动时追加到 PATH 末尾 | Program.cs 启动逻辑 |
| CI 钉扎 | fzf/fd 按哈希固定版本 | `appveyor.yml:182-183` |
| FreeRDP 探测顺序 | `vendor\freerdp\` > `freerdp-bin\`（CI 源码构建产物） | `appveyor.yml:215-219` |

## 为什么有效

单文件夹拷贝即完整迁移，卸载无残留；用户明确拍板 logs 回主目录（拒绝 %APPDATA%）。

## 何时不适用

多机漫游配置场景（配置需跟随用户账号时才用 `%APPDATA%`）；本项目明确不做。
