# gdterm 终端引擎选型：True Color + TUI（tmux / codex / opencode）

**日期**: 2026-07-26  
**范围**: 本项目内实现，非旁路工具  
**硬约束**:

1. **快速落地** — 优先接入 SDK / 成熟模拟器，避免从零写完整 xterm  
2. **绿色便携** — 单文件夹部署；无安装器；Win7 / Server 2008 + **.NET Framework 4.6.2**  
3. **低内存** — 常驻目标 **30–80MB**（15 纯 SSH 标签场景）；无 GPU 持续渲染；暂停标签零 CPU  

---

## 1. 现状（为何不行）

| 层 | 现状 | 对 TUI 的影响 |
|----|------|----------------|
| PTY | `CreateShellStream("xterm-256color", …)` | 只宣告 256 色；无 `COLORTERM=truecolor` |
| 解析 | `LightweightRenderer` 正则只处理 SGR `m` | 丢弃光标/清屏/alt-screen/鼠标等 CSI |
| 颜色 | 仅前景 16 色（30–37 / 90–97） | 无 256 / true color / 背景色 |
| 缓冲 | **行文本缓冲**（非 cell grid） | 全屏 TUI 必花屏 |
| 输入 | 基本键位 | 无完整鼠标协议 / 部分特殊键 |
| 渲染 | GDI+ Panel，16ms 节流，Pause 停表 | 内存友好，但语义不够 |

结论：当前是运维向「彩色 shell 日志」，**不是** VT 终端。

---

## 2. 候选库对比（检索结果）

### A. VtNetCore（推荐首选 SDK）

| 项 | 内容 |
|----|------|
| 仓库 | https://github.com/darrenstarr/VtNetCore |
| NuGet | `VtNetCore` 1.0.30 |
| 目标框架 | **.NET Framework 4.5 / 4.6** + netstandard1.3 / 2.0 |
| 许可 | MIT |
| 能力 | VT100/xterm 状态机、scrollback、alt buffer、鼠标、byobu/mc/vim 级兼容；作者明确用 **SSH.NET** 联调 |
| 依赖 | 纯托管，无 native DLL |
| 维护 | 2018 主开发期，之后低活跃；但功能面已够用，仍是 **唯一明确 net46 友好** 的完整引擎 |
| 绿色 | NuGet DLL 可 vendoring 进 `lib/`（与 AxMsTscLib 同模式） |
| 内存 | cell buffer + scrollback；需 **硬顶 scrollback**（建议默认 300–1000）+ 非活动标签不持大 history |
| 真彩 | 文档称颜色接近 XTerm；实现含 256/扩展色路径（接入后以 vttest / `echo -e $'\\e[38;2;…m'` 实测） |

**适配方式**：引擎 headless → 本项目保留 GDI `IRenderer` 只做 **cell→像素**；`ITerminalSession` 仍走 SSH.NET `ShellStream`。

### B. XTerm.NET（能力最强，框架不匹配）

| 项 | 内容 |
|----|------|
| 仓库 | https://github.com/tomlm/XTerm.NET |
| 能力 | 明确 **256 + true color**、双缓冲、键鼠序列、事件系统 |
| 目标框架 | **net6.0+**（NuGet / csproj 均如此） |
| 结论 | **不能直接进 gdterm**；除非未来整体升 .NET 6+（与 Win7 目标冲突） |

### C. XtermSharp（migueldeicaza）

| 项 | 内容 |
|----|------|
| 能力 | xterm.js 血统，vim/emacs/mc 可用；headless + 自带渲染器 |
| 问题 | 主要面向 Mac / Terminal.Gui；**无 WinForms 一等公民**；维护偏研究；无稳定 NuGet 发布 |
| 结论 | 参考实现可以，不适合作为绿色 net462 主依赖 |

### D. libvt100（仅解析器）

| 项 | 内容 |
|----|------|
| 能力 | 纯解析，宿主自管渲染 |
| 缺口 | **不是完整终端状态机**（无完整 grid/alt-screen/TUI 语义） |
| 结论 | 加速 SGR 不够，解决不了 codex/opencode |

### E. libvterm / Ghostty-vt / ConPTY 宿主

| 项 | 内容 |
|----|------|
| libvterm | C 库，需 native + P/Invoke；绿色包体积与 Win7 分发复杂 |
| Ghostty-vt .NET | 现代、真彩好，但绑定新运行时 / native |
| ConEmu / Windows Terminal 嵌入 | 进程/COM 重，内存与绿色目标冲突 |
| 结论 | **否决**（违背绿色 + 低内存 + 无 native） |

