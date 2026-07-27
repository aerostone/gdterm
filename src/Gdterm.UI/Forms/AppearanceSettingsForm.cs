using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Gdterm.Terminal.Themes;
using Gdterm.UI.Diagnostics;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 外观设置：字体/字号/配色/行距提示。保存到 data/config/appearance.ini。
    /// </summary>
    public sealed class AppearanceSettingsForm : Form
    {
        private readonly string _iniPath;
        private ComboBox _fontCombo;
        private NumericUpDown _sizeNum;
        private ComboBox _schemeCombo;
        private CheckBox _dpiAwareCheck;
        private Label _preview;
        private Button _btnOk;
        private Button _btnCancel;
        // 界面字体（菜单/树/状态栏）
        private ComboBox _uiFontCombo;
        private NumericUpDown _uiSizeNum;

        public AppearanceSettings Result { get; private set; }

        public AppearanceSettingsForm(string configDir)
        {
            if (string.IsNullOrEmpty(configDir))
                configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config");
            Directory.CreateDirectory(configDir);
            _iniPath = Path.Combine(configDir, "appearance.ini");

            Text = "外观设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            // 高/低 DPI 自适应
            ClientSize = new Size(440, 420);
            BackColor = Color.FromArgb(32, 32, 34);
            ForeColor = Color.FromArgb(220, 220, 220);
            Font = new Font("Microsoft YaHei UI", 9f);

            BuildUi();
            LoadCurrent();
        }

        private void BuildUi()
        {
            int y = 16;
            Controls.Add(MakeLabel("终端字体", 16, y));
            _fontCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, y - 2),
                Width = 280,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    foreach (var f in fonts.Families)
                    {
                        // 优先等宽
                        if (IsLikelyMono(f.Name))
                            _fontCombo.Items.Add(f.Name);
                    }
                    foreach (var f in fonts.Families)
                    {
                        if (!IsLikelyMono(f.Name) && _fontCombo.Items.Count < 80)
                            _fontCombo.Items.Add(f.Name);
                    }
                }
            }
            catch
            {
                _fontCombo.Items.Add("Consolas");
                _fontCombo.Items.Add("Cascadia Mono");
                _fontCombo.Items.Add("Courier New");
            }
            if (_fontCombo.Items.Count == 0) _fontCombo.Items.Add("Consolas");
            Controls.Add(_fontCombo);
            y += 36;

            Controls.Add(MakeLabel("字号 (px)", 16, y));
            _sizeNum = new NumericUpDown
            {
                Location = new Point(120, y - 2),
                Width = 80,
                Minimum = 8,
                Maximum = 36,
                Value = 12,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };
            Controls.Add(_sizeNum);
            y += 36;

            Controls.Add(MakeLabel("配色方案", 16, y));
            _schemeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, y - 2),
                Width = 200,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            foreach (var name in new[]
            {
                "Classic", "HighContrast", "SolarizedDark", "Monokai", "Dracula", "GreenTerminal", "Light"
            })
                _schemeCombo.Items.Add(name);
            Controls.Add(_schemeCombo);
            y += 36;

            _dpiAwareCheck = new CheckBox
            {
                Text = "启用 DPI 感知（需重启，减轻菜单模糊）",
                Location = new Point(16, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200),
                Checked = true
            };
            Controls.Add(_dpiAwareCheck);
            y += 36;

            // —— 界面字体（非等宽，菜单/树/状态栏/对话框）——
            Controls.Add(MakeLabel("界面字体", 16, y));
            _uiFontCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, y - 2),
                Width = 220,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    // 中文界面优先 Sans
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
            catch { _uiFontCombo.Items.Add("Microsoft YaHei UI"); }
            if (_uiFontCombo.Items.Count == 0) _uiFontCombo.Items.Add("Microsoft YaHei UI");
            Controls.Add(_uiFontCombo);

            _uiSizeNum = new NumericUpDown
            {
                Location = new Point(350, y - 2),
                Width = 56,
                Minimum = 8,
                Maximum = 24,
                Value = 9,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };
            Controls.Add(_uiSizeNum);
            y += 36;

            _preview = new Label
            {
                Text = "AaBbCc 0123 预览 Preview",
                Location = new Point(16, y),
                Size = new Size(400, 60),
                BackColor = Color.FromArgb(12, 12, 12),
                ForeColor = Color.FromArgb(0, 255, 128),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_preview);
            y += 72;

            _fontCombo.SelectedIndexChanged += (s, e) => UpdatePreview();
            _sizeNum.ValueChanged += (s, e) => UpdatePreview();

            _btnOk = new Button
            {
                Text = "保存",
                DialogResult = DialogResult.OK,
                Location = new Point(240, y),
                Size = new Size(88, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            _btnOk.FlatAppearance.BorderSize = 0;
            _btnOk.Click += (s, e) =>
            {
                Result = new AppearanceSettings
                {
                    FontName = _fontCombo.SelectedItem != null ? _fontCombo.SelectedItem.ToString() : "Consolas",
                    FontSize = (int)_sizeNum.Value,
                    ColorScheme = _schemeCombo.SelectedItem != null ? _schemeCombo.SelectedItem.ToString() : "Classic",
                    DpiAware = _dpiAwareCheck.Checked,
                    UIFontName = _uiFontCombo.SelectedItem != null ? _uiFontCombo.SelectedItem.ToString() : "Microsoft YaHei UI",
                    UIFontSize = (int)_uiSizeNum.Value
                };
                try { Result.Save(_iniPath); } catch (Exception ex) { DiagLog.Swallowed("Appearance.Save", ex); }
            };
            Controls.Add(_btnOk);

            _btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(336, y),
                Size = new Size(88, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 64),
                ForeColor = Color.White
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200)
            };
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
            _dpiAwareCheck.Checked = s.DpiAware;
            SelectCombo(_uiFontCombo, s.UIFontName ?? "Microsoft YaHei UI");
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
                _preview.Font = new Font(name, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                try { _preview.Font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Pixel); } catch { }
            }
        }
    }

    /// <summary>外观设置模型 + INI 读写。</summary>
    public sealed class AppearanceSettings
    {
        /// <summary>终端等宽字体（内容区）。</summary>
        public string FontName { get; set; } = "Consolas";
        /// <summary>终端等宽字号。</summary>
        public int FontSize { get; set; } = 12;
        public string ColorScheme { get; set; } = "Classic";
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
                    else if (string.Equals(key, "colorScheme", StringComparison.OrdinalIgnoreCase))
                        s.ColorScheme = val;
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
                "colorScheme=" + (ColorScheme ?? "Classic") + "\r\n" +
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
