using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using System.Web.Script.Serialization;

namespace Gdterm.Tools.Scanning
{
    /// <summary>
    /// 扫描插件仓库——发现、校验、热更新。
    /// 两个信任根：
    ///   builtin：BaseDirectory\plugins\scanner\（首次启动从内嵌资源物化，用户可编辑/停用）
    ///   user   ：data\plugins\scanner\（用户自建）
    /// 热更新：FileSystemWatcher 监控两个根（递归），去抖后重载并触发 PluginsReloaded。
    /// 签名信任：官方 RSA-3072 公钥钉死在程序集内；Trusted 静默执行，
    /// Unsigned 首次运行需用户确认（按内容哈希记账），Invalid（内容与签名不符）硬拒绝。
    /// </summary>
    public class ScanPluginStore : IDisposable
    {
        /// <summary>官方发布公钥（gdterm-official-1，RSA-3072）。私钥离线保存，不进仓库/CI。</summary>
        internal const string OfficialPublicKeyXml =
            "<RSAKeyValue><Modulus>vrrLhL198Q4ERm/hX8vVZx+bw8asZtb0CeBkOPZ/A/t/rRyZxzATY0HKlDNN2heGcySCbk/n1Yh3GZSbBFFFma2Oxa0c34e8PXSNw9rtKIiPgdGnAmMBUEEG6x6CRySRMXrerDGHcDZmPbXdlHpv1Pc8FeTE6aqZWYvKHQxqqUvKSegmFKUPW4QSJFqbiOX2k114w5Qgl9etN1u1J6fjmkhsL+TLn8rPCY/j483KcjLyE5ps+wrGXBHCifsdoEhKji3Ur44O7JgACaQG5lBRXsEIQ8iitm2/dBTAGQ050CdBAAWbmc5lofdR9fXsTyTaNiSM2kvojgQwiIGBflNg74JX8op3hHQDG1QaTsdKMDpW5XHi/h/gOafddUwZmPMzoeFq/XMOaK3txbtGxM4d3EBOb5CRRWnJjtQWssdtG/K6CWpFLy25lHZX7h6eIGEv6hhQ8QcWXR0mn5qKAc5SFhwFs/65M6De/LTqimO/xuaVI387Mnd4leeOCcVe3x3N</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private readonly string _builtinRoot;
        private readonly string _userRoot;
        private readonly List<ScanPlugin> _plugins = new List<ScanPlugin>();
        private readonly object _gate = new object();

        private FileSystemWatcher _watcherBuiltin;
        private FileSystemWatcher _watcherUser;
        private System.Timers.Timer _debounce;

        /// <summary>插件集变化（含热更）后触发（UI 线程自行封送）。</summary>
        public event EventHandler PluginsReloaded;

        public ScanPluginStore()
            : this(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "scanner"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "plugins", "scanner"))
        {
        }

        public ScanPluginStore(string builtinRoot, string userRoot)
        {
            _builtinRoot = builtinRoot ?? throw new ArgumentNullException("builtinRoot");
            _userRoot = userRoot ?? throw new ArgumentNullException("userRoot");
        }

        /// <summary>当前插件快照（内置在前）。</summary>
        public List<ScanPlugin> Plugins
        {
            get
            {
                lock (_gate) return _plugins.ToList();
            }
        }

        /// <summary>是否已启动（Start 幂等，只生效一次）。</summary>
        public bool Started { get; private set; }

        /// <summary>物化内置插件 → 启动监控 → 首次加载。重复调用安全。</summary>
        public void Start()
        {
            if (Started) return;
            MaterializeBuiltins();
            StartWatching();
            Reload();
            Started = true;
        }

        // ===== 发现与加载 =====

