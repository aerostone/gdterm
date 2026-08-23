using System.Collections.Generic;

namespace Gdterm.Tools.Scanning
{
    /// <summary>内置扫描插件定义（内嵌资源）。</summary>
    public class BuiltinPluginDef
    {
        public string Id { get; set; }
        public string ManifestJson { get; set; }
        public string ScriptFile { get; set; }
        public string ScriptContent { get; set; }
    }

    /// <summary>
    /// 内置插件集：首次启动物化到 plugins\scanner\&lt;id&gt;\，之后归用户所有
    /// （可编辑、可停用 enabled=false；删除后下次启动恢复）。
    /// 脚本输出契约：FINDING|&lt;severity&gt;|&lt;title&gt;|&lt;detail&gt;。
    /// </summary>
    public static class BuiltinPlugins
    {
        public static IEnumerable<BuiltinPluginDef> All()
        {
            yield return SensitiveFiles;
            yield return CertAudit;
            yield return NtpStatus;
            yield return LinuxHealth;
        }

        // ===== 1. 本机敏感文件快扫 =====

        public static readonly BuiltinPluginDef SensitiveFiles = new BuiltinPluginDef
        {
            Id = "local-sensitive-files",
            ScriptFile = "scan.ps1",
            ManifestJson = @"{
  ""id"": ""local-sensitive-files"",
  ""name"": ""本机敏感文件快扫"",
  ""description"": ""扫描用户目录下 .ssh/.aws/.kube/.docker 等凭据目录中的私钥与疑似密钥明文。快速启发式，深度扫描请用 敏感信息扫描 工具。"",
  ""category"": ""安全"",
  ""targets"": [ ""windows"" ],
  ""scriptFile"": ""scan.ps1"",
  ""timeoutSeconds"": 120,
  ""version"": ""1.1"",
  ""enabled"": true
}",
            // 注意：PS 单引号字符串内不能出现裸单引号——正则一律避开引号字符类
            ScriptContent = @"$maxFileBytes = 2MB
$maxDirs = 20
$dirs = @(
  (Join-Path $env:USERPROFILE '.ssh'),
  (Join-Path $env:USERPROFILE '.aws'),
  (Join-Path $env:USERPROFILE '.azure'),
  (Join-Path $env:USERPROFILE '.kube'),
  (Join-Path $env:USERPROFILE '.gnupg'),
  (Join-Path $env:USERPROFILE '.gitconfig')
)
$patterns = @(
  @{ n='私钥文件'; r='-----BEGIN (RSA |OPENSSH |EC |DSA )?PRIVATE KEY-----'; s='critical' },
  @{ n='AWS AccessKey'; r='AKIA[0-9A-Z]{16}'; s='critical' },
  @{ n='GitHub Token'; r='gh[pousr]_[A-Za-z0-9]{20,}'; s='critical' },
  @{ n='Slack Token'; r='xox[baprs]-[A-Za-z0-9-]{10,}'; s='high' },
  @{ n='疑似密钥明文赋值'; r='(?i)(api[_-]?key|secret|passwd|password)[=:]\S{8,}'; s='medium' }
)
$count = 0
foreach ($d in $dirs) {
  if (-not (Test-Path $d)) { continue }
  $files = @()
  if (Test-Path $d -PathType Container) {
    $files = Get-ChildItem $d -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 300
  } else { $files = @(Get-Item $d) }
  Write-Output ('扫描: ' + $d + '（' + @($files).Count + ' 个文件）')
  foreach ($f in $files) {
    if ($count -ge 200) { break }
    if ($f.Length -gt $maxFileBytes) { continue }
    $text = $null
    # 兼容 PS 2.0（Win7/2008R2 原生）：不用 Get-Content -Raw（PS 3+ 才有），直接 .NET 读取
    try { if ($f.Length -gt 0) { $text = [System.IO.File]::ReadAllText($f.FullName) } } catch { continue }
    if ($null -eq $text) { continue }
    foreach ($p in $patterns) {
      if ($text -match $p.r) {
        Write-Output ('FINDING|' + $p.s + '|' + $p.n + '|' + $f.FullName)
        $count++
      }
    }
  }
}
Write-Output ('完成: 命中 ' + $count + ' 处')
"
        };

