# P0–P2 接线完成 + 内存评审

**日期**: 2026-07-25  
**基线**: master（接线后）  
**目标**: 绿色便携，SSH 多标签 30–80MB，Win7/.NET 4.6.2

---

## 一、本轮完成项

### P0
| 项 | 状态 | 说明 |
|---|---|---|
| RDP 标签 | ✅ | `TabContainerControl.CreateRdpTab` 使用 `RdpClient`，懒连接（选中后 Connect） |
| 串口标签 | ✅ | `CreateSerialTab` → `TerminalControl` + `SerialSession` |
| 危险命令输入路径 | ✅ | `TerminalControl` 回车确认 + 多通道广播前 `DangerousCommandDialog` |
| 主菜单接线 | ✅ | 工具箱/扫描/高亮/快捷键/登录脚本/多通道/批量/历史/健康/端口转发/本地终端/SFTP/QuickBar |

### P1 资源闸门
| 项 | 默认 | 说明 |
|---|---|---|
| SecretScanner 后台 | **关** | `EnableBackgroundScan = false` |
| TerminalAutoLogger | **不自动开** | 仅 `EnableAutoLog` 显式调用；上限 **10MB × 3** |
| HealthMonitor | 活动标签 | `IsPaused` 非活动跳过；历史 **120** 条 |
| Scrollback | 硬顶 | `TerminalProfile.ScrollbackLines` clamp **100–1000** |
| 端口扫描并发 | 40 | 原 100 → 40 |
| 子网扫描并发 | 30 | 原 50 → 30 |

### P2
| 项 | 状态 |
|---|---|
| AutoReconnect ↔ HealthMonitor | ✅ `ConnectionLost` → `NotifyConnectionLost`；`DefaultReconnectFunc` close+reopen |
| TerminalProfile 应用 | ✅ 配色/`Scrollback` clamp；AutoRunCommands |
| SFTP 入口 | ✅ `SftpBrowserPanel` + 菜单「SFTP 浏览器」 |
| 工具 CreatePanel | ✅ 5 个工具均有暗色操作面板（`ToolPanelHelper`） |

### 关键修复
- 补 `TerminalSessionFactory` / `SftpServiceFactory`
- 重写 UI `TerminalControl`（懒连接、危险命令、Pause/Resume）
- 对齐 `LocalTerminalSession` 到 `ITerminalSession`
- 修正 `TunnelEndpoint` 命名空间为 `Gdterm.Core.Models`
- csproj 补 Compile Include

---

## 二、接线后内存模型

### 启动（无标签）
| 组件 | 粗估 |
|---|---|
| CLR + WinForms + 菜单/树 | 20–30MB |
| KeePass 未解锁 | ~0 |
| 工具注册表（类已加载，面板未建） | <1MB |
| SecretScanner 实例但未扫 | <1MB |
| **合计** | **~25–35MB** |

### 每 SSH 标签（已连接）
| 项 | 粗估 |
|---|---|
| SSH.NET + ShellStream | 0.5–2MB |
| LightweightRenderer 300 行 | ~0.3–0.8MB |
| HealthMonitor 120 快照 | <50KB |
| **单标签** | **~1–3MB** |

### 15 路 SSH（仅活动渲染）
| 场景 | 粗估 |
|---|---|
| 启动基线 | 30MB |
| 15 × ~2MB | 30MB |
| 活动 tab 渲染 | 已含 |
| **合计** | **~50–70MB → 落在 30–80MB 目标内** |

### 高风险场景（会打破目标）
| 场景 | 影响 |
|---|---|
| 1× RDP ActiveX | +50–150MB+（不计入纯终端预算） |
| 手动开 SecretScan 全盘 | 扫描期峰值明显，Win7 低配会抖 |
| 显式 EnableAutoLog × 多会话 | 磁盘 10MB×3/会话，内存影响小 |
| 多通道广播 15 路 | 短时 Task 峰值，可接受 |
| 侧边面板 RichTextBox（工具箱） | 单开 ~2–5MB，关面板应 Dispose |

---

## 三、磁盘

| 路径 | 策略 |
|---|---|
| `data/logs/` 审计 | 原有轮转（约 10MB×10 + 天数） |
| `data/logs/commands/` | 10MB 轮转 |
| `data/logs/terminal/` | **仅显式开启**；10MB×3 |
| `data/config/` | 小 JSON |
| `data/gdterm.kdbx` | 凭据，可迁移 |

---

## 四、仍需 Windows 实机验证

1. RDP ActiveX（Linux 开发环境仅占位 Label）
2. 串口真实设备
3. 15 标签 Process Explorer 工作集
4. 危险命令对话框在高速输入下的体验

---

## 五、结论

- **P0–P2 产品入口已接通**，不再是“幽灵功能”。
- **默认策略偏保守**：后台扫描关、自动日志关、并发收紧、历史有硬顶。
- **纯 SSH 多标签仍可落在 30–80MB**；RDP / 全盘扫描 / 全家桶侧边面板同时开则需用户预期额外成本。
