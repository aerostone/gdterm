FreeRDP 进程嵌入引擎（wfreerdp.exe）——放置说明
====================================================

gdterm 的 RDP 引擎优先使用 FreeRDP（进程嵌入），相比 mstscax ActiveX：
  - 许可证存储为用户目录文件，不写 HKLM\MSLicensing，
    彻底规避「reason=2056 / ext=267 许可存储创建被拒绝」提权问题；
  - 无 COM 注册、无位数依赖。

放置位置（二选一，运行时按序探测）：
  1. <程序目录>\freerdp\          （绿色发布包用这个）
  2. <程序目录>\lib\freerdp\      （仓库/源码运行用这个，即本目录）

下载来源（需要 FreeRDP 2.x 的 wfreerdp.exe，≥2.7 含键盘嵌入修复 PR #7790）：
  - 夜间构建：https://ci.freerdp.com/job/freerdp-nightly-windows/
    选 freerdp-2.x-win64.zip 之类条目
  - 或 GitHub Releases / 第三方镜像中的 2.11.x Windows 包

注意：FreeRDP 3.x 已用 sdl3-freerdp 替换 wfreerdp 且官方不再发稳定版
二进制；本引擎依赖 /parent-window 嵌入参数，请使用 2.x 构建。

解压后至少应包含：
  wfreerdp.exe
  freerdp2.dll / winpr2.dll 及其依赖 DLL（zlib1.dll、libssl/x509 等）
  （保持 zip 内目录结构整体拷入即可）

提交：与 lib\winpty 同理，二进制直接提交进仓库随 CI 打包。