        // ===== 2. 本机证书体检 =====

        public static readonly BuiltinPluginDef CertAudit = new BuiltinPluginDef
        {
            Id = "local-cert-audit",
            ScriptFile = "scan.ps1",
            ManifestJson = @"{
  ""id"": ""local-cert-audit"",
  ""name"": ""本机证书体检"",
  ""description"": ""检查个人证书存储（本机/当前用户）：已过期、30天内到期、弱密钥(RSA<2048)、MD5 签名算法。"",
  ""category"": ""安全"",
  ""targets"": [ ""windows"" ],
  ""scriptFile"": ""scan.ps1"",
  ""timeoutSeconds"": 60,
  ""version"": ""1.0"",
  ""enabled"": true
}",
            ScriptContent = @"$stores = @('Cert:\LocalMachine\My', 'Cert:\CurrentUser\My')
foreach ($store in $stores) {
  if (-not (Test-Path $store)) { continue }
  $certs = Get-ChildItem $store -ErrorAction SilentlyContinue
  Write-Output ('检查 ' + $store + '（' + @($certs).Count + ' 张证书）')
  foreach ($c in $certs) {
    try {
      $days = (New-TimeSpan -Start (Get-Date) -End $c.NotAfter).Days
      if ($days -lt 0) {
        Write-Output ('FINDING|high|证书已过期|' + $c.Subject + ' 过期于 ' + $c.NotAfter.ToString('yyyy-MM-dd') + ' [' + $store + ']')
      } elseif ($days -le 30) {
        Write-Output ('FINDING|medium|证书30天内到期|' + $c.Subject + ' 还剩 ' + $days + ' 天 [' + $store + ']')
      }
    } catch { }
    try {
      $keySize = $c.PublicKey.Key.KeySize
      if ($keySize -gt 0 -and $keySize -lt 2048) {
        Write-Output ('FINDING|high|弱密钥长度|' + $c.Subject + ' 仅 ' + $keySize + ' 位 [' + $store + ']')
      }
    } catch { }
    try {
      if ($c.SignatureAlgorithm -and $c.SignatureAlgorithm.FriendlyName -match 'MD5') {
        Write-Output ('FINDING|high|MD5 签名算法|' + $c.Subject + ' [' + $store + ']')
      }
    } catch { }
  }
}
Write-Output '证书体检完成'
"
        };

        // ===== 3. 本机时间同步状态 =====

        public static readonly BuiltinPluginDef NtpStatus = new BuiltinPluginDef
        {
            Id = "local-ntp-status",
            ScriptFile = "scan.ps1",
            ManifestJson = @"{
  ""id"": ""local-ntp-status"",
  ""name"": ""本机时间同步状态"",
  ""description"": ""检查 W32Time 服务状态与 NTP 服务器配置；时间偏差过大会导致 Kerberos/审计时间戳异常。"",
  ""category"": ""系统"",
  ""targets"": [ ""windows"" ],
  ""scriptFile"": ""scan.ps1"",
  ""timeoutSeconds"": 60,
  ""version"": ""1.0"",
  ""enabled"": true
}",
            ScriptContent = @"$svc = Get-Service W32Time -ErrorAction SilentlyContinue
