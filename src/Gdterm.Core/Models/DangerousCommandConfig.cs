using System;
using System.Collections.Generic;

namespace Gdterm.Core.Models
{
    /// <summary>
    /// 危险命令检测配置——定义哪些命令需要确认、确认几次
    /// 配置文件路径: config/dangerous-commands.json
    /// </summary>
    public class DangerousCommandConfig
    {
        /// <summary>当前配置架构版本（2 = 内置规则只读 + 用户 delta）</summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// 配置架构版本。
        /// 2 = 内置规则只读 + 用户 delta（内置规则的 pattern/level/文案以代码为准，仅继承用户的 enabled）；
        /// 1/0 = 旧文件，文件里的内置规则视为过期副本，加载时丢弃并升级。
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>
        /// 是否启用危险命令检测
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 是否对多通道广播也生效
        /// </summary>
        public bool ApplyToBroadcast { get; set; } = true;

        /// <summary>
        /// 危险命令规则列表
        /// </summary>
        public List<DangerousCommandRule> Rules { get; set; } = new List<DangerousCommandRule>();

        /// <summary>
        /// 白名单——这些命令即使匹配危险规则也放行（精确匹配）
        /// </summary>
        public List<string> Whitelist { get; set; } = new List<string>();

        /// <summary>
        /// 获取默认配置（内置的危险命令规则）
        /// </summary>
        public static DangerousCommandConfig GetDefault()
        {
            var config = new DangerousCommandConfig();

            // ===== Critical 级：系统毁灭性命令，必须确认 3 次 =====
            config.Rules.AddRange(new[]
            {
                // 文件系统毁灭
                new DangerousCommandRule
                {
                    Id = "rm-rf-root",
                    Name = L10n.T("递归删除根目录"),
                    // 精度修正：旧写法 `...\s+/` 会把 `rm -rf /tmp/build` 也算成删根（/ 后面接什么都命中），
                    // 每天踩一次误报 → 用户学会无脑点确认，整套闸门失效。现在只认真正的根：`/` 后必须是空白或行尾。
                    Pattern = @"\brm\s+(-{1,2}\S+\s+)*/(\s|$)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("递归强制删除根目录，将导致系统完全损坏"),
                    Category = "filesystem"
                },
                new DangerousCommandRule
                {
                    Id = "rm-rf-root-star",
                    Name = L10n.T("递归删除根目录通配"),
                    Pattern = @"\brm\s+(-{1,2}\S+\s+)*/\*",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("递归强制删除根目录下所有内容"),
                    Category = "filesystem"
                },
                new DangerousCommandRule
                {
                    Id = "rm-rf-home",
                    Name = L10n.T("删除用户主目录"),
                    // 只拦主目录本身（~ / ~/ / ~/* / $HOME），`rm -rf ~/build` 属日常操作
                    Pattern = @"\brm\s+(-{1,2}\S+\s+)*(~|\$HOME)(/\*)?(\s|$)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("递归强制删除用户主目录，所有个人数据将丢失"),
                    Category = "filesystem"
                },
                new DangerousCommandRule
                {
                    // 覆铜：旧 rm-rf-root 的宽正则顺带盖住了 `rm -rf /etc`；收紧后必须显式补上，
                    // 否则“删根”变安全了、“删 /etc”却没人管。只认目录本身，`/etc/nginx` 这种子目录不算。
                    Id = "rm-system-dirs",
                    Name = L10n.T("删除系统关键目录"),
                    Pattern = @"\brm\s+(-{1,2}\S+\s+)*/(etc|usr|bin|sbin|boot|lib|lib64|opt|srv|sys|proc|dev)(/\*)?(\s|$)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("删除系统关键目录，将导致服务不可用甚至无法开机"),
                    Category = "filesystem"
                },

                // 磁盘毁灭
                new DangerousCommandRule
                {
                    Id = "dd-disk-wipe",
                    Name = L10n.T("dd 磁盘写入"),
                    Pattern = @"dd\s+.*of=/dev/[shv]",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("dd 直接写入磁盘设备，将覆盖所有数据且不可恢复"),
                    Category = "disk"
                },
                new DangerousCommandRule
                {
                    Id = "mkfs-format",
                    Name = L10n.T("格式化磁盘"),
                    // 审计发现：这条规则写成了 PatternType.Contains + 正则转义 `mkfs\.`，
                    // 于是它去找字面量 "mkfs\."（带反斜杠）——真命令里永远不存在，规则实际是死的。
                    Pattern = @"\bmkfs\.",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("格式化文件系统，目标分区所有数据将被清除"),
                    Category = "disk"
                },
                new DangerousCommandRule
                {
                    Id = "mkfs-all",
                    Name = L10n.T("mkfs 格式化"),
                    Pattern = @"mkfs ",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("格式化文件系统，目标分区所有数据将被清除"),
                    Category = "disk"
                },
                new DangerousCommandRule
                {
                    Id = "raw-write-sda",
                    Name = L10n.T("直接写入磁盘设备"),
                    Pattern = @">\s*/dev/[shv]d[a-z]",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("直接重定向写入磁盘设备文件"),
                    Category = "disk"
                },

                // 系统崩溃
                new DangerousCommandRule
                {
                    Id = "fork-bomb",
                    Name = L10n.T("Fork 炸弹"),
                    Pattern = @":\(\)\{\s*:\|:&\s*\};:",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("无限 fork 进程，将导致系统资源耗尽崩溃"),
                    Category = "system"
                },

                // 文件系统根目录操作
                new DangerousCommandRule
                {
                    Id = "chmod-777-root",
                    Name = L10n.T("根目录权限 777"),
                    Pattern = @"\bchmod\s+(-\S+\s+)*777\s+/(\s|$)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("将根目录权限设为 777，系统安全完全失控"),
                    Category = "permissions"
                },
                new DangerousCommandRule
                {
                    Id = "chmod-r-root",
                    Name = L10n.T("递归修改根目录权限"),
                    // 旧写法 `chmod\s+-\S*R\s+` 对每条 `chmod -R 755 .` 都报 Critical（3 次确认），纯噪声。
                    // 现在要求目标是根目录本身：`chmod -R <mode> /`
                    Pattern = @"\bchmod\s+(-\S+\s+)*\S+\s+/(\s|$)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("递归修改权限，可能影响系统关键文件"),
                    Category = "permissions"
                },
                new DangerousCommandRule
                {
                    Id = "chown-r-root",
                    Name = L10n.T("递归修改根目录所有者"),
                    Pattern = @"\bchown\s+(-\S+\s+)*\S+\s+/(\s|$)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Critical,
                    ConfirmCount = 3,
                    Description = L10n.T("递归修改文件所有者，可能破坏系统权限结构"),
                    Category = "permissions"
                },
            });

            // ===== High 级：数据丢失或服务中断，确认 2 次 =====
            config.Rules.AddRange(new[]
            {
                // 进程管理
                new DangerousCommandRule
                {
                    Id = "kill-9-1",
                    Name = L10n.T("杀死 init 进程"),
                    Pattern = @"kill\s+-9\s+1\s*$",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("杀死 PID 1 (init/systemd) 将导致系统关机"),
                    Category = "process"
                },
                new DangerousCommandRule
                {
                    Id = "killall-9",
                    Name = L10n.T("杀死所有进程"),
                    // 同 mkfs-format：Contains + 正则转义写法 = 死规则（找 "killall\s+-9" 字面量）
                    Pattern = @"\bkillall\s+-9",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("强制杀死所有匹配进程，可能导致服务中断"),
                    Category = "process"
                },
                new DangerousCommandRule
                {
                    Id = "kill-all-processes",
                    Name = L10n.T("杀死所有进程"),
                    Pattern = @"kill\s+-9\s+-1",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("杀死系统所有进程，等同于系统关机"),
                    Category = "process"
                },
                new DangerousCommandRule
                {
                    Id = "pkill-all",
                    Name = L10n.T("pkill 全部进程"),
                    Pattern = @"pkill\s+-9\s+\*",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("强制杀死所有进程"),
                    Category = "process"
                },

                // 系统管理
                new DangerousCommandRule
                {
                    Id = "shutdown",
                    Name = L10n.T("关机"),
                    Pattern = "shutdown ",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("系统关机命令，所有连接将断开"),
                    Category = "system"
                },
                new DangerousCommandRule
                {
                    Id = "reboot",
                    Name = L10n.T("重启"),
                    Pattern = "reboot",
                    PatternType = PatternType.Equals,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("系统重启，所有连接将断开"),
                    Category = "system"
                },
                new DangerousCommandRule
                {
                    Id = "halt",
                    Name = L10n.T("停机"),
                    Pattern = "halt",
                    PatternType = PatternType.Equals,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("系统停机"),
                    Category = "system"
                },
                new DangerousCommandRule
                {
                    Id = "poweroff",
                    Name = L10n.T("断电关机"),
                    Pattern = "poweroff",
                    PatternType = PatternType.Equals,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("立即断电关机"),
                    Category = "system"
                },

                // 防火墙
                new DangerousCommandRule
                {
                    Id = "iptables-flush",
                    Name = L10n.T("清空防火墙规则"),
                    Pattern = "iptables -F",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("清空所有防火墙规则，服务器将暴露在网络中"),
                    Category = "firewall"
                },
                new DangerousCommandRule
                {
                    Id = "iptables-delete",
                    Name = L10n.T("删除防火墙规则"),
                    Pattern = "iptables -D",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("删除指定防火墙规则"),
                    Category = "firewall"
                },
                new DangerousCommandRule
                {
                    Id = "firewalld-stop",
                    Name = L10n.T("停止防火墙服务"),
                    Pattern = @"systemctl\s+(stop|disable)\s+firewalld",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("停止/禁用 firewalld，服务器防火墙将关闭"),
                    Category = "firewall"
                },
                new DangerousCommandRule
                {
                    Id = "ufw-disable",
                    Name = L10n.T("禁用 UFW 防火墙"),
                    Pattern = "ufw disable",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("禁用 UFW 防火墙"),
                    Category = "firewall"
                },

                // 用户管理
                new DangerousCommandRule
                {
                    Id = "userdel-root",
                    Name = L10n.T("删除 root 用户"),
                    Pattern = @"userdel\s+.*root",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("删除 root 用户将导致系统无法管理"),
                    Category = "user"
                },
                new DangerousCommandRule
                {
                    Id = "passwd-root",
                    Name = L10n.T("修改 root 密码"),
                    Pattern = @"passwd\s+root",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("修改 root 密码将影响所有依赖 root 认证的服务"),
                    Category = "user"
                },

                // SSH
                new DangerousCommandRule
                {
                    Id = "ssh-keygen-overwrite",
                    Name = L10n.T("覆盖 SSH 密钥"),
                    Pattern = "ssh-keygen",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("生成新 SSH 密钥可能覆盖现有密钥，导致无法远程登录"),
                    Category = "ssh"
                },

                // 磁盘操作
                new DangerousCommandRule
                {
                    Id = "fdisk",
                    Name = L10n.T("磁盘分区操作"),
                    Pattern = "fdisk ",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("磁盘分区操作，错误操作将导致数据丢失"),
                    Category = "disk"
                },
                new DangerousCommandRule
                {
                    Id = "parted",
                    Name = L10n.T("磁盘分区操作"),
                    Pattern = "parted ",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("磁盘分区操作"),
                    Category = "disk"
                },

                // crontab
                new DangerousCommandRule
                {
                    Id = "crontab-remove",
                    Name = L10n.T("删除所有定时任务"),
                    Pattern = "crontab -r",
                    PatternType = PatternType.Equals,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("删除当前用户所有定时任务，不可恢复"),
                    Category = "system"
                },

                // 包管理高危操作
                new DangerousCommandRule
                {
                    Id = "apt-remove-all",
                    Name = L10n.T("卸载关键包"),
                    Pattern = @"apt\s+.*remove\s+.*linux-(image|kernel|headers)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("卸载 Linux 内核包，系统将无法启动"),
                    Category = "package"
                },
                new DangerousCommandRule
                {
                    Id = "yum-remove-all",
                    Name = L10n.T("卸载关键包"),
                    Pattern = @"yum\s+.*remove\s+.*kernel",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("卸载内核包"),
                    Category = "package"
                },
            });

            // ===== Medium 级：潜在风险，确认 1 次 =====
            config.Rules.AddRange(new[]
            {
                // 管道执行（最常见攻击向量）
                new DangerousCommandRule
                {
                    Id = "curl-pipe-sh",
                    Name = L10n.T("下载并执行脚本"),
                    Pattern = @"curl\s+.*\|\s*(ba)?sh",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("从网络下载脚本并直接执行，可能包含恶意代码"),
                    Category = "network"
                },
                new DangerousCommandRule
                {
                    Id = "wget-pipe-sh",
                    Name = L10n.T("下载并执行脚本"),
                    Pattern = @"wget\s+.*\|\s*(ba)?sh",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("从网络下载脚本并直接执行"),
                    Category = "network"
                },
                new DangerousCommandRule
                {
                    Id = "curl-pipe-bash-exec",
                    Name = L10n.T("下载并执行脚本"),
                    Pattern = @"curl\s+.*\|\s*sudo\s+(ba)?sh",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("以 root 权限下载并执行远程脚本"),
                    Category = "network"
                },
                new DangerousCommandRule
                {
                    Id = "wget-pipe-bash-exec",
                    Name = L10n.T("下载并执行脚本"),
                    Pattern = @"wget\s+.*\|\s*sudo\s+(ba)?sh",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.High,
                    ConfirmCount = 2,
                    Description = L10n.T("以 root 权限下载并执行远程脚本"),
                    Category = "network"
                },

                // sudo su / sudo -i
                new DangerousCommandRule
                {
                    Id = "sudo-su",
                    Name = L10n.T("切换到 root"),
                    Pattern = "sudo su",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("切换到 root 用户，后续所有命令以 root 执行"),
                    Category = "privilege"
                },
                new DangerousCommandRule
                {
                    Id = "sudo-bash",
                    Name = L10n.T("以 root 打开 shell"),
                    Pattern = "sudo bash",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("以 root 身份打开新 shell"),
                    Category = "privilege"
                },
                new DangerousCommandRule
                {
                    Id = "sudo-i",
                    Name = L10n.T("以 root 打开登录 shell"),
                    Pattern = "sudo -i",
                    PatternType = PatternType.Equals,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("以 root 身份打开登录 shell"),
                    Category = "privilege"
                },

                // 系统服务
                new DangerousCommandRule
                {
                    Id = "systemctl-stop",
                    Name = L10n.T("停止系统服务"),
                    Pattern = @"systemctl\s+stop\s+",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("停止系统服务，可能影响业务运行"),
                    Category = "service"
                },
                new DangerousCommandRule
                {
                    Id = "systemctl-disable",
                    Name = L10n.T("禁用系统服务"),
                    Pattern = @"systemctl\s+disable\s+",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("禁用系统服务开机自启"),
                    Category = "service"
                },
                new DangerousCommandRule
                {
                    Id = "service-stop",
                    Name = L10n.T("停止服务"),
                    Pattern = @"service\s+\S+\s+stop",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("停止服务"),
                    Category = "service"
                },

                // 网络配置
                new DangerousCommandRule
                {
                    Id = "ifconfig-down",
                    Name = L10n.T("禁用网卡"),
                    Pattern = @"ifconfig\s+\S+\s+down",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("禁用网络接口，将断开网络连接"),
                    Category = "network"
                },
                new DangerousCommandRule
                {
                    Id = "ip-link-down",
                    Name = L10n.T("禁用网卡"),
                    Pattern = @"ip\s+link\s+set\s+\S+\s+down",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("禁用网络接口"),
                    Category = "network"
                },

                // 历史记录
                new DangerousCommandRule
                {
                    Id = "history-clear",
                    Name = L10n.T("清除命令历史"),
                    Pattern = "history -c",
                    PatternType = PatternType.Equals,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("清除命令历史记录，可能隐藏操作痕迹"),
                    Category = "audit"
                },

                // Git 高危
                new DangerousCommandRule
                {
                    Id = "git-reset-hard",
                    Name = L10n.T("Git 硬重置"),
                    Pattern = "git reset --hard",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("Git 硬重置，所有未提交的修改将丢失"),
                    Category = "git"
                },
                new DangerousCommandRule
                {
                    Id = "git-clean-fd",
                    Name = L10n.T("Git 清理未跟踪文件"),
                    Pattern = "git clean -fd",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("删除所有未跟踪的文件和目录，不可恢复"),
                    Category = "git"
                },
                new DangerousCommandRule
                {
                    Id = "git-push-force",
                    Name = L10n.T("Git 强制推送"),
                    Pattern = "git push --force",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("强制推送可能覆盖远程仓库历史"),
                    Category = "git"
                },
                new DangerousCommandRule
                {
                    Id = "git-push-f",
                    Name = L10n.T("Git 强制推送"),
                    Pattern = "git push -f ",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("强制推送（简写）"),
                    Category = "git"
                },

                // Docker 高危
                new DangerousCommandRule
                {
                    Id = "docker-rm-all",
                    Name = L10n.T("删除所有容器"),
                    Pattern = @"docker\s+rm\s+.*-f.*-a",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("强制删除所有容器"),
                    Category = "docker"
                },
                new DangerousCommandRule
                {
                    Id = "docker-rmi-all",
                    Name = L10n.T("删除所有镜像"),
                    Pattern = @"docker\s+rmi\s+.*-f.*-a",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("强制删除所有镜像"),
                    Category = "docker"
                },
                new DangerousCommandRule
                {
                    Id = "docker-prune",
                    Name = L10n.T("Docker 清理"),
                    Pattern = "docker system prune",
                    PatternType = PatternType.Contains,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("清理 Docker 未使用的资源"),
                    Category = "docker"
                },

                // 环境变量/配置
                new DangerousCommandRule
                {
                    Id = "rm-bashrc",
                    Name = L10n.T("删除 shell 配置"),
                    Pattern = @"rm\s+.*\.(bashrc|bash_profile|profile|zshrc)",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("删除 shell 配置文件，可能导致登录异常"),
                    Category = "config"
                },
                new DangerousCommandRule
                {
                    Id = "rm-ssh-config",
                    Name = L10n.T("删除 SSH 配置"),
                    Pattern = @"rm\s+.*\.ssh/",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("删除 SSH 配置或密钥文件"),
                    Category = "config"
                },

                // 大规模删除
                new DangerousCommandRule
                {
                    Id = "rm-var-log",
                    Name = L10n.T("删除日志目录"),
                    Pattern = @"rm\s+.*-r.*\s+/var/log",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("递归删除日志目录，可能丢失审计记录"),
                    Category = "filesystem"
                },
                new DangerousCommandRule
                {
                    Id = "rm-tmp-star",
                    Name = L10n.T("清空临时目录"),
                    Pattern = @"rm\s+.*-r.*/tmp/\*",
                    PatternType = PatternType.Regex,
                    Level = DangerLevel.Medium,
                    ConfirmCount = 1,
                    Description = L10n.T("清空 /tmp 目录，可能影响正在运行的程序"),
                    Category = "filesystem"
                },
            });

            return config;
        }
    }