        public void Reload()
        {
            var loaded = new List<ScanPlugin>();
            loaded.AddRange(LoadRoot(_builtinRoot, "builtin"));
            loaded.AddRange(LoadRoot(_userRoot, "user"));

            lock (_gate)
            {
                _plugins.Clear();
                _plugins.AddRange(loaded);
            }
            var handler = PluginsReloaded;
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        /// <summary>扫描一个信任根下的一级子目录，每个含 manifest.json 的目录即一个插件。</summary>
        private IEnumerable<ScanPlugin> LoadRoot(string root, string source)
        {
            var result = new List<ScanPlugin>();
            try
            {
                if (!Directory.Exists(root)) return result;
                foreach (var dir in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(LoadOne(dir, source));
                }
            }
            catch (Exception)
            {
                // 根不可读时静默返回空——UI 会显示为无插件
            }
            return result;
        }

        private static ScanPlugin LoadOne(string pluginDir, string source)
        {
            var id = Path.GetFileName(pluginDir);
            try
            {
                var manifestPath = Path.Combine(pluginDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    return BadPlugin(id, null, source, "缺少 manifest.json");

                var json = File.ReadAllText(manifestPath);
                
                var serializer = new JavaScriptSerializer();
                var manifest = serializer.Deserialize<ScanPluginManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                    return BadPlugin(id, null, source, "manifest.json 缺少 id");
                if (string.IsNullOrWhiteSpace(manifest.Name))
                    return BadPlugin(manifest.Id, manifest, source, "manifest.json 缺少 name");
                if (manifest.Targets == null || manifest.Targets.Count == 0)
                    return BadPlugin(manifest.Id, manifest, source, "targets 为空（windows/linux）");
                if (string.IsNullOrWhiteSpace(manifest.ScriptFile))
                    return BadPlugin(manifest.Id, manifest, source, "缺少 scriptFile");

                // 安全约束：脚本必须落在本插件目录内，禁止 ../ 逃逸；
                // 前缀比对必须带目录分隔符，避免同级目录 scanner-extra 之类的前缀绕过
                var scriptPath = Path.GetFullPath(Path.Combine(pluginDir, manifest.ScriptFile));
                var dirFull = Path.GetFullPath(pluginDir);
                var dirPrefix = dirFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? dirFull : dirFull + Path.DirectorySeparatorChar;
                if (!scriptPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                    return BadPlugin(manifest.Id, manifest, source, "scriptFile 越出插件目录");

                if (!File.Exists(scriptPath))
                    return BadPlugin(manifest.Id, manifest, source, "脚本不存在: " + manifest.ScriptFile);

                if (manifest.TimeoutSeconds <= 0) manifest.TimeoutSeconds = 60;

                return new ScanPlugin
                {
                    Manifest = manifest,
                    RawId = id,
                    ScriptPath = scriptPath,
                    Source = source,
                    LoadError = null,
                    Trust = VerifyTrust(manifestPath, scriptPath),
                    VerifiedScriptSha256 = ScriptSha256Hex(scriptPath),
                    VerifiedManifestSha256 = FileSha256Hex(manifestPath)
                };
            }
            catch (Exception ex)
            {
                return BadPlugin(id, null, source, "清单解析失败: " + ex.Message);
            }
        }

        private static ScanPlugin BadPlugin(string id, ScanPluginManifest manifest, string source, string error)
        {
            // RawId 保留目录名/清单 id：加载失败的插件也以稳定 key 出现在列表与去重逻辑中
            return new ScanPlugin { Manifest = manifest, RawId = id, Source = source, LoadError = error };
        }

        // ===== 官方签名验证（RSA-3072 + SHA256，公钥钉死在程序集） =====
        // 规范负载 = hex(sha256(manifest.json)) || 0x00 || hex(sha256(脚本))；
        // plugin.sig 记录两个哈希便于精确报错"哪个文件被改过"。
        // 判定：无 sig → Unsigned；keyId 非官方 → Unsigned（外来签名视同未签）；
        //       官方 keyId 但哈希不匹配/验签失败/sig 损坏 → Invalid（篡改信号，硬拒绝）。

        private const string OfficialKeyId = "gdterm-official-1";

        private sealed class SigEnvelope
        {
            public string alg { get; set; }
            public string keyId { get; set; }
            public string manifest { get; set; }
            public string script { get; set; }
            public string signature { get; set; }
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(data.Length * 2);
                foreach (var b in sha.ComputeHash(data)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>计算文件当前内容的 SHA256（十六进制小写）；读不到返回 null。</summary>
        internal static string FileSha256Hex(string filePath)
        {
            try { return Sha256Hex(File.ReadAllBytes(filePath)); }
            catch { return null; }
        }

        /// <summary>计算脚本文件当前内容的 SHA256（十六进制小写）；读不到返回 null。</summary>
        internal static string ScriptSha256Hex(string scriptPath)
        {
            return FileSha256Hex(scriptPath);
        }

        /// <summary>规范负载字节：hex(manifestSha) || 0x00 || hex(scriptSha)。批准台账复用其再散列。</summary>
        internal static byte[] BuildCanonicalPayload(string manifestPath, string scriptPath)
        {
            var d1 = Sha256Hex(File.ReadAllBytes(manifestPath));
            var d2 = Sha256Hex(File.ReadAllBytes(scriptPath));
            return Encoding.ASCII.GetBytes(d1 + "\0" + d2);
        }

        private static ScanTrust VerifyTrust(string manifestPath, string scriptPath)
        {
            try
            {
                var sigPath = Path.Combine(Path.GetDirectoryName(manifestPath), "plugin.sig");
                if (!File.Exists(sigPath)) return ScanTrust.Unsigned;

                var env = new JavaScriptSerializer().Deserialize<SigEnvelope>(File.ReadAllText(sigPath));
                if (env == null || env.signature == null || env.manifest == null || env.script == null)
                    return ScanTrust.Invalid;
                if (!string.Equals(env.keyId, OfficialKeyId, StringComparison.Ordinal))
                    return ScanTrust.Unsigned;
                if (!string.Equals(env.alg, "RSA-SHA256-PKCS1", StringComparison.Ordinal))
                    return ScanTrust.Invalid;

                var payload = BuildCanonicalPayload(manifestPath, scriptPath);
                var text = Encoding.ASCII.GetString(payload);
                var sep = text.IndexOf('\0');
                if (!string.Equals(text.Substring(0, sep), env.manifest.ToLowerInvariant(), StringComparison.Ordinal)
                    || !string.Equals(text.Substring(sep + 1), env.script.ToLowerInvariant(), StringComparison.Ordinal))
                    return ScanTrust.Invalid; // 内容与签名记录的哈希不符——能精确知道被改过

                using (var rsa = RSA.Create())
                {
                    rsa.FromXmlString(OfficialPublicKeyXml);
                    var ok = rsa.VerifyData(payload, Convert.FromBase64String(env.signature),
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return ok ? ScanTrust.Trusted : ScanTrust.Invalid;
                }
            }
            catch
            {
                return ScanTrust.Invalid; // sig 存在但读不了/解析不了——按篡改处理
            }
        }

        // ===== 未签名插件批准台账（按内容哈希记账，内容变更自动失效重问） =====

        private readonly object _ledgerGate = new object();
        private List<ApprovedEntry> _approved;

        private sealed class ApprovedEntry
        {
            public string id { get; set; }
            public string hash { get; set; }
        }

        private string LedgerPath
        {
            get { return Path.Combine(Path.GetDirectoryName(_userRoot), "config", "scanner-approved.json"); }
        }

        private List<ApprovedEntry> LoadLedger()
        {
            lock (_ledgerGate)
            {
                if (_approved != null) return _approved;
                _approved = new List<ApprovedEntry>();
                try
                {
                    if (File.Exists(LedgerPath))
                    {
                        var box = new JavaScriptSerializer().Deserialize<Dictionary<string, List<ApprovedEntry>>>(File.ReadAllText(LedgerPath));
                        if (box != null && box.ContainsKey("approved") && box["approved"] != null) _approved = box["approved"];
                    }
                }
                catch { /* 台账损坏视为空——最多重新确认一次 */ }
                return _approved;
            }
        }

        /// <summary>该未签名插件是否已被用户批准过（按 id + 内容哈希记账）。</summary>
        public bool IsApproved(ScanPlugin plugin)
        {
            if (plugin == null || plugin.Trust == ScanTrust.Trusted) return true;
            var hash = CanonicalHash(plugin);
            if (hash == null) return false;
            lock (_ledgerGate)
            {
                foreach (var e in LoadLedger())
                {
                    if (e != null && string.Equals(e.id, plugin.Id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.hash, hash, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>记录用户对未签名插件的批准。</summary>
        public void Approve(ScanPlugin plugin)
        {
            var hash = CanonicalHash(plugin);
            if (hash == null) return;
            lock (_ledgerGate)
            {
                var list = LoadLedger();
                list.RemoveAll(e => e != null && string.Equals(e.id, plugin.Id, StringComparison.OrdinalIgnoreCase));
                list.Add(new ApprovedEntry { id = plugin.Id, hash = hash });
                try
                {
                    var dir = Path.GetDirectoryName(LedgerPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(LedgerPath,
                        new JavaScriptSerializer().Serialize(new Dictionary<string, List<ApprovedEntry>> { { "approved", list } }),
                        new UTF8Encoding(false));
                }
                catch { /* 只读盘写不进——本次会话内仍生效 */ }
            }
        }

        private static string CanonicalHash(ScanPlugin plugin)
        {
            try
            {
                var manifestPath = Path.Combine(Path.GetDirectoryName(plugin.ScriptPath), "manifest.json");
                return Sha256Hex(BuildCanonicalPayload(manifestPath, plugin.ScriptPath));
            }
            catch { return null; }
        }

        // ===== 内置物化 =====

        /// <summary>内置插件缺失时从程序集内嵌内容写出；已存在则不覆盖（尊重用户修改/禁用）。</summary>
        private void MaterializeBuiltins()
        {
            try
            {
                foreach (var def in BuiltinPlugins.All())
                {
                    var dir = Path.Combine(_builtinRoot, def.Id);
                    var manifestPath = Path.Combine(dir, "manifest.json");
                    var scriptPath = Path.Combine(dir, def.ScriptFile);
                    if (!File.Exists(manifestPath) || !File.Exists(scriptPath))
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(manifestPath, def.ManifestJson, new System.Text.UTF8Encoding(false));
                        File.WriteAllText(scriptPath, def.ScriptContent, new System.Text.UTF8Encoding(false));
                        WriteBuiltinSignature(def, Path.Combine(dir, "plugin.sig"));
                        continue;
                    }
                    RefreshOutdatedBuiltin(def, manifestPath, scriptPath);
                    BackfillPristineSignature(def, manifestPath, scriptPath);
                }
            }
            catch (Exception)
            {
                // 只读安装盘等场景物化失败不致命——内置仍可经 user 根补充
            }
        }

        /// <summary>
        /// 版本感知更新：磁盘上的内置插件版本旧于程序集内嵌版时刷新，
        /// 旧脚本备份为 *.bak（用户改动不丢）；版本一致或磁盘更新则不动。
        /// </summary>
        private static void RefreshOutdatedBuiltin(BuiltinPluginDef def, string manifestPath, string scriptPath)
        {
            try
            {
                var diskVersion = ReadManifestVersion(manifestPath);
                if (CompareVersions(diskVersion, ExtractVersion(def.ManifestJson)) >= 0) return;

                var bakScript = scriptPath + ".bak";
                if (File.Exists(bakScript)) File.Delete(bakScript);
                File.Copy(scriptPath, bakScript, true);

                var bakManifest = manifestPath + ".bak";
                if (File.Exists(bakManifest)) File.Delete(bakManifest);
                File.Copy(manifestPath, bakManifest, true);

                File.WriteAllText(manifestPath, def.ManifestJson, new System.Text.UTF8Encoding(false));
                File.WriteAllText(scriptPath, def.ScriptContent, new System.Text.UTF8Encoding(false));
                WriteBuiltinSignature(def, Path.Combine(Path.GetDirectoryName(manifestPath), "plugin.sig"));
            }
            catch (Exception)
            {
                // 更新失败保留旧文件——旧版能跑总比损坏强
            }
        }

        /// <summary>升级安装补齐：文件存在且与内嵌逐字节一致（未被用户改过）但缺 .sig 时补写；改过的保持 Unsigned 走确认流。</summary>
        private static void BackfillPristineSignature(BuiltinPluginDef def, string manifestPath, string scriptPath)
        {
            try
            {
                var sigPath = Path.Combine(Path.GetDirectoryName(manifestPath), "plugin.sig");
                if (File.Exists(sigPath)) return;
                if (def.SignatureJson == null) return;
                if (!FileMatchesContent(manifestPath, def.ManifestJson)) return;
                if (!FileMatchesContent(scriptPath, def.ScriptContent)) return;
                WriteBuiltinSignature(def, sigPath);
            }
            catch { }
        }

        private static bool FileMatchesContent(string path, string expected)
        {
            try { return File.ReadAllBytes(path).SequenceEqual(new System.Text.UTF8Encoding(false).GetBytes(expected)); }
            catch { return false; }
        }

        private static void WriteBuiltinSignature(BuiltinPluginDef def, string sigPath)
        {
            if (def.SignatureJson == null) return;
            try { File.WriteAllText(sigPath, def.SignatureJson, new System.Text.UTF8Encoding(false)); }
            catch { /* 只读盘——下次启动再试 */ }
        }

        /// <summary>从 manifest JSON 文本提取 "version" 字段；解析失败视为 "0"。</summary>
        private static string ExtractVersion(string json)
        {
            if (json != null)
            {
                var m = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return "0";
        }

        /// <summary>从 manifest.json 文件提取 "version" 字段；解析失败视为 "0"。</summary>
        private static string ReadManifestVersion(string manifestPath)
        {
            try
            {
                foreach (var line in File.ReadLines(manifestPath))
                {
                    var m = Regex.Match(line, "\"version\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
            catch { }
            return "0";
        }

        /// <summary>按数字段比较版本号（1.2 > 1.10 按段比较为小于）；不可解析段按字符串比。</summary>
        private static int CompareVersions(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
            var sa = (a ?? "0").Split('.');
            var sb = (b ?? "0").Split('.');
            for (var i = 0; i < Math.Max(sa.Length, sb.Length); i++)
            {
                int na, nb;
                var ta = i < sa.Length ? sa[i] : "0";
                var tb = i < sb.Length ? sb[i] : "0";
                if (int.TryParse(ta, out na) && int.TryParse(tb, out nb))
                {
                    if (na != nb) return na.CompareTo(nb);
                }
                else
                {
                    var c = string.Compare(ta, tb, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
            }
            return 0;
        }

        // ===== 热更新监控 =====

        private void StartWatching()
        {
            lock (_gate)
            {
                if (_watcherBuiltin != null) return; // 已启动

                _debounce = new System.Timers.Timer(800) { AutoReset = false };
                _debounce.Elapsed += OnDebounceElapsed;

                _watcherBuiltin = CreateWatcher(_builtinRoot);
                _watcherUser = CreateWatcher(_userRoot);
            }
        }

        private FileSystemWatcher CreateWatcher(string root)
        {
            try
            {
                Directory.CreateDirectory(root);
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                w.Changed += OnFsEvent;
                w.Created += OnFsEvent;
                w.Deleted += OnFsEvent;
                w.Renamed += OnFsEvent;
                w.EnableRaisingEvents = true;
                return w;
            }
            catch
            {
                return null; // 根不可建时放弃该根的监控
            }
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            try { _debounce.Stop(); _debounce.Start(); } catch { }
        }

        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            Reload();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_watcherBuiltin != null) { _watcherBuiltin.Dispose(); _watcherBuiltin = null; }
                if (_watcherUser != null) { _watcherUser.Dispose(); _watcherUser = null; }
                if (_debounce != null) { _debounce.Dispose(); _debounce = null; }
            }
        }
    }
}