if ($svc -eq $null) {
  Write-Output 'FINDING|low|时间服务不存在|W32Time 服务未安装'
  exit 0
}
if ($svc.Status -ne 'Running') {
  Write-Output ('FINDING|medium|时间同步服务未运行|W32Time 当前状态 ' + $svc.Status)
} else {
  Write-Output 'W32Time 服务运行中'
}
try {
  $status = w32tm /query /status 2>&1 | Out-String
  Write-Output $status.TrimEnd()
} catch { Write-Output ('查询状态失败: ' + $_.Exception.Message) }
try {
  $cfg = w32tm /query /configuration 2>&1 | Out-String
  $line = $null
  foreach ($l in ($cfg -split '\r?\n')) {
    if ($l -match '^NtpServer') { $line = $l; break }
  }
  if ($line) {
    $parts = $line -split ':', 2
    $srv = ''
    if ($parts.Length -gt 1) { $srv = $parts[1].Trim() }
    $srv = ($srv -replace '\([^)]*\)\s*$', '').Trim()
    Write-Output ('配置的 NTP 服务器: ' + $srv)
    if ($srv.Length -eq 0 -or $srv -match '^time\.windows\.com') {
      Write-Output 'FINDING|info|使用默认 NTP 源|建议改用企业内部或公共 NTP（如 ntp.aliyun.com）'
    }
  } else {
    Write-Output '未找到 NtpServer 配置项'
  }
} catch { Write-Output ('查询配置失败: ' + $_.Exception.Message) }
Write-Output 'NTP 状态检查完成'
"
        };

        // ===== 4. Linux 健康基线 =====

        public static readonly BuiltinPluginDef LinuxHealth = new BuiltinPluginDef
        {
            Id = "linux-health-basics",
            ScriptFile = "scan.sh",
            ManifestJson = @"{
  ""id"": ""linux-health-basics"",
  ""name"": ""Linux 健康基线"",
  ""description"": ""远端 Linux：磁盘水位、负载、内存压力、失败的 systemd 单元、SSH 私钥权限。"",
  ""category"": ""系统"",
  ""targets"": [ ""linux"" ],
  ""scriptFile"": ""scan.sh"",
  ""timeoutSeconds"": 60,
  ""version"": ""1.0"",
  ""enabled"": true
}",
            ScriptContent = @"#!/bin/sh
# --- 磁盘水位 ---
df -P 2>/dev/null | tail -n +2 | while read -r fs size used avail pct mnt; do
  p=${pct%%%}
  case ""$mnt"" in /tmp|/dev|/run|/sys|/proc|/boot/efi) continue ;; esac
  if [ ""$p"" -ge 90 ] 2>/dev/null; then
    echo ""FINDING|high|磁盘即将写满|$mnt 使用率 $pct""
  elif [ ""$p"" -ge 80 ] 2>/dev/null; then
    echo ""FINDING|medium|磁盘水位偏高|$mnt 使用率 $pct""
  fi
done

# --- 负载 ---
if [ -r /proc/loadavg ]; then
  load=$(cut -d' ' -f1 /proc/loadavg)
  cores=$(nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo 2>/dev/null || echo 1)
  awk -v l=""$load"" -v c=""$cores"" 'BEGIN {
    if (l+0 > c*0.9) printf ""FINDING|high|系统负载过高|load %.2f / %d 核\n"", l, c
    else printf ""负载 %.2f / %d 核\n"", l, c
  }'
fi

# --- 内存 ---
if [ -r /proc/meminfo ]; then
  total=$(awk '/MemTotal/{print int($2/1024)}' /proc/meminfo)
  avail=$(awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo)
  if [ ""$total"" -gt 0 ] 2>/dev/null && [ -n ""$avail"" ]; then
    usedp=$(( (total - avail) * 100 / total ))
    if [ ""$usedp"" -ge 90 ]; then echo ""FINDING|high|内存压力|已用 ${usedp}% (${avail}MB 可用)""
    elif [ ""$usedp"" -ge 80 ]; then echo ""FINDING|medium|内存偏高|已用 ${usedp}% (${avail}MB 可用)""
    fi
  fi
fi

# --- 失败的 systemd 单元 ---
if command -v systemctl >/dev/null 2>&1; then
  failed=$(systemctl --failed --no-legend 2>/dev/null | wc -l)
  if [ ""$failed"" -gt 0 ]; then
    echo ""FINDING|medium|存在失败的 systemd 单元|共 ${failed} 个，远端执行 systemctl --failed 查看""
  fi
fi

# --- SSH 私钥权限 ---
for keydir in /root/.ssh ""$HOME/.ssh""; do
  [ -d ""$keydir"" ] || continue
  for f in ""$keydir""/id_*; do
    [ -f ""$f"" ] || continue
    case ""$f"" in *.pub) continue ;; esac
    perm=$(stat -c %a ""$f"" 2>/dev/null)
    if [ -n ""$perm"" ] && [ ""$perm"" != 600 ] && [ ""$perm"" != 400 ]; then
      echo ""FINDING|high|私钥权限过宽|$f 权限为 $perm（应为 600）""
    fi
  done
done

echo 'Linux 基线检查完成'
"
        };
    }
}
