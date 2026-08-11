using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Gdterm.Core.Security
{
    /// <summary>
    /// Shell 参数转义与白名单校验——用于 SSH RunCommand 拼接远端 shell 命令。
    ///
    /// 设计目标：用户输入只作"字面值"嵌入，不作"语义"嵌入。
    ///   - ShellQuote: POSIX 单引号包裹，单引号本身用 '\'' 切断转义——bash/dash/zsh 通用安全形式。
    ///   - ValidateName / ValidateRepoName / ValidateNtpServer / ValidateRepoUrl / ValidateLocalPath:
    ///     各场景白名单——拒绝 shell 元字符，限制长度。
    ///   - ValidatePermission: chmod 权限字符串白名单（八进制 000-777 或符号 [ugoa]*[+-=][rwxst]*）。
    ///   - ValidateOwner: chown user:group 白名单。
    ///
    /// 注意：本类是 last-layer 防护，调用方仍应优先用 SftpClient API（SetAttributes）而非 shell。
    /// </summary>
    public static class ShellArgument
    {
        /// <summary>POSIX 安全引号——单引号内除 ' 外都是字面字符。' 用 '\'' 转义（关闭引号 + \' + 重开引号）。</summary>
        public static string ShellQuote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "''";
            if (value.IndexOf('\0') >= 0)
                throw new ArgumentException("Shell 参数不能含 NUL 字符", nameof(value));
            var sb = new StringBuilder(value.Length + 8);
            sb.Append('\'');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\'')
                    sb.Append("'\\''");
                else
                    sb.Append(c);
            }
            sb.Append('\'');
            return sb.ToString();
        }

        /// <summary>白名单校验：标识符/文件名。允许 [A-Za-z0-9_.-]，长度 ≤ 64，不允许路径分隔符 / 或空格。</summary>
        public static string ValidateName(string name, string fieldName = "name")
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(fieldName + " 不能为空", fieldName);
            if (name.Length > 64)
                throw new ArgumentException(fieldName + " 长度超过 64 字符", fieldName);
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_.\\-]+$"))
                throw new ArgumentException(fieldName + " 含非法字符（只允许字母、数字、._-）", fieldName);
            return name;
        }

        /// <summary>白名单校验：repo name（与 ValidateName 相同准则，yum repo ID 必须严格）。</summary>
        public static string ValidateRepoName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("repo name 不能为空", nameof(name));
            if (name.Length > 64)
                throw new ArgumentException("repo name 长度超过 64 字符", nameof(name));
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_.\\-]+$"))
                throw new ArgumentException("repo name 含非法字符（只允许字母、数字、._-）", nameof(name));
            return name;
        }

        /// <summary>NTP 服务器：FQDN/IPv4/IPv6。允许 [A-Za-z0-9.:-]，长度 ≤ 255。</summary>
        public static string ValidateNtpServer(string server)
        {
            if (string.IsNullOrEmpty(server))
                throw new ArgumentException("NTP 服务器不能为空", nameof(server));
            if (server.Length > 255)
                throw new ArgumentException("NTP 服务器地址过长", nameof(server));
            if (!Regex.IsMatch(server, "^[A-Za-z0-9.:\\-]+$"))
                throw new ArgumentException("NTP 服务器含非法字符（只允许字母、数字、.:,-）", nameof(server));
            return server;
        }

        /// <summary>repo URL：白名单协议 + 合法 URL 字符。必须以协议开头。</summary>
        public static string ValidateRepoUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("repo URL 不能为空", nameof(url));
            if (url.Length > 2048)
                throw new ArgumentException("repo URL 过长", nameof(url));
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("repo URL 必须以 http:// https:// ftp:// 或 file:// 开头", nameof(url));
            }
            if (url.IndexOfAny(new[] { '\'', '"', '`', '$', '\\', ';', '|', '&', '\n', '\r', '\0' }) >= 0)
                throw new ArgumentException("repo URL 含 shell 元字符", nameof(url));
            return url;
        }

        /// <summary>本地路径校验：必须非空，无 shell 元字符。允许字母数字 _ . - / \ : 空格 中文等一般路径字符。</summary>
        public static string ValidateLocalPath(string path, string fieldName = "path")
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException(fieldName + " 不能为空", fieldName);
            if (path.Length > 1024)
                throw new ArgumentException(fieldName + " 过长", fieldName);
            if (path.IndexOfAny(new[] { '\'', '"', '`', '$', '\\', ';', '|', '&', '\n', '\r', '\0', '*' }) >= 0)
                throw new ArgumentException(fieldName + " 含 shell 元字符", fieldName);
            return path;
        }

        /// <summary>chmod 权限字符串白名单：
        /// - 八进制：000-777（含 sticky/setuid 位时最多 4 位 0-7）
        /// - 符号模式：[ugoa]*[+-=][rwxstST]* （可含逗号分隔多段，如 u+x,g-w）</summary>
        public static string ValidatePermission(string permission)
        {
            if (string.IsNullOrEmpty(permission))
                throw new ArgumentException("权限不能为空", nameof(permission));
            if (permission.Length > 32)
                throw new ArgumentException("权限字符串过长", nameof(permission));
            // 八进制：1-4 位 0-7
            if (Regex.IsMatch(permission, "^[0-7]{1,4}$"))
                return permission;
            // 符号模式：[ugoa]*[+-=][rwxstST]*（，分隔）
            if (Regex.IsMatch(permission, "^[ugoa]*[+-=][rwxstST]*(,[ugoa]*[+-=][rwxstST]*)*$"))
                return permission;
            throw new ArgumentException("权限字符串非法（只允许八进制如 755 或符号如 u+x,g-w）", nameof(permission));
        }

        /// <summary>chown 的 user[:group] 白名单。允许字母数字 _ . - ，冒号分隔 user:group，长度 ≤ 64 每段。</summary>
        public static string ValidateOwner(string owner, string group = null)
        {
            ValidateUnixName(owner, "owner");
            if (!string.IsNullOrEmpty(group))
                ValidateUnixName(group, "group");
            return group != null ? owner + ":" + group : owner;
        }

        private static void ValidateUnixName(string name, string fieldName)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException(fieldName + " 不能为空", fieldName);
            if (name.Length > 64)
                throw new ArgumentException(fieldName + " 过长", fieldName);
            // POSIX user/group 名字符集：字母开头，后跟字母数字 _ - 或 $
            if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_\\-]*"))
                throw new ArgumentException(fieldName + " 含非法字符（POSIX 用户名）", fieldName);
        }
    }
}