### F. 商业 Rebex Terminal

| 项 | 内容 |
|----|------|
| 能力 | 成熟 VT/xterm，.NET Framework 友好 |
| 否决点 | 商业授权；绿色分发与成本不匹配 |

### G. 完全自研 cell engine

| 项 | 内容 |
|----|------|
| 优点 | 无外部依赖；可按内存极限裁剪 |
| 缺点 | 完整 xterm 是 **人月级**；与「迅速开发」冲突 |
| 结论 | 只作 **降级兜底**（VtNetCore 不可用时的精简子集），不作为主路径 |

---

## 3. 决策（推荐）

### 主路径：**Vendoring VtNetCore + 自研 GDI Cell Renderer**

```
SSH.NET ShellStream (bytes)
        │
        ▼
  VtNetCore VirtualTerminal   ← 解析 + cell buffer + alt screen + mouse state
        │
        ▼
  Gdterm CellGdiRenderer      ← IRenderer 新实现 / 替换 LightweightRenderer
        │
        ▼
  WinForms Panel (现有 Pause/节流/双缓冲模式)
```

**保留**：

- `ITerminalSession` / `TerminalSession` / 危险命令门 / 多通道 / 自动日志  
- 非活动标签 `Pause`（不泵 UI、可选冻结引擎 dirty 标志）  
- 绿色：`lib/VtNetCore.dll` + 许可证文件，**不强制在线 NuGet restore**（Windows 可 restore，Linux 手写 csproj HintPath）

**不引入**：

- XTerm.NET / Avalonia / Electron / xterm.js WebView  
- native libvterm  
- 商业 Rebex  

### 为何不是「继续改 LightweightRenderer」

行缓冲 + 丢弃 CSI **无法**渐进到 codex/opencode；必须换 **cell grid 状态机**。VtNetCore 已是该状态机，自研只做渲染与接线。

---

## 4. 内存预算（强制）

| 场景 | 目标 | 手段 |
|------|------|------|
| 启动 | 25–35MB | 不预建大 scrollback |
| 单 SSH 活动标签 | +2–5MB | cell 缓冲 80×24～120×40；scrollback 默认 **500** |
| 15 SSH 标签（多数 Pause） | **≤80MB** | Pause 标签：停重绘；scrollback 可降到 200；禁止每字符 `new string` |
| TUI 全屏 | +3–8MB/活动 | alt screen 固定 rows×cols，无无限 history 膨胀 |
| 真彩 | 可忽略增量 | `Color` 缓存刷子（现有 `_brushCache` 模式） |

硬门禁：

1. `MaxScrollback` 默认 500，Metadata 可调，上限 2000  
2. 非活动标签不跑 60fps；沿用 16ms 节流且 Pause 停 Timer  
3. 禁止 RichTextBox 回到主路径  
4. 可选：仅活动标签启用 true color 全量重绘脏矩形  

---

## 5. 分阶段交付（本项目执行）

### Phase 0 — Spike（0.5–1 天，Windows）

- vendoring `VtNetCore` 进 `lib/`  
- 最小 WinForms 窗体：SSH 或本地 echo → Feed → 画 80×24  
- 验收：`vim` 打开/退出不残影；`htop` 刷新；`echo -e "\e[38;2;255;0;0mRGB\e[0m"`  

### Phase 1 — 引擎接线（主路径）

- 新 `VtTerminalEngine`（包一层，隔离第三方类型泄漏到 UI）  
- `IRenderer` 扩展或并行 `ICellRenderer`：  
  - `Feed(byte[]/string)`  
  - `GetDirtyRect` / `Snapshot`  
  - `Resize(cols,rows)` → 同步 `ShellStream` window-change（SSH.NET 能力范围内）  
- `TerminalSession.CreateShellStream`：  
  - `TERM=xterm-256color`  
  - 若引擎支持真彩：环境侧文档指导用户 `COLORTERM=truecolor`（SSH.NET 对 env 的支持需实测；必要时 `export` 登录脚本）  

### Phase 2 — 输入与 TUI

- 键位 → VtNetCore 输入序列  
- 鼠标报告（可选开关，默认开）  
- alt screen 切换时 UI 自动全量刷新  

### Phase 3 — 替换与双轨

