---
doc_type: change
kind: issue
slug: 2026-11-28-sftp-fixes
status: accepted
phase: analyzed
mode: standard
severity: P1
risk:
  level: high
  reasons: [file_integrity, remote_permissions, path_boundary, async_lifecycle]
contract:
  include:
  - src/Gdterm.Sftp/**
  - src/Gdterm.Core/L10n/**
  - src/Gdterm.UI/Controls/SftpDualPanePanel.cs
  - src/Gdterm.UI/Controls/ListShell.cs
  - src/Gdterm.UI/Controls/FilePaneControl.cs
  - src/Gdterm.UI/Controls/FilePaneModel.cs
  - src/Gdterm.UI/Controls/TransferProgressDialog.cs
  - src/Gdterm.UI/Controls/TransferCenterPanel.cs
  - src/Gdterm.UI/Forms/SftpPermissionForm.cs
  - src/Gdterm.UI/Services/SftpTransferRunner.cs
  - src/Gdterm.UI/Controls/TabContainerControl.cs
  - src/Gdterm.UI/Gdterm.UI.csproj
  - src/Gdterm.Tests/**
  - tools/sftp-smoke/**
  - tools/ui-baseline/**
  - tools/ui-matrix-ratchet.txt
  - tools/ui-tree-check.py
  - .github/workflows/ci.yml
  - docs/FEATURE-MAP.md
  - .codestable/tools/ui-coverage-registry.txt
  - .codestable/changes/2026-11-28-sftp-audit/**
  - .codestable/changes/2026-11-28-sftp-fixes/**
  - .codestable/changes/2026-11-28-port-forward-audit/**
  exclude: []
  architecture_impact: unchanged
  architecture_reason: 保留 SftpService 协议边界和双栏 provider；传输遍历抽到所属 UI 服务以集中取消、路径和覆盖规则，不新增后端或独立会话体系。
  architecture_refs: [ARCHITECTURE.md]
  requirement_impact: unchanged
  requirement_reason: 完成已有文件管理、传输、预览、权限和重连体验，修复审计确认的错误。
  requirement_refs: []
  context_refs:
    design: [.codestable/changes/2026-11-28-sftp-audit/change.md]
    impl: [src/Gdterm.Sftp/SftpService.cs, src/Gdterm.UI/Controls/SftpDualPanePanel.cs, src/Gdterm.UI/Controls/FilePaneControl.cs]
    accept: [src/Gdterm.Tests/Sftp/SftpPermissionTests.cs, src/Gdterm.Tests/Ui/DialogsSmoke.cs]
  artifacts: []
  evidence_ledger: false
  preexisting_changes: [.codestable/changes/2026-11-28-sftp-audit/change.md, .codestable/changes/2026-11-28-sftp-audit/evidence/Audit.csproj, .codestable/changes/2026-11-28-sftp-audit/evidence/verification.json, .codestable/changes/2026-11-28-sftp-audit/evidence/Program.cs, .codestable/changes/2026-11-28-sftp-audit/evidence/run.log, .codestable/changes/2026-11-28-sftp-audit/evidence/sftp-permission.png, .codestable/changes/2026-11-28-sftp-audit/evidence/README.md, .codestable/changes/2026-11-28-sftp-audit/evidence/sftp-permission.json, .codestable/changes/2026-11-28-sftp-audit/evidence/run.py]
  baseline:
    git_head: f00e88db120b75e214424ae627e1027794873add
    dirty_hashes:
      .codestable/changes/2026-11-28-sftp-audit/change.md: 93412c4c810e9d309f14af8c04ae7d0d9a9963b7ca954e3fe05c1d9c3b87a75a
      .codestable/changes/2026-11-28-sftp-audit/evidence/Audit.csproj: 8a74243ee201a6d186caa68b3095f68ec6625f8ed8d1e3d160c419338c3f7922
      .codestable/changes/2026-11-28-sftp-audit/evidence/Program.cs: d04d0ae52c8fda8efb650e2b04d238df24c4f5f91c8d7cb9b04e9376d2559f45
      .codestable/changes/2026-11-28-sftp-audit/evidence/README.md: dd98b39c7f58e4d37474b25f58de63f5b13c6922821711ad71eac35cc6264180
      .codestable/changes/2026-11-28-sftp-audit/evidence/run.log: 72407790c9e47dba506f0ab52310330acdc0d59c61d70bf710385a5409fa22cc
      .codestable/changes/2026-11-28-sftp-audit/evidence/run.py: 797f71e8195119f3e95bcf62bef2ad61cbc64dff1b9ede286e00e24c0f356e72
      .codestable/changes/2026-11-28-sftp-audit/evidence/sftp-permission.json: 178586e93101464c387fa9fa8217f0873d49ddb5d7845f8e409fac6822f23f28
      .codestable/changes/2026-11-28-sftp-audit/evidence/sftp-permission.png: 2f369b84eb35d73e79a226df0371ebc76bb05949fdfcdcffc688c37eeba952b8
      .codestable/changes/2026-11-28-sftp-audit/evidence/verification.json: aaa0f13d28a8367c9ec19b4cfc9fea139dda77124f87f731188d5960e34b7634
---

# SFTP 完整功能修复

## 目标与边界

用户已明确要求全部优化上一轮 13 项发现。修复、必要测试与既有提交/推送/CI 监控授权继续有效。以 S01–S13 为验收对象；同一调用链的目录边界、拖放空态、跟随来源及异步回调清理一起修正。

## 根因与设计依据

见相邻审计报告及真实服务探针。权限参数重复转码；依赖 canonical-path 追随软链接；普通传输无条件按长度续传；界面初始化漏调用；下载名称缺 Windows 边界；同步建连及无关闭令牌；递归漏删根目录；进度/取消断线状态未接线；预览临时名冲突且全量下载；拖拽缺来源身份。

## 设计与契约

- 推荐沿既有服务和面板修正，增加有限读取/元数据接口与可取消操作，生产实现和测试替身同时迁移。备选仅在 UI 禁用续传/链接操作可快速止损，但会损失既有能力且不能修复底层使用者，因此不采用。
- 服务统一管理连接存活与并发；连接返回异步任务，取消或 Dispose 后不得发布新连接。后台任务完成后不得操作已关闭控件。
- 权限直接传合法八进制数字；表单回填、勾选和数字框保持一致，应用前捕获结果值。
- 链接在元数据中保留；删除和重命名只操作选定目录项，递归不追随链接。下载和上传的目录遍历拒绝不安全的循环/越界链接。
- 普通上传/下载使用覆盖逻辑，并确认覆盖已有文件；显式续传仅在完整前缀一致时追加，不一致则报可读错误，不能默默混合内容。完整内容一致才允许跳过。
- 本地下载限制合法 Windows 单名称、规范化根边界、重解析点与大小写碰撞。后台传输使用稳定的源/目标快照。
- 实际任务接入已有进度对话框，显示当前文件、字节进度和结果；取消、关闭及断线传播令牌；失败恢复提供重新连接，不能显示假成功。
- 拖放携带所属 provider 身份，只允许同双栏的对侧组合；空态也能接收受支持拖放。
- 文本预览直接有限读取流，限制行数与字节，取消/超时可中止，不落共享临时文件。

## 行为增量

MODIFIED：S01–S13 的错误路径按用户选择安全执行；新增的是原有进度/取消/重连能力的实际接线，不改变认证和隧道复用规则。

## 执行计划

1. 服务权限、链接、传输、有限预览及连接生命周期；真实 OpenSSH 回归。
2. 路径边界与双栏任务编排；权限初始化、拖放、进度/取消/重连、关闭回调。
3. 单元测试、Windows UI 表单与双栏场景、Linux 实连纳入 CI。
4. 本地编译/静态检查；推送监控两套 CI，审查截图和逐项验收。

## 执行证据

- S01/S04：权限数字直接传 SSH.NET；表单初值及双向联动修复，特殊权限大写 S/T 不错误勾选执行位。
- S02/S08：目录原条目删除/移动，递归不追链接，删除包括所选最外层目录。
- S03/S05：普通传输覆盖并确认；续传完整前缀验证；新增传输计划与 Windows 名称/根边界/重解析点/大小写冲突检查。同步执行器同步采用普通覆盖语义。
- S06/S07：后台异步握手，串行会话操作，取消/释放撤销会话，关闭后忽略 UI 回调。
- S09/S11/S12：实际任务进度及取消接线；拖放核对 provider；空态支持拖放；断线禁用旧栏并提供重连；跟随核对连接身份并保留忙时最新导航。
- S10/S13：文本限制 100 行/256 KiB，图片限制 8 MiB，预览直接读内存，无共享临时文件。
- Linux 实连：`tools/sftp-smoke/run.py` 75 项回归通过，权限/同步规划及新增路径边界 37 项断言通过。见 `evidence/sftp-smoke.log`。
- 本地全量 C# 编译通过（380 个源码，13 个工程清单）；Windows 行为断言与双栏/断线页面矩阵已加入 CI，最终结果通过。
- L0 与 i18n 等静态门通过；功能地图按新增 UI 证据登记重新生成。

- CI 扩展：新面板按常驻标签免判关闭按钮；暗色表头、状态行内边距与窄栏列表预留空间已修复。审计旧 JSON 统一为 UTF-8 无 BOM 与 LF，字段内容未改，以符合仓库编码门。

- Windows CI 发现异步导航超时，释放异常为连带故障。生产首次连接与目录加载延后到 OnHandleCreated 后的 UI 消息，未展示就关闭的面板不启动后台连接；该修正单独验证仍失败。进一步发现测试只有 DoEvents 临时消息循环，直接调用异步方法时可能没有 WinForms 同步上下文。测试宿主已改用 Application.Run 持续消息循环，并增加上下文断言及诊断日志；GitHub 46137351042 已验证导航、实际拖放下载、跟随、重连、实际传输取消与关闭连接全部通过，原卡死消失。

- 同轮补齐构造期创建句柄的边界：首次连接改为 OnLoad 后排队，关闭未装载面板不得创建服务。像素审查发现进度标题/说明行高未包含 Padding，已补足行盒，增加真实文字像素断言；本地路径状态行增加省略显示。

- 高字号/150% 注入矩阵发现文件工具条自动换行会挤掉列表：改为单行加“更多”菜单，菜单保留所有动作；高度包含按钮 Margin，栏标题用实际字号行盒。实际窄栏点击菜单刷新加入回归。长路径传输进度先显示文件名，避免省略号把文件身份一起裁掉。

- 用户在 CI 运行期间继续要求检查下一个功能，独立端口转发审计产物写入 `2026-11-28-port-forward-audit/`。仅将该文档/探针目录纳入并行交付范围供工作树范围校验；未修改端口转发业务代码，其 9 项新发现不属于本 SFTP 修复的已解决项。

## 验收结果

源码提交 `4bcf79f` 已完成验收。GitHub run [46141368771](https://github.com/aerostone/gdterm/actions/runs/46141368771) 全部步骤成功；AppVeyor [0.1.566](https://ci.appveyor.com/project/aerostone/gdterm/build/0.1.566) 成功。

- [x] S01/S04：真实 chmod 600/640/644/700/755/777/000；权限 UI 初值、双向修改、应用；截图 644 与九个复选框匹配。
- [x] S02/S08：实连删除/重命名普通与悬挂链接，不改变目标；递归删除包含根且不追链接。
- [x] S03/S05：覆盖、完整前缀续传、拒绝不符目标且保留原内容；路径/非法名/重解析点/大小写冲突及同步规划验证。
- [x] S06/S07：异步握手、关闭连接中面板、未加载面板关闭、取消后重连；关闭后不复活。
- [x] S09/S11/S12：实际 UI 拖放下载、拒绝跨服务/同侧拖放；真实进度和取消传播、断线禁用旧栏及重连；跟随排队；窄栏“更多→刷新”。
- [x] S10/S13：并发同名预览隔离，100 行/256 KiB 限额，内存预览无共享临时文件。
- [x] 全量构建/静态门/性能门；1089 项单测、75 项实连及 37 项纯断言；10 个 UI 用例组零失败；64 页 × 6 组矩阵 = 384 dump；110 张像素基线通过。

JSON、运行报告和最终提交截图在 `evidence/`；没有用旧审计的“缺陷存在”探针充当回归通过。人工看图确认进度标题和当前文件名、取消文案、权限数字、重连按钮以及大字号窄栏可见。

## 遗留事项

本次 S01–S13 无未解决项。未在用户生产服务器验证；高 DPI 为应用缩放注入矩阵，真实跨显示器系统缩放仍属于真机验证边界；Windows 交互用真实处理路径和服务替身，网络行为另外由隔离 OpenSSH 测试。下一项端口转发审计发现的 9 项问题单独登记，尚未修复。

本轮无新增经验库文档，必要约束及复现证据保留在本包。
