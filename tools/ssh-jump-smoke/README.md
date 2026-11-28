# SSH 跳板实连回归

Linux 安装 .NET 8、OpenSSH server 后运行 `python3 tools/ssh-jump-smoke/run.py`。
可通过 `GDT_DOTNET` 指定 dotnet；服务需要提权时设置 `GDT_SSHD_SUDO=1`（仅启动/停止临时 sshd）。

工具链接生产 TunnelManager、TunnelSession、凭据模型、主机密钥校验源码，使用 SSH.NET 2026.0.0。
临时 SSH 服务只监听回环地址，使用独立密钥和 known_hosts；不修改系统配置或用户 authorized_keys。
验证两跳命令、断线重建、真实 HTTP CONNECT、代理失败、凭据隔离、并发单实例和关闭/销毁时的建立竞争。
测试结束关闭服务、删除临时密钥。该实连测试运行于 net8；Windows CI 另行编译 net462 并验证原生界面。
