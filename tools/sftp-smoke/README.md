# SFTP 实连回归

`python3 tools/sftp-smoke/run.py` 在 Linux 上创建临时目录、临时密钥和只监听回环地址的 OpenSSH 服务，直接编译并执行生产 SFTP 服务、路径校验与双栏传输编排代码。退出时关闭服务、清理文件，不修改系统 sshd、authorized_keys 或 known_hosts。

依赖：.NET 8 SDK、Python 3、OpenSSH server/client。以当前用户运行；CI 安装 sshd 后创建 `/run/sshd` 即可。离线开发可通过 `GDT_NUGET_SOURCE` 指定已有 NuGet 包缓存。

验证权限、软链接/悬挂链接删除与移动、目录递归、覆盖与校验续传、预览限额与并发、Windows 文件名/重解析点/大小写冲突、进度取消、连接关闭竞态和重连。WinForms 布局与交互由 `SftpUiSmoke` 及页面矩阵在 Windows CI 中验证。
