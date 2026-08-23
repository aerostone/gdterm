FreeRDP 进程嵌入引擎（wfreerdp.exe）——来源说明
====================================================

gdterm 的 RDP 引擎优先使用 FreeRDP（进程嵌入），相比 mstscax ActiveX：
  - 许可证存储为用户目录文件，不写 HKLM\MSLicensing，
    彻底规避「reason=2056 / ext=267 许可存储创建被拒绝」提权问题；
  - 无 COM 注册、无位数依赖。

二进制从哪来？
  【默认】无需任何手工操作——AppVeyor CI 在 install 阶段直接从官方源码
    （GitHub Releases 的 freerdp-2.11.7.zip，带 sha256 校验）编译
    wfreerdp.exe + freerdp2.dll + winpr2.dll，产物缓存在 CI 的
    freerdp-bin\ 目录并打包进 dist\gdterm\freerdp\。
    （官方已停发 2.x Windows 二进制：夜间构建只留最近 5 个且已是 3.x，
     GitHub Releases 仅源码包，3.x 的 sdl-freerdp 嵌入参数损坏 #12227，
     故只能自建。）

  【可选覆盖】把现成的 FreeRDP 2.x Windows 构建解压到本目录
    （lib\freerdp\wfreerdp.exe ...），CI 会优先采用手工放置的版本。
    运行时探测顺序：<程序目录>\freerdp\ → <程序目录>\lib\freerdp\。

技术要求：FreeRDP ≥ 2.7（含 parent-window 键盘输入修复 PR #7790）；
必须用 2.x 构建（/parent-window 参数在 3.x 已移除）。

本目录若只有 README.txt 而无 exe，属正常状态——CI 会自行构建。
