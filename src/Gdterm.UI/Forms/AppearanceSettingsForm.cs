using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Gdterm.Terminal.Themes;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 外观设置：字体/字号/配色/行距提示。保存到 data/config/appearance.ini。
    /// 2026-09 AntdUI 版：AntdUI.Window + Input/Select/InputNumber/Checkbox。
    /// 布局：手工 y 流式（AntdUI 控件固定行高，本窗体 FixedDialog 不随字号伸缩）。
    /// </summary>
    public sealed class AppearanceSettingsForm : AntdUI.Window
    {
        private readonly string _iniPath;
        private AntdUI.Select _fontCombo;
        private AntdUI.InputNumber _sizeNum;
        private AntdUI.Select _cjkFontCombo;
        private AntdUI.Select _schemeCombo;
        private AntdUI.Checkbox _dpiAwareCheck;
        private AntdUI.Input _preview;
        private AntdUI.Button _btnOk;
        private AntdUI.Button _btnCancel;
        private AntdUI.Button _btnReset;
        // 界面字体（菜单/树/状态栏）
        private AntdUI.Select _uiFontCombo;
        private AntdUI.InputNumber _uiSizeNum;
        private AntdUI.Select _uiThemeCombo;

        public AppearanceSettings Result { get; private set; }

        public AppearanceSettingsForm(string configDir)
        {
            if (string.IsNullOrEmpty(configDir))
                configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config");
            Directory.CreateDirectory(configDir);
            _iniPath = Path.Combine(configDir, "appearance.ini");

            BuildUi();
            LoadCurrent();
        }

        private void BuildUi()
        {
            Text = "外观设置";
            Size = new Size(520, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int pad = 20;
            int colLabel = pad;
            int colValue = pad + 110;
            int valueW = 220;
            int rowH = 44;
            int y = 22;

            // —— 终端字体 ——
            Controls.Add(MakeLabel("终端字体", colLabel, y));
            _fontCombo = MakeSelect(valueW, FillTerminalFonts);
            _fontCombo.Location = new Point(colValue, y);
            Controls.Add(_fontCombo);
            y += rowH;

            // —— 字号 ——
            Controls.Add(MakeLabel("字号 (pt)", colLabel, y));
            _sizeNum = MakeNumber(8, 36, 12);
            _sizeNum.Location = new Point(colValue, y);
            Controls.Add(_sizeNum);
            y += rowH;

            // —— 配色方案 ——
            Controls.Add(MakeLabel("配色方案", colLabel, y));
            _schemeCombo = MakeSelect(valueW, null);
            foreach (var name in new[]
            {
                "Classic", "HighContrast", "SolarizedDark", "Monokai", "Dracula", "GreenTerminal", "Light"
            })
                _schemeCombo.Items.Add(name);
            _schemeCombo.Location = new Point(colValue, y);
            Controls.Add(_schemeCombo);
            y += rowH;

            // —— 中日韩补充字体（Xshell 风格双字体）——
            Controls.Add(MakeLabel("中日韩字体", colLabel, y));
            _cjkFontCombo = MakeSelect(valueW, null);
            _cjkFontCombo.Items.Add(""); // 空表示跟随主字体
            _cjkFontCombo.Items.Add("Microsoft YaHei Mono");
            _cjkFontCombo.Items.Add("Sarasa Mono SC");
            _cjkFontCombo.Items.Add("Noto Sans Mono CJK SC");
            _cjkFontCombo.Items.Add("PingFang SC");
            _cjkFontCombo.Items.Add("Source Han Mono SC");
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    foreach (var f in fonts.Families)
                    {
                        var n = f.Name;
                        if ((n.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             n.IndexOf("Noto", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             n.IndexOf("Sarasa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             n.IndexOf("PingFang", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             n.IndexOf("Source Han", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            !_cjkFontCombo.Items.Contains(n))
                        {
                            _cjkFontCombo.Items.Add(n);
                        }
                    }
                }
            }
            catch { }
            _cjkFontCombo.Location = new Point(colValue, y);
            Controls.Add(_cjkFontCombo);
            y += rowH;

            // —— UI 外壳主题（与终端 ColorScheme 独立）——
            Controls.Add(MakeLabel("界面主题", colLabel, y));
            _uiThemeCombo = MakeSelect(valueW, null);
            foreach (var name in new[] { "Dark", "Darker", "OLED" })
                _uiThemeCombo.Items.Add(name);
            _uiThemeCombo.Location = new Point(colValue, y);
            Controls.Add(_uiThemeCombo);
            var themeHint = MakeLabel("Dark/Darker/OLED 配暗色终端方案", colValue + valueW + 10, y + 8);
            Controls.Add(themeHint);
            y += rowH;

            // —— 界面字体 + 字号（同行）——
            Controls.Add(MakeLabel("界面字体", colLabel, y));
            _uiFontCombo = MakeSelect(valueW, FillUiFonts);
            _uiFontCombo.Location = new Point(colValue, y);
            Controls.Add(_uiFontCombo);
            _uiSizeNum = MakeNumber(8, 24, 9);
            _uiSizeNum.Location = new Point(colValue + valueW + 10, y);
            _uiSizeNum.Size = new Size(70, 38);
            Controls.Add(_uiSizeNum);
            y += rowH;

            // —— DPI ——
            _dpiAwareCheck = new AntdUI.Checkbox
            {
                Text = "启用 DPI 感知（需重启，减轻菜单模糊）",
                Location = new Point(colLabel, y),
                AutoSize = true,
                Checked = true
            };
            Controls.Add(_dpiAwareCheck);
            y += rowH + 2;

            // —— 预览（ReadOnly Input 展示）——
            _preview = new AntdUI.Input
            {
                Text = "AaBbCc 0123 预览 Preview",
                Location = new Point(colLabel, y),
                Size = new Size(520 - pad * 2, 64),
                ReadOnly = true,
                Multiline = true,
                BorderWidth = 1F
            };
            Controls.Add(_preview);
            y += 78;

            var resetHint = new AntdUI.Label
            {
                Text = "字体或界面错乱时，点「恢复默认」一键还原全部外观设置",
                AutoSize = true,
                Location = new Point(colLabel, y)
            };
            Controls.Add(resetHint);
            y += 36;

            // ── 底部按钮条：主(保存) + 恢复默认 + 取消 ──
            _btnOk = new AntdUI.Button { Text = "保存", Type = AntdUI.TTypeMini.Primary, Size = new Size(88, 38) };
            _btnOk.Click += (s, e) => SaveResult();

            _btnReset = new AntdUI.Button { Text = "恢复默认", Size = new Size(96, 38) };
            _btnReset.Click += (s, e) => ResetToDefaults();

            _btnCancel = new AntdUI.Button { Text = "取消", Size = new Size(88, 38) };
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            int btnTotal = 88 + 8 + 96 + 8 + 88;
            int bx = 520 - 20 - btnTotal;
            _btnOk.Location = new Point(bx, y);
            _btnReset.Location = new Point(bx + 96, y);
            _btnCancel.Location = new Point(bx + 96 + 104, y);
            Controls.Add(_btnOk);
            Controls.Add(_btnReset);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private static AntdUI.Label MakeLabel(string text, int x, int y)
        {
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + 10) };
        }

        private AntdUI.Select MakeSelect(int width, Action<AntdUI.Select> fill)
        {
            var cb = new AntdUI.Select { Size = new Size(width, 38) };
            if (fill != null) fill(cb);
            return cb;
        }

        private AntdUI.InputNumber MakeNumber(int min, int max, int value)
        {
            return new AntdUI.InputNumber
            {
                Size = new Size(86, 38),
                Minimum = min,
                Maximum = max,
                Value = value,
                Increment = 1
            };
        }

        private static void FillTerminalFonts(AntdUI.Select cb)
        {
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    foreach (var f in fonts.Families)
                    {
                        if (IsLikelyMono(f.Name)) cb.Items.Add(f.Name);
                    }
                    foreach (var f in fonts.Families)
                    {
                        if (!IsLikelyMono(f.Name) && cb.Items.Count < 80) cb.Items.Add(f.Name);
                    }
                }
            }
            catch
            {
                cb.Items.Add("Consolas");
                cb.Items.Add("Cascadia Mono");
                cb.Items.Add("Courier New");
            }
            if (cb.Items.Count == 0) cb.Items.Add("Consolas");
        }

        private static void FillUiFonts(AntdUI.Select cb)
        {
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    // 中文界面优先 Sans；Win7 无 "Microsoft YaHei UI"，探测链交给 FormFontPolicy
                    var prefer = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC" };
                    foreach (var p in prefer)
                        if (Array.Exists(fonts.Families, ff => string.Equals(ff.Name, p, StringComparison.OrdinalIgnoreCase)))
                            cb.Items.Add(p);
                    foreach (var f in fonts.Families)
                    {
                        if (!IsLikelyMono(f.Name) && cb.Items.Count < 60 && !cb.Items.Contains(f.Name))
                            cb.Items.Add(f.Name);
                    }
                }
            }
            catch { cb.Items.Add(FormFontPolicy.UiFontName); }
            if (cb.Items.Count == 0) cb.Items.Add(FormFontPolicy.UiFontName);
        }

        private void SaveResult()
        {
            Result = new AppearanceSettings
            {
                FontName = _fontCombo.SelectedValue != null ? _fontCombo.SelectedValue.ToString() : "Consolas",
                FontSize = (int)_sizeNum.Value,
                CjkFontName = _cjkFontCombo.SelectedValue != null ? (_cjkFontCombo.SelectedValue.ToString() ?? "") : "",
                ColorScheme = _schemeCombo.SelectedValue != null ? _schemeCombo.SelectedValue.ToString() : "Classic",
                UiTheme = _uiThemeCombo.SelectedValue != null ? _uiThemeCombo.SelectedValue.ToString() : "Dark",
                DpiAware = _dpiAwareCheck.Checked,
                UIFontName = _uiFontCombo.SelectedValue != null ? _uiFontCombo.SelectedValue.ToString() : FormFontPolicy.UiFontName,
                UIFontSize = (int)_uiSizeNum.Value
            };
            try
            {
                Result.Save(_iniPath);
                // 可观测性：用户选了什么（终端字体/字号/CJK/UI 字体）——排查"字号不匹配"时与 FontMetrics 对照
                DiagLog.Info("Appearance.Save",
                    "font=" + Result.FontName + "/" + Result.FontSize + "pt cjk=" + (string.IsNullOrEmpty(Result.CjkFontName) ? "-" : Result.CjkFontName) +
                    " scheme=" + Result.ColorScheme + " uiFont=" + Result.UIFontName + "/" + Result.UIFontSize + "pt dpiAware=" + Result.DpiAware);
            }
            catch (Exception ex) { DiagLog.Swallowed("Appearance.Save", ex); }
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// 恢复出厂外观——用户把字号调得过大/字体换得离谱导致界面错乱时的自救出口。
        /// 只重置外观相关字段，落盘后立即把表单各控件回显为默认值（用户可再微调后保存）。
        /// </summary>
        private void ResetToDefaults()
        {
            var dr = AntdUI.Modal.open(this, "恢复默认外观",
                "将恢复以下默认值：\n" +
                "  终端字体 Consolas 12pt / 配色 Classic\n" +
                "  界面字体 " + FormFontPolicy.UiFontName + " 9pt / 界面主题 Dark\n" +
                "  DPI 感知 开启\n\n确定恢复？",
                TType.Warn);
            if (dr != DialogResult.Yes && dr != DialogResult.OK)
                return;

            var d = new AppearanceSettings(); // 出厂默认
            try
            {
                d.Save(_iniPath);
                DiagLog.Info("Appearance.Reset", "restored factory appearance defaults");
            }
            catch (Exception ex) { DiagLog.Swallowed("Appearance.Reset", ex); }

            // 表单回显默认值（不关窗，用户可继续微调）
            SelectCombo(_fontCombo, d.FontName);
            _sizeNum.Value = Math.Max(_sizeNum.Minimum, Math.Min(_sizeNum.Maximum, d.FontSize));
            SelectCombo(_schemeCombo, d.ColorScheme);
            SelectCombo(_cjkFontCombo, "");
            SelectCombo(_uiThemeCombo, d.UiTheme);
            _dpiAwareCheck.Checked = d.DpiAware;
            SelectCombo(_uiFontCombo, FormFontPolicy.UiFontName);
            _uiSizeNum.Value = Math.Max(_uiSizeNum.Minimum, Math.Min(_uiSizeNum.Maximum, d.UIFontSize));
            UpdatePreview();
        }

        private static bool IsLikelyMono(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant();
            return n.Contains("consolas") || n.Contains("cascadia") || n.Contains("courier")
                || n.Contains("mono") || n.Contains("menlo") || n.Contains("source code")
                || n.Contains("fira") || n.Contains("jetbrains") || n.Contains("hack")
                || n.Contains("sarasa") || n.Contains("nerd");
        }

        private void LoadCurrent()
        {
            var s = AppearanceSettings.Load(_iniPath);
            SelectCombo(_fontCombo, s.FontName);
            _sizeNum.Value = Math.Max(_sizeNum.Minimum, Math.Min(_sizeNum.Maximum, s.FontSize));
            SelectCombo(_schemeCombo, s.ColorScheme);
            SelectCombo(_cjkFontCombo, string.IsNullOrEmpty(s.CjkFontName) ? "" : s.CjkFontName);
            SelectCombo(_uiThemeCombo, string.IsNullOrEmpty(s.UiTheme) ? "Dark" : s.UiTheme);
            _dpiAwareCheck.Checked = s.DpiAware;
            SelectCombo(_uiFontCombo, s.UIFontName ?? FormFontPolicy.UiFontName);
            _uiSizeNum.Value = Math.Max(_uiSizeNum.Minimum, Math.Min(_uiSizeNum.Maximum, s.UIFontSize > 0 ? s.UIFontSize : 9));
            UpdatePreview();
        }

        private static void SelectCombo(AntdUI.Select box, string value)
        {
            if (box == null) return;
            if (string.IsNullOrEmpty(value))
            {
                if (box.Items.Count > 0) box.SelectedIndex = 0;
                return;
            }
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (string.Equals(box.Items[i].ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
            box.Items.Insert(0, value);
            box.SelectedIndex = 0;
        }

        private void UpdatePreview()
        {
            try
            {
                var name = _fontCombo.SelectedValue != null ? _fontCombo.SelectedValue.ToString() : "Consolas";
                var size = (float)_sizeNum.Value;
                _preview.Font = new Font(name, size, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                try { _preview.Font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Point); } catch { }
            }
        }
    }

    /// <summary>外观设置模型 + INI 读写。</summary>
    public sealed class AppearanceSettings
    {
        /// <summary>终端等宽字体（ASCII 内容区）。</summary>
        public string FontName { get; set; } = "Consolas";
        /// <summary>终端等宽字号。</summary>
        public int FontSize { get; set; } = 12;
        /// <summary>终端 CJK 补充字体（Xshell 风格的非 ASCII 字体，可空）。</summary>
        public string CjkFontName { get; set; } = "";
        public string ColorScheme { get; set; } = "Classic";
        /// <summary>UI 外壳主题名（与终端 ColorScheme 独立）。</summary>
        public string UiTheme { get; set; } = "Dark";
        public bool DpiAware { get; set; } = true;
        /// <summary>界面非等宽字体（菜单/树/状态栏/对话框）。</summary>
        public string UIFontName { get; set; } = "Microsoft YaHei UI";
        /// <summary>界面字号。</summary>
        public int UIFontSize { get; set; } = 9;

        public static AppearanceSettings Load(string path)
        {
            var s = new AppearanceSettings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return s;
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                        continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "fontName", StringComparison.OrdinalIgnoreCase))
                        s.FontName = val;
                    else if (string.Equals(key, "fontSize", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(val, out n) && n >= 8 && n <= 36) s.FontSize = n;
                    }
                    else if (string.Equals(key, "cjkFontName", StringComparison.OrdinalIgnoreCase))
                        s.CjkFontName = val;
                    else if (string.Equals(key, "colorScheme", StringComparison.OrdinalIgnoreCase))
                        s.ColorScheme = val;
                    else if (string.Equals(key, "uiTheme", StringComparison.OrdinalIgnoreCase))
                        s.UiTheme = val;
                    else if (string.Equals(key, "dpiAware", StringComparison.OrdinalIgnoreCase))
                        s.DpiAware = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    else if (string.Equals(key, "uiFontName", StringComparison.OrdinalIgnoreCase))
                        s.UIFontName = val;
                    else if (string.Equals(key, "uiFontSize", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(val, out n) && n >= 8 && n <= 24) s.UIFontSize = n;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                "[appearance]\r\n" +
                "fontName=" + (FontName ?? "Consolas") + "\r\n" +
                "fontSize=" + FontSize + "\r\n" +
                "cjkFontName=" + (CjkFontName ?? "") + "\r\n" +
                "colorScheme=" + (ColorScheme ?? "Classic") + "\r\n" +
                "uiTheme=" + (UiTheme ?? "Dark") + "\r\n" +
                "dpiAware=" + (DpiAware ? "1" : "0") + "\r\n" +
                "uiFontName=" + (UIFontName ?? "Microsoft YaHei UI") + "\r\n" +
                "uiFontSize=" + UIFontSize + "\r\n");
        }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "data", "config", "appearance.ini");
            }
        }
    }
}
