using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Gdterm.Terminal.Themes;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using Gdterm.Core;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 外观设置：字体/字号/配色/行距提示。保存到 data/config/appearance.ini。
    /// 2026-09 AntdUI 版：AntdUI.Window + Input/Select/InputNumber/Checkbox。
    /// 布局：手工 y 流式，但行高和客户区高度由当前 UI 字体驱动。
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
        private AntdUI.Button _btnImport;
        private AntdUI.Button _btnExport;
        // 界面字体（菜单/树/状态栏）
        private AntdUI.Select _uiFontCombo;
        private AntdUI.InputNumber _uiSizeNum;
        private AntdUI.Select _uiThemeCombo;
        private AntdUI.InputNumber _opacityNum;
        // 界面语言（重启生效）
        private AntdUI.Select _langCombo;
        private int _fieldHeight = 38;
        private int _rowHeight = 44;

        public AppearanceSettings Result { get; private set; }

        public AppearanceSettingsForm(string configDir)
        {
            if (string.IsNullOrEmpty(configDir))
                configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "config");
            Directory.CreateDirectory(configDir);
            _iniPath = Path.Combine(configDir, "appearance.ini");

            BuildUi();
            LoadCurrent();
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导
        }

        private void BuildUi()
        {
            Text = L10n.T("外观设置");
            Font = FormFontPolicy.UiFont();
            Size = DpiScale.S(this, 520, 560); // 初始基准，末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;

            int pad = DpiScale.V(this, 20);
            int clientW = DpiScale.V(this, 520);
            int colLabel = pad;
            int colValue = pad + DpiScale.V(this, 110);
            int valueW = DpiScale.V(this, 220);
            _fieldHeight = FormFontPolicy.FieldHeight(this);
            _rowHeight = Math.Max(DpiScale.V(this, 44), _fieldHeight + DpiScale.V(this, 6));
            int rowH = _rowHeight;
            int y = 22;

            // —— 终端字体 ——
            Controls.Add(MakeLabel(L10n.T("终端字体"), colLabel, y));
            _fontCombo = MakeSelect(valueW, FillTerminalFonts);
            _fontCombo.Location = new Point(colValue, y);
            Controls.Add(_fontCombo);
            y += rowH;

            // —— 字号 ——
            Controls.Add(MakeLabel(L10n.T("字号 (pt)"), colLabel, y));
            _sizeNum = MakeNumber(8, 36, 12);
            _sizeNum.Location = new Point(colValue, y);
            Controls.Add(_sizeNum);
            y += rowH;

            // —— 配色方案 ——
            Controls.Add(MakeLabel(L10n.T("配色方案"), colLabel, y));
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
            Controls.Add(MakeLabel(L10n.T("中日韩字体"), colLabel, y));
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
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("AppearanceSettingsForm", exSwallowed); } catch { } }
            _cjkFontCombo.Location = new Point(colValue, y);
            Controls.Add(_cjkFontCombo);
            y += rowH;

            // —— UI 外壳主题（与终端 ColorScheme 独立）——
            Controls.Add(MakeLabel(L10n.T("界面主题"), colLabel, y));
            _uiThemeCombo = MakeSelect(valueW, null);
            foreach (var name in new[] { "Dark", "Darker", "OLED" })
                _uiThemeCombo.Items.Add(name);
            _uiThemeCombo.Location = new Point(colValue, y);
            Controls.Add(_uiThemeCombo);
            y += rowH;

            // —— 界面字体 + 字号（同行）——
            Controls.Add(MakeLabel(L10n.T("界面字体"), colLabel, y));
            _uiFontCombo = MakeSelect(valueW, FillUiFonts);
            _uiFontCombo.Location = new Point(colValue, y);
            Controls.Add(_uiFontCombo);
            _uiSizeNum = MakeNumber(8, 24, 9);
            _uiSizeNum.Location = new Point(colValue + valueW + DpiScale.V(this, 10), y);
            _uiSizeNum.Size = new Size(DpiScale.V(this, 70), _fieldHeight);
            Controls.Add(_uiSizeNum);
            y += rowH;

            // —— 窗口不透明度 30-100（路线图 terminal-enhancements；WinForms 整窗 Opacity，即时生效）——
            Controls.Add(MakeLabel(L10n.T("不透明度 %"), colLabel, y));
            _opacityNum = MakeNumber(30, 100, 100);
            _opacityNum.Location = new Point(colValue, y);
            Controls.Add(_opacityNum);
            y += rowH;

            // —— DPI ——
            _dpiAwareCheck = new AntdUI.Checkbox {
                Text = L10n.T("启用 DPI 感知（需重启，减轻菜单模糊）"),
                Location = new Point(colLabel, y),
                AutoSize = true,
                Checked = true,
                ForeColor = GdtermColorTable.Foreground
            };
            Controls.Add(_dpiAwareCheck);
            y += rowH;

            // —— 界面语言（重启生效；选项名用各自母语自标识，业界惯例）——
            Controls.Add(MakeLabel(L10n.T("界面语言"), colLabel, y));
            _langCombo = MakeSelect(valueW, null);
            _langCombo.Items.Add(L10n.T("中文"));
            _langCombo.Items.Add("English");
            _langCombo.Location = new Point(colValue, y);
            Controls.Add(_langCombo);
            var langHint = new AntdUI.Label {
                Text = L10n.T("重启后生效"),
                AutoSize = true,
                Location = new Point(colValue + valueW + DpiScale.V(this, 10), y + DpiScale.V(this, 10)),
                ForeColor = GdtermColorTable.Muted
            };
            Controls.Add(langHint);
            y += rowH;

            // —— 预览（ReadOnly Input 展示）——
            _preview = new AntdUI.Input {
                Text = L10n.T("AaBbCc 0123 预览 Preview"),
                Location = new Point(colLabel, y),
                Size = new Size(clientW - pad * 2, Math.Max(DpiScale.V(this, 64), _fieldHeight + DpiScale.V(this, 20))),
                ReadOnly = true,
                Multiline = true,
                BorderWidth = 1F,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
            };
            Controls.Add(_preview);
            y += _preview.Height + DpiScale.V(this, 14);

            var resetHint = new AntdUI.Label {
                Text = L10n.T("字体异常可恢复默认"),
                AutoSize = true,
                Location = new Point(colLabel, y),
                ForeColor = GdtermColorTable.Muted
            };
            Controls.Add(resetHint);
            y += rowH;

            // ── 底部按钮条：导出/导入 + 主(保存) + 恢复默认 + 取消 ──
            // Type 自绘：手写 BackColor/ForeColor 会被 AntdUI 绘制管线忽略，只留 Type
            _btnOk = new AntdUI.Button { Text = L10n.T("保存"), Type = AntdUI.TTypeMini.Primary, Size = new Size(DpiScale.V(this, 88), _fieldHeight) };
            _btnOk.Click += (s, e) => SaveResult();

            _btnReset = new AntdUI.Button { Text = L10n.T("恢复默认"), Type = AntdUI.TTypeMini.Default, Size = new Size(DpiScale.V(this, 96), _fieldHeight) };
            _btnReset.Click += (s, e) => ResetToDefaults();

            _btnCancel = new AntdUI.Button { Text = L10n.T("取消"), Type = AntdUI.TTypeMini.Default, Size = new Size(DpiScale.V(this, 88), _fieldHeight) };
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            // F10 导入/导出：设置迁移到另一台机器/备份（外观.ini 是纯文本，无凭据）
            _btnExport = new AntdUI.Button { Text = L10n.T("导出…"), Type = AntdUI.TTypeMini.Default, Size = new Size(DpiScale.V(this, 88), _fieldHeight) };
            _btnExport.Click += (s, e) => ExportSettings();

            _btnImport = new AntdUI.Button { Text = L10n.T("导入…"), Type = AntdUI.TTypeMini.Default, Size = new Size(DpiScale.V(this, 88), _fieldHeight) };
            _btnImport.Click += (s, e) => ImportSettings();

            int w88 = DpiScale.V(this, 88), w96 = DpiScale.V(this, 96), gap = DpiScale.V(this, 8);
            int btnTotal = w88 * 4 + w96 + gap * 4;
            int bx = clientW - pad - btnTotal;
            _btnExport.Location = new Point(bx, y);
            _btnImport.Location = new Point(bx + w88 + gap, y);
            _btnOk.Location = new Point(bx + (w88 + gap) * 2, y);
            _btnReset.Location = new Point(bx + (w88 + gap) * 2 + w88 + gap, y);
            _btnCancel.Location = new Point(bx + btnTotal - w88, y);
            Controls.Add(_btnExport);
            Controls.Add(_btnImport);
            Controls.Add(_btnOk);
            Controls.Add(_btnReset);
            Controls.Add(_btnCancel);

            ClientSize = new Size(Math.Max(ClientSize.Width, clientW), Math.Max(ClientSize.Height, y + _fieldHeight + pad));

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private AntdUI.Label MakeLabel(string text, int x, int y)
        {
            var label = new AntdUI.Label { Text = text, AutoSize = true, Font = Font, ForeColor = GdtermColorTable.Muted };
            int offset = Math.Max(4, (_rowHeight - label.Font.Height) / 2);
            label.Location = new Point(x, y + offset);
            return label;
        }

        private AntdUI.Select MakeSelect(int width, Action<AntdUI.Select> fill)
        {
            var cb = new AntdUI.Select { Size = new Size(width, _fieldHeight), BackColor = GdtermColorTable.Surface, ForeColor = GdtermColorTable.Foreground };
            if (fill != null) fill(cb);
            return cb;
        }

        private AntdUI.InputNumber MakeNumber(int min, int max, int value)
        {
            return new AntdUI.InputNumber {
                Size = new Size(DpiScale.V(this, 86), _fieldHeight),
                Minimum = min,
                Maximum = max,
                Value = value,
                Increment = 1,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground
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

        /// <summary>
        /// 保存前校验：字号区间、透明度区间、字体是否真的装在系统里。
        /// 「选了没装的字体」是外观页最典型的静默失败——GDI 会 fallback 到别的字体，
        /// 用户只看到"设置没生效"，没有任何提示。
        /// </summary>
        private bool ValidateSettings()
        {
            int size = (int)_sizeNum.Value;
            if (size < 6 || size > 48)
            {
                AntdUI.Message.warn(this, L10n.Tf("终端字号需在 6–48 之间（当前 {0}）", size));
                _sizeNum.Focus();
                return false;
            }
            int uiSize = (int)_uiSizeNum.Value;
            if (uiSize < 6 || uiSize > 36)
            {
                AntdUI.Message.warn(this, L10n.Tf("界面字号需在 6–36 之间（当前 {0}）", uiSize));
                _uiSizeNum.Focus();
                return false;
            }
            if (_opacityNum != null && ((int)_opacityNum.Value < 30 || (int)_opacityNum.Value > 100))
            {
                AntdUI.Message.warn(this, L10n.T("窗口不透明度需在 30–100 之间"));
                return false;
            }
            var missing = new List<string>();
            if (!FontExists(_fontCombo)) missing.Add(L10n.T("终端字体"));
            if (!FontExists(_uiFontCombo)) missing.Add(L10n.T("界面字体"));
            if (missing.Count > 0)
            {
                // 不拦保存（用户可能故意先配好再去装字体），但必须说清楚会 fallback
                AntdUI.Message.warn(this, string.Join(L10n.IsEnglish ? ", " : "、", missing) + L10n.T(" 在本机未安装，将回退到系统默认字体"));
                DiagLog.Info("Appearance.Validate", "missing fonts: " + string.Join(",", missing));
            }
            return true;
        }

        private static bool FontExists(AntdUI.Select combo)
        {
            try
            {
                var name = combo != null && combo.SelectedValue != null ? combo.SelectedValue.ToString() : null;
                if (string.IsNullOrWhiteSpace(name)) return true;
                using (var ff = new FontFamily(name)) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        private void SaveResult()
        {
            if (!ValidateSettings()) return;
            // 底栏分组/钉住不在本对话框编辑：必须透传旧值，否则 Save 会清掉 MainForm 已落盘的 bottom-bar 状态。
            var old = AppearanceSettings.Load(_iniPath);
            Result = new AppearanceSettings
            {
                FontName = _fontCombo.SelectedValue != null ? _fontCombo.SelectedValue.ToString() : "Consolas",
                FontSize = (int)_sizeNum.Value,
                CjkFontName = _cjkFontCombo.SelectedValue != null ? (_cjkFontCombo.SelectedValue.ToString() ?? "") : "",
                ColorScheme = _schemeCombo.SelectedValue != null ? _schemeCombo.SelectedValue.ToString() : "Classic",
                UiTheme = _uiThemeCombo.SelectedValue != null ? _uiThemeCombo.SelectedValue.ToString() : "Dark",
                DpiAware = _dpiAwareCheck.Checked,
                UIFontName = _uiFontCombo.SelectedValue != null ? _uiFontCombo.SelectedValue.ToString() : FormFontPolicy.UiFontName,
                UIFontSize = (int)_uiSizeNum.Value,
                WindowOpacity = _opacityNum != null ? (int)_opacityNum.Value : 100,
                Language = _langCombo != null && _langCombo.SelectedIndex == 1 ? L10n.En : L10n.Zh,
                QuickBarGroup = old != null ? old.QuickBarGroup : null,
                PinTmux = old != null && old.PinTmux,
                // 视图状态不在本对话框编辑：同样必须透传（否则保存一次外观就把窗口几何/侧板宽度清零）
                WindowX = old != null ? old.WindowX : int.MinValue,
                WindowY = old != null ? old.WindowY : int.MinValue,
                WindowWidth = old != null ? old.WindowWidth : 0,
                WindowHeight = old != null ? old.WindowHeight : 0,
                WindowMaximized = old != null && old.WindowMaximized,
                SidePanelWidth = old != null ? old.SidePanelWidth : 0
            };
            try
            {
                Result.Save(_iniPath);
                // 可观测性：用户选了什么（终端字体/字号/CJK/UI 字体）——排查"字号不匹配"时与 FontMetrics 对照
                DiagLog.Info("Appearance.Save",
                    "font=" + Result.FontName + "/" + Result.FontSize + "pt cjk=" + (string.IsNullOrEmpty(Result.CjkFontName) ? "-" : Result.CjkFontName) +
                    " scheme=" + Result.ColorScheme + " uiFont=" + Result.UIFontName + "/" + Result.UIFontSize + "pt dpiAware=" + Result.DpiAware);
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("Appearance.Save", ex);
                // A7：保存失败不落 OK、不关窗——否则调用方会误报"外观已保存"
                AntdUI.Message.error(this, L10n.Tf("外观保存失败: {0}", ex.Message));
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// 恢复出厂外观——用户把字号调得过大/字体换得离谱导致界面错乱时的自救出口。
        /// 只重置外观相关字段，落盘后立即把表单各控件回显为默认值（用户可再微调后保存）。
        /// </summary>
        private void ResetToDefaults()
        {
            var dr = AntdUI.Modal.open(this, L10n.T("恢复默认外观"),
                L10n.Tf("将恢复以下默认值：\n  终端字体 Consolas 12pt / 配色 Classic\n  界面字体 {0} 9pt / 界面主题 Dark\n  不透明度 100% / DPI 感知 开启\n\n确定恢复？", FormFontPolicy.UiFontName),
                AntdUI.TType.Warn);
            if (dr != DialogResult.Yes && dr != DialogResult.OK)
                return;

            var d = new AppearanceSettings(); // 出厂默认
            try
            {
                // 恢复默认只重置外观字段：底栏分组/钉住是 MainForm 运行时状态，不随外观重置清空。
                var cur = AppearanceSettings.Load(_iniPath);
                if (cur != null) { d.QuickBarGroup = cur.QuickBarGroup; d.PinTmux = cur.PinTmux; }
                d.Save(_iniPath);
                DiagLog.Info("Appearance.Reset", "restored factory appearance defaults");
            }
            catch (Exception ex)
            {
                DiagLog.Swallowed("Appearance.Reset", ex);
                // A7：恢复失败不回显——d 未落盘，继续回显=展示未保存的假默认
                AntdUI.Message.error(this, L10n.Tf("恢复默认外观失败: {0}", ex.Message));
                return;
            }

            // 表单回显默认值（不关窗，用户可继续微调）
            SelectCombo(_fontCombo, d.FontName);
            _sizeNum.Value = ClampNum(_sizeNum, d.FontSize);
            SelectCombo(_schemeCombo, d.ColorScheme);
            SelectCombo(_cjkFontCombo, "");
            SelectCombo(_uiThemeCombo, d.UiTheme);
            _dpiAwareCheck.Checked = d.DpiAware;
            SelectCombo(_uiFontCombo, FormFontPolicy.UiFontName);
            _uiSizeNum.Value = ClampNum(_uiSizeNum, d.UIFontSize);
            if (_opacityNum != null) _opacityNum.Value = ClampNum(_opacityNum, 100);
            UpdatePreview();
        }

        private static decimal ClampNum(AntdUI.InputNumber num, int v)
        {
            decimal value = v;
            if (num.Minimum.HasValue && value < num.Minimum.Value) value = num.Minimum.Value;
            if (num.Maximum.HasValue && value > num.Maximum.Value) value = num.Maximum.Value;
            return value;
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
            ApplySettings(AppearanceSettings.Load(_iniPath));
        }

        /// <summary>把设置模型回显到各控件（LoadCurrent/导入共用；缺字段时 Load 已回填默认值）。</summary>
        private void ApplySettings(AppearanceSettings s)
        {
            SelectCombo(_fontCombo, s.FontName);
            _sizeNum.Value = ClampNum(_sizeNum, s.FontSize);
            SelectCombo(_schemeCombo, s.ColorScheme);
            SelectCombo(_cjkFontCombo, string.IsNullOrEmpty(s.CjkFontName) ? "" : s.CjkFontName);
            SelectCombo(_uiThemeCombo, string.IsNullOrEmpty(s.UiTheme) ? "Dark" : s.UiTheme);
            _dpiAwareCheck.Checked = s.DpiAware;
            SelectCombo(_uiFontCombo, s.UIFontName ?? FormFontPolicy.UiFontName);
            _uiSizeNum.Value = ClampNum(_uiSizeNum, s.UIFontSize > 0 ? s.UIFontSize : 9);
            if (_opacityNum != null) _opacityNum.Value = ClampNum(_opacityNum, s.WindowOpacity >= 30 && s.WindowOpacity <= 100 ? s.WindowOpacity : 100);
            // 界面语言回显（索引 0=中文 1=English；与选项添加顺序对应）
            if (_langCombo != null) _langCombo.SelectedIndex = L10n.IsEnglish ? 1 : 0;
            UpdatePreview();
        }

        /// <summary>导出当前已落盘的外观设置（不含表单未保存的编辑）——跨机迁移/备份。</summary>
        private void ExportSettings()
        {
            using (var dlg = new SaveFileDialog
            {
                Title = L10n.T("导出外观设置"),
                Filter = L10n.T("外观设置 (*.ini)|*.ini"),
                FileName = "appearance.ini"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.Copy(_iniPath, dlg.FileName, true);
                    DiagLog.Info("Appearance.Export", dlg.FileName);
                    ToastNotifier.Success(L10n.T("外观设置已导出"));
                }
                catch (Exception ex)
                {
                    AntdUI.Message.error(this, L10n.Tf("导出失败: {0}", ex.Message));
                }
            }
        }

        /// <summary>导入外观设置文件到表单（点「保存」后生效）——Load 对损坏/缺字段回填默认值，不会导出一半状态。</summary>
        private void ImportSettings()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = L10n.T("导入外观设置"),
                Filter = L10n.T("外观设置 (*.ini)|*.ini")
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ApplySettings(AppearanceSettings.Load(dlg.FileName));
                DiagLog.Info("Appearance.Import", dlg.FileName);
                ToastNotifier.Success(L10n.T("已导入外观设置（保存后生效）"));
            }
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
                try { _preview.Font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Point); } catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("AppearanceSettingsForm", exSwallowed); } catch { } }
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
        /// <summary>底栏快捷命令分组回放（null/空=全部；"__tmux__"=tmux 键组）。</summary>
        public string QuickBarGroup { get; set; }
        /// <summary>tmux 键组钉住常驻。</summary>
        public bool PinTmux { get; set; }
        /// <summary>界面语言（"zh" 默认 / "en"）；启动时由 Program 加载到 L10n。</summary>
        public string Language { get; set; } = "zh";
        /// <summary>窗口不透明度（整窗 Opacity；终端画布为不透明子控件，WinForms 下无逐区透明）。</summary>
        public int WindowOpacity { get; set; } = 100;

        // ── 视图状态（窗口几何 / 侧板宽度）──────────────────────────────────
        // 和字体配色放同一个 ini：它们都是"这台机器上的显示偏好"，分两个文件反而要维护两条
        // 读写路径。注意 Save() 是**整文件重写**——任何一处只带自己那几个字段去 Save()，
        // 就会静默清掉别人的键（SaveResult 里已踩过 pinTmux 的坑，新键一律 pass-through）。
        /// <summary>窗口左上角 X（WindowWidth/Height 有效才用；未保存过为 int.MinValue）。</summary>
        public int WindowX { get; set; } = int.MinValue;
        public int WindowY { get; set; } = int.MinValue;
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        /// <summary>侧板宽度（工具箱/监控/多通道等右挂面板），0=未设置用默认 360。</summary>
        public int SidePanelWidth { get; set; }

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
                    else if (string.Equals(key, "quickBarGroup", StringComparison.OrdinalIgnoreCase))
                        s.QuickBarGroup = val;
                    else if (string.Equals(key, "pinTmux", StringComparison.OrdinalIgnoreCase))
                        s.PinTmux = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    else if (string.Equals(key, "language", StringComparison.OrdinalIgnoreCase))
                        s.Language = val;
                    else if (string.Equals(key, "windowOpacity", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        // 路线图口径 30%-100%，非法值回 100（不透明）。
                        if (int.TryParse(val, out n) && n >= 30 && n <= 100) s.WindowOpacity = n;
                    }
                    else if (string.Equals(key, "windowX", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(val, out n)) s.WindowX = n;
                    }
                    else if (string.Equals(key, "windowY", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(val, out n)) s.WindowY = n;
                    }
                    else if (string.Equals(key, "windowWidth", StringComparison.OrdinalIgnoreCase))
                    {
                        // 下限挡掉"上次最小化时存了 160×28"这类把窗口缩成一条的值
                        int n; if (int.TryParse(val, out n) && n >= 640 && n <= 20000) s.WindowWidth = n;
                    }
                    else if (string.Equals(key, "windowHeight", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(val, out n) && n >= 480 && n <= 20000) s.WindowHeight = n;
                    }
                    else if (string.Equals(key, "windowMax", StringComparison.OrdinalIgnoreCase))
                        s.WindowMaximized = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    else if (string.Equals(key, "sidePanelWidth", StringComparison.OrdinalIgnoreCase))
                    {
                        int n; if (int.TryParse(val, out n) && n >= 320 && n <= 1600) s.SidePanelWidth = n;
                    }
                }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("AppearanceSettingsForm", exSwallowed); } catch { } }
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
                "uiFontSize=" + UIFontSize + "\r\n" +
                "quickBarGroup=" + (QuickBarGroup ?? "") + "\r\n" +
                "pinTmux=" + (PinTmux ? "1" : "0") + "\r\n" +
                "language=" + (Language ?? "zh") + "\r\n" +
                "windowOpacity=" + WindowOpacity + "\r\n" +
                "windowX=" + WindowX + "\r\n" +
                "windowY=" + WindowY + "\r\n" +
                "windowWidth=" + WindowWidth + "\r\n" +
                "windowHeight=" + WindowHeight + "\r\n" +
                "windowMax=" + (WindowMaximized ? "1" : "0") + "\r\n" +
                "sidePanelWidth=" + SidePanelWidth + "\r\n");
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
