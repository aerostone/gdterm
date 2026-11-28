# 审计证据

- `run.log`：本轮隔离服务的最终观察结果。`CONFIRMED DEFECT` 表示已复现缺陷，不是产品用例通过。
- `Program.cs`、`Audit.csproj`、`run.py`：只用于复核本审计，链接仓库生产代码；没有修改生产逻辑。
- `sftp-permission.png/json`：既有 Windows CI run 46113892779、提交 24c8ffa 的权限窗口证据。窗口与冒烟调用源码到本轮 HEAD 无差异。
- `verification.json`：源码覆盖、版本、散列、执行条件、未覆盖项。

Linux 开发机已具备 dotnet 8、OpenSSH 和 NuGet 缓存时，在仓库根运行：

```sh
python3 .codestable/changes/2026-11-28-sftp-audit/evidence/run.py
```

脚本只在回环地址启临时 sshd，使用临时密钥和测试文件，结束后清理；不修改系统 sshd、用户 authorized_keys 或 known_hosts。`GDT_DOTNET` 可指定 dotnet 路径。依赖从本机 NuGet 缓存离线还原。

探针刻意断言当前缺陷仍然存在；修复业务后需将对应行为加入正式回归测试，不能把这套审计探针直接当产品通过门。没有当前 Windows 运行时，本轮不提供“UI 全部验收通过”的结论。