    /// <summary>
    /// 单条危险命令规则
    /// </summary>
    public class DangerousCommandRule
    {
        /// <summary>
        /// 规则 ID
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 规则名称（显示用）
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 匹配模式
        /// </summary>
        public string Pattern { get; set; }

        /// <summary>
        /// 匹配类型
        /// </summary>
        public PatternType PatternType { get; set; }

        /// <summary>
        /// 危险等级
        /// </summary>
        public DangerLevel Level { get; set; }

        /// <summary>
        /// 需要确认的次数
        /// </summary>
        public int ConfirmCount { get; set; } = 1;

        /// <summary>
        /// 危险描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 分类（filesystem/disk/system/process/firewall/network/privilege/service/config/git/docker/package/audit/user/ssh）
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 否决模式（正则，可选）——匹配到命令时撤销本条规则的命中。
        /// 黑名单正则最容易死在“太宽”上（rm -rf / 顺带干掉 /tmp/x），有了它用户不必关掉整条规则，
        /// 只需把已知安全的形态摘出去。只对自定义规则生效；内置规则只读（改精度走代码）。
        /// </summary>
        public string Exclude { get; set; }
    }

    /// <summary>
    /// 匹配类型
    /// </summary>
    public enum PatternType
    {
        /// <summary>
        /// 精确匹配（去除首尾空格后比较）
        /// </summary>
        Equals,

        /// <summary>
        /// 包含匹配
        /// </summary>
        Contains,

        /// <summary>
        /// 正则表达式匹配
        /// </summary>
        Regex
    }

    /// <summary>
    /// 危险等级
    /// </summary>
    public enum DangerLevel
    {
        /// <summary>
        /// 中等风险——可能影响服务或数据，确认 1 次
        /// </summary>
        Medium = 0,

        /// <summary>
        /// 高风险——数据丢失或服务中断，确认 2 次
        /// </summary>
        High = 1,

        /// <summary>
        /// 严重风险——系统毁灭性操作，确认 3 次
        /// </summary>
        Critical = 2
    }
}
