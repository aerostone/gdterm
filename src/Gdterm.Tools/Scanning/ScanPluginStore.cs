using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// </summary>
    public class ScanPluginStore : IDisposable
    {
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

                // 安全约束：脚本必须落在本插件目录内，禁止 ../ 逃逸
                var scriptPath = Path.GetFullPath(Path.Combine(pluginDir, manifest.ScriptFile));
                var dirFull = Path.GetFullPath(pluginDir);
                if (!scriptPath.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
                    return BadPlugin(manifest.Id, manifest, source, "scriptFile 越出插件目录");

                if (!File.Exists(scriptPath))
                    return BadPlugin(manifest.Id, manifest, source, "脚本不存在: " + manifest.ScriptFile);

                if (manifest.TimeoutSeconds <= 0) manifest.TimeoutSeconds = 60;

                return new ScanPlugin
                {
                    Manifest = manifest,
                    ScriptPath = scriptPath,
                    Source = source,
                    LoadError = null
                };
            }
            catch (Exception ex)
            {
                return BadPlugin(id, null, source, "清单解析失败: " + ex.Message);
            }
        }

        private static ScanPlugin BadPlugin(string id, ScanPluginManifest manifest, string source, string error)
        {
            return new ScanPlugin { Manifest = manifest, Source = source, LoadError = error };
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
                        continue;
                    }
                    RefreshOutdatedBuiltin(def, manifestPath, scriptPath);
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

                File.WriteAllText(manifestPath, def.ManifestJson, new System.Text.UTF8Encoding(false));
                File.WriteAllText(scriptPath, def.ScriptContent, new System.Text.UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 更新失败保留旧文件——旧版能跑总比损坏强
            }
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