- `TerminalProfile.Renderer = Lightweight | VtCell`  
- 默认新连接走 `VtCell`；异常可回退 Lightweight（纯 shell）  
- 验收矩阵：tmux 分屏、`vim`、`htop`、`mc`、**codex / opencode**（远端 Linux）  

### Phase 4 — 内存与绿色收口

- 15 标签内存 spot-check  
- pack-release 包含 `VtNetCore.dll` + `LICENSE.VtNetCore.txt`  
- 更新 ARCHITECTURE / attention  

---

## 6. 风险与缓解

| 风险 | 缓解 |
|------|------|
| VtNetCore 停更 | MIT 可 fork 进 `third_party/VtNetCore`；API 面用 `VtTerminalEngine` 隔离 |
| 真彩不完整 | Spike 实测；不足则补 SGR 38;2/48;2 补丁（小改） |
| SSH.NET 无 COLORTERM | 连接后 `export COLORTERM=truecolor` / LogonScript |
| 内存超标 | 默认 scrollback 严；Pause 策略；禁止每格托管大对象 |
| Win7 GDI | 继续纯 GDI+，无 DirectX |
| 编码（UTF-8 宽字符） | VtNetCore 有字符集路径；中文宽字符需 spike |

---

## 7. 明确不做

- 升级到 .NET 6 仅为用 XTerm.NET（破坏 Win7）  
- WebView2 + xterm.js（内存与绿色双杀）  
- 嵌入 Windows Terminal / ConEmu  
- 本阶段完整自研 xterm 兼容层  

---

## 8. 建议拍板

| 问题 | 建议 |
|------|------|
| SDK 还是自研？ | **SDK：VtNetCore（vendoring）+ 自研 GDI cell 渲染** |
| 能否 true color？ | Phase 0 实测后确认；架构按 true color 预留 |
| 能否 codex/opencode？ | Phase 3 目标；依赖 alt-screen + 键鼠 + 足够 VT 覆盖 |
| 内存？ | 默认 scrollback 500 + Pause；15 标签仍盯 80MB |
| 下一步？ | ~~Phase 0 Spike~~ → **Phase 1 UI 接线** |

## 10. Phase 0 落地状态（2026-07-26）

**已完成（本仓库）：**

| 产物 | 路径 |
|------|------|
| VtNetCore 1.0.30 net46 | `lib/VtNetCore.dll` + `lib/LICENSE.VtNetCore.txt` |
| 源码参考树 | `third_party/VtNetCore-src/` |
| 引擎封装 | `src/Gdterm.Terminal/Rendering/Vt/VtTerminalEngine.cs` |
| GDI cell 渲染器 | `src/Gdterm.Terminal/Rendering/CellGdiRenderer.cs`（实现 `IRenderer`） |
| 自动化验收 | `src/Gdterm.Tests/Terminal/VtTerminalEngineTests.cs` |
| 手册 | `tools/phase0-vt-harness.md` |

**测试覆盖（Windows MSBuild 权威）：** plain text/cursor、`38;2` true color、`38;5` 256 色、alt-screen 1049、history 硬顶、DA→SendToHost。

**Phase 1–4 已落地（2026-07-26）：**

| 项 | 状态 |
|----|------|
| UI 默认 `CellGdiRenderer` | ✅ `TerminalProfile.Renderer=VtCell` |
| 双轨 Lightweight | ✅ `renderer=lightweight` Metadata |
| ShellStream → Write | ✅ 输出事件喂入 IRenderer |
| SendToHost → SendBytes | ✅ DA/键鼠应答 |
| Resize + window-change | ✅ 反射 `SendWindowChangeRequest` |
| 鼠标 press/move/release | ✅ cell 路径 |
| 去掉连接 uname 污染 | ✅ |
| AppVeyor | ✅ 根目录 `appveyor.yml` |
| 手工 vim/tmux/codex | ⏳ 用户 Windows/AppVeyor 验收 |

---

## 9. 参考链接

- VtNetCore: https://github.com/darrenstarr/VtNetCore  
- NuGet VtNetCore: https://www.nuget.org/packages/VtNetCore  
- XTerm.NET（对照，net6）: https://github.com/tomlm/XTerm.NET  
- XtermSharp: https://github.com/migueldeicaza/XtermSharp  
- libvt100（仅解析）: https://github.com/rasmus-toftdahl-olesen/libvt100  
- gdterm 现状渲染: `src/Gdterm.Terminal/Rendering/LightweightRenderer.cs`  
- gdterm PTY: `src/Gdterm.Terminal/TerminalSession.cs` (`CreateShellStream`)  
