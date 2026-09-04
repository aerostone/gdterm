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
    /// 2026-09 重构：TableLayoutPanel 流式布局（字号任意调不重叠）+ 恢复默认按钮。
    /// </summary>
    public sealed class AppearanceSettingsForm : Form
    {
        private readonly string _iniPath;
        private ComboBox _fontCombo;
        private NumericUpDown _sizeNum;
        private ComboBox _cjkFontCombo;
        private ComboBox _schemeCombo;
        private CheckBox _dpiAwareCheck;
        private Label _preview;
        private Button _btnOk;
        private Button _btnCancel;
        private Button _btnReset;
        // 界面字体（菜单/树/状态栏）
        private ComboBox _uiFontCombo;
        private NumericUpDown _uiSizeNum;
        private ComboBox _uiThemeCombo;

        public AppearanceSettings Result { get; private set; }

        public AppearanceSettingsForm(string configDir)
        {
            if (string.IsNullOrEmpty(configDir))
                configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config");
            Directory.CreateDirectory(configDir);
            _iniPath = Path.Combine(configDir, "appearance.ini");

            DialogStyle.ApplyChrome(this, 460, 492);
            BuildUi();
            LoadCurrent();
            Gdterm.UI.Services.FormFontPolicy.Apply(this); // 全局 UI 字体传导（含显式雅黑硬编码子控件）
        }

        private void BuildUi()
        {
            // ── 主体：TableLayoutPanel 流式布局，字号任意调整不重叠 ──
            // 结构：row0 终端字体 | row1 字号 | row2 配色 | row3 CJK | row4 界面主题
            //       row5 界面字体+字号 | row6 DPI | row7 预览（跨两列）
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 9,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(DpiScale.V(this, 12))
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 标签列
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));  // 值列
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 附属列（字号框等）

            int row = 0;

            // —— 终端字体 ——
            grid.Controls.Add(DialogStyle.FieldLabel("终端字体"), 0, row);
            _fontCombo = MakeCombo(FillTerminalFonts);
            grid.Controls.Add(_fontCombo, 1, row);
            grid.SetColumnSpan(_fontCombo, 2);
            row++;

            // —— 字号 ——
            grid.Controls.Add(DialogStyle.FieldLabel("字号 (pt)"), 0, row);
            _sizeNum = MakeNumeric(8, 36, 12);
            grid.Controls.Add(_sizeNum, 1, row);
            row++;

            // —— 配色方案 ——
            grid.Controls.Add(DialogStyle.FieldLabel("配色方案"), 0, row);
            _schemeCombo = MakeCombo(null);
            foreach (var name in new[]
            {
                "Classic", "HighContrast", "SolarizedDark", "Monokai", "Dracula", "GreenTerminal", "Light"
            })
                _schemeCombo.Items.Add(name);
            grid.Controls.Add(_schemeCombo, 1, row);
            grid.SetColumnSpan(_schemeCombo, 2);
            row++;

            // —— 中日韩补充字体（Xshell 风格双字体）——
            grid.Controls.Add(DialogStyle.FieldLabel("中日韩字体"), 0, row);
            _cjkFontCombo = MakeCombo(null);
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
            grid.Controls.Add(_cjkFontCombo, 1, row);
            grid.SetColumnSpan(_cjkFontCombo, 2);
            row++;

            // —— UI 外壳主题（与终端 ColorScheme 独立）——
            grid.Controls.Add(DialogStyle.FieldLabel("界面主题"), 0, row);
            _uiThemeCombo = MakeCombo(null);
            foreach (var name in new[] { "Dark", "Darker", "OLED" })
                _uiThemeCombo.Items.Add(name);
            grid.Controls.Add(_uiThemeCombo, 1, row);
            var themeHint = new Label
            {
                Text = "Dark/Darker/OLED 配暗色终端方案，Light 配 Light",
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Margin = new Padding(DpiScale.V(this, 8), DpiScale.V(this, 8), 3, 0)
            };
            grid.Controls.Add(themeHint, 2, row);
            row++;

            // —— 界面字体 + 字号（同行）——
            grid.Controls.Add(DialogStyle.FieldLabel("界面字体"), 0, row);
            _uiFontCombo = MakeCombo(null);
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    // 中文界面优先 Sans；Win7 无 "Microsoft YaHei UI"，探测链交给 FormFontPolicy
                    var prefer = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC" };
                    foreach (var p in prefer)
                        if (Array.Exists(fonts.Families, ff => string.Equals(ff.Name, p, StringComparison.OrdinalIgnoreCase)))
                            _uiFontCombo.Items.Add(p);
                    foreach (var f in fonts.Families)
                    {
                        if (!IsLikelyMono(f.Name) && _uiFontCombo.Items.Count < 60 && !_uiFontCombo.Items.Contains(f.Name))
                            _uiFontCombo.Items.Add(f.Name);
                    }
                }
            }
            catch { _uiFontCombo.Items.Add(FormFontPolicy.UiFontName); }
            if (_uiFontCombo.Items.Count == 0) _uiFontCombo.Items.Add(FormFontPolicy.UiFontName);
            grid.Controls.Add(_uiFontCombo, 1, row);
            _uiSizeNum = MakeNumeric(8, 24, 9);
            grid.Controls.Add(_uiSizeNum, 2, row);
            row++;

            // —— DPI ——
            _dpiAwareCheck = new CheckBox
            {
                Text = "启用 DPI 感知（需重启，减轻菜单模糊）",
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Checked = true,
                Margin = new Padding(3, DpiScale.V(this, 8), 3, 0)
            };
            grid.Controls.Add(_dpiAwareCheck, 0, row);
            grid.SetColumnSpan(_dpiAwareCheck, 3);
            row++;

            // —— 预览 ——
            _preview = new Label
            {
                Text = "AaBbCc 0123 预览 Preview",
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, DpiScale.V(this, 56)),
                Margin = new Padding(3, DpiScale.V(this, 10), 3, 3),
                BackColor = Color.FromArgb(12, 12, 12),
                ForeColor = Color.FromArgb(0, 255, 128),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle
            };
            grid.Controls.Add(_preview, 0, row);
            grid.SetColumnSpan(_preview, 3);
            row++;

            // —— 重置提示（可跨两列）——
            var resetHint = new Label
            {
                Text = "字体或界面错乱时，可点“恢复默认”一键还原全部外观设置",
                AutoSize = true,
                ForeColor = GdtermColorTable.Muted,
                Margin = new Padding(3, DpiScale.V(this, 4), 3, 0)
            };
            grid.Controls.Add(resetHint, 0, row);
            grid.SetColumnSpan(resetHint, 3);

            Controls.Add(grid);

            _fontCombo.SelectedIndexChanged += (s, e) => UpdatePreview();
            _sizeNum.ValueChanged += (s, e) => UpdatePreview();

            // ── 底部按钮条：主(保存) + 恢复默认 + 取消 ──
            _btnOk = new Button { Text = "保存", DialogResult = DialogResult.OK };
            DialogStyle.MakePrimary(_btnOk);
            _btnOk.Click += (s, e) => SaveResult();

            _btnReset = new Button { Text = "恢复默认" };
            DialogStyle.MakeSecondary(_btnReset);
            _btnReset.Click += (s, e) => ResetToDefaults();

            _btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            DialogStyle.MakeSecondary(_btnCancel);

            Controls.Add(DialogStyle.ButtonStrip(_btnOk, _btnReset, _btnCancel));

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void SaveResult()
        {
            Result = new AppearanceSettings
            {
                FontName = _fontCombo.SelectedItem != null ? _fontCombo.SelectedItem.ToString() : "Consolas",
                FontSize = (int)_sizeNum.Value,
                CjkFontName = _cjkFontCombo.SelectedItem != null ? (_cjkFontCombo.SelectedItem.ToString() ?? "") : "",
                ColorScheme = _schemeCombo.SelectedItem != null ? _schemeCombo.SelectedItem.ToString() : "Classic",
                UiTheme = _uiThemeCombo.SelectedItem != null ? _uiThemeCombo.SelectedItem.ToString() : "Dark",
                DpiAware = _dpiAwareCheck.Checked,
                UIFontName = _uiFontCombo.SelectedItem != null ? _uiFontCombo.SelectedItem.ToString() : FormFontPolicy.UiFontName,
                UIFontSize = (int)_uiSizeNum.Value
            };
            try
            {
                Result.Save(_iniPath);
                // 可观测性：用户选了什么（终端字体/字号/CJK/UI 字体）——排查“字号不匹配”时与 FontMetrics 对照
                DiagLog.Info("Appearance.Save",
                    "font=" + Result.FontName + "/" + Result.FontSize + "pt cjk=" + (string.IsNullOrEmpty(Result.CjkFontName) ? "-" : Result.CjkFontName) +
                    " scheme=" + Result.ColorScheme + " uiFont=" + Result.UIFontName + "/" + Result.UIFontSize + "pt dpiAware=" + Result.DpiAware);
            }
            catch (Exception ex) { DiagLog.Swallowed("Appearance.Save", ex); }
        }

        /// <summary>
        /// 恢复出厂外观——用户把字号调得过大/字体换得离谱导致界面错乱时的自救出口。
        /// 只重置外观相关字段，落盘后立即把表单各控件回显为默认值（用户可再微调后保存）。
        /// </summary>
        private void ResetToDefaults()
        {
            if (MessageBox.Show(this,
                    "将恢复以下默认值：\n" +
                    "  终端字体 Consolas 12pt / 配色 Classic\n" +
                    "  界面字体 " + FormFontPolicy.UiFontName + " 9pt / 界面主题 Dark\n" +
                    "  DPI 感知 开启\n\n确定恢复？",
                    "恢复默认外观", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
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

        private ComboBox MakeCombo(Action<ComboBox> fill)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = DpiScale.V(this, 220),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3, DpiScale.V(this, 6), 3, 0)
            };
            DialogStyle.ApplyInput(cb);
            cb.BackColor = GdtermColorTable.Surface;
            if (fill != null) fill(cb);
            return cb;
        }

        private static void FillTerminalFonts(ComboBox cb)
        {
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    foreach (var f in fonts.Families)
                    {
                        // 优先等宽
                        if (IsLikelyMono(f.Name))
                            cb.Items.Add(f.Name);
                    }
                    foreach (var f in fonts.Families)
                    {
                        if (!IsLikelyMono(f.Name) && cb.Items.Count < 80)
                            cb.Items.Add(f.Name);
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

        private NumericUpDown MakeNumeric(int min, int max, int value)
        {
            var n = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                Width = DpiScale.V(this, 64),
                Margin = new Padding(3, DpiScale.V(this, 6), 3, 0)
            };
            DialogStyle.ApplyInput(n);
            return n;
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

        private static void SelectCombo(ComboBox box, string value)
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
                var name = _fontCombo.SelectedItem != null ? _fontCombo.SelectedItem.ToString() : "Consolas";
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
