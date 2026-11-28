# 端口转发审计探针

`GDT_NUGET_SOURCE=/home/aerostone/.nuget/packages python3 run.py`，也可省略缓存环境变量在线还原。
需要 .NET 8、OpenSSH 和 Linux；源码链接生产 PortForwardManager，未修改业务代码。
运行只监听本机回环地址，使用临时密钥；暂停隔离 SSH 会话进程 1 秒后恢复，验证同步阻塞；退出关闭临时 sshd。
探针中的 CONFIRMED 表示确认现状（包含缺陷），不是修复回归测试。13 项观察匹配，最终退出码 0。

截图来自 GitHub run 46138278324 / commit 644edce；相关端口转发及编辑器代码与审计 HEAD 无差异。
界面截图仅证明空态及编辑器布局，未以真实鼠标在 Windows 执行网络转发。
