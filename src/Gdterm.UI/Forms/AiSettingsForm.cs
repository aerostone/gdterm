using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;
using Gdterm.Core;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// AI 设置对话框（AntdUI 版）——配置 API 端点、密钥、模型参数。
    /// 从 AiModelStore 加载默认模型配置，保存后回写。
    /// </summary>
    public class AiSettingsForm : AntdUI.Window
    {
        private readonly AiModelStore _modelStore;
        private AiModelConfig _currentConfig;

        private AntdUI.Input _nameBox;
        private AntdUI.Input _endpointBox;
        private AntdUI.Input _apiKeyBox;
        private AntdUI.Input _modelBox;
        private AntdUI.InputNumber _maxTokensSpinner;
        private AntdUI.InputNumber _temperatureSpinner;
        private AntdUI.Label _statusLabel;
        private AntdUI.Checkbox _showKeyCheck;

        public AiSettingsForm(AiModelStore modelStore)
        {
            _modelStore = modelStore;
            InitializeComponent();
            LoadCurrentConfig();
            Services.FormFontPolicy.Apply(this); // AntdUI 控件继承 Form.Font，恢复用户配置 UI 字号传导
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
        }

        private void InitializeComponent()
        {
            Text = L10n.T("AI 设置");
            Size = DpiScale.S(this, 520, 500); // 初始基准，构造末尾按内容自适应重设
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Foreground;
            Font = FormFontPolicy.UiFont(); // 布局前先设全局字体，RowStep 才能按真实字号算行距

            // 字体驱动 + DPI 缩放布局（修复：固定像素步进在高 DPI/大字号下控件重叠）
            int pad = DpiScale.V(this, 20);
            int labelX = pad;
            // 标签列按实测文字宽撑开：写死 110 设计 px 时 14pt「最大 Token」实测 126px，
            // 直接压到右边输入框上（矩阵门 Dark-d100-f14 实证 5 处重叠）
            int boxX = pad + FormFontPolicy.LabelColumn(this, 110,
                L10n.T("配置名称"), L10n.T("端点 URL"), "API Key", L10n.T("模型名称"),
                L10n.T("最大 Token"), L10n.T("温度"));
            int boxW = DpiScale.V(this, 350);
            int fieldH = FormFontPolicy.FieldHeight(this);
            int rowH = fieldH + DpiScale.V(this, 10);
            int y = DpiScale.V(this, 18);

            var titleLabel = FormFontPolicy.FitSingleLineLabel(new AntdUI.Label {
                Text = L10n.T("AI 模型配置"),
                Font = Gdterm.UI.Services.FormFontPolicy.UiFont(+5f, FontStyle.Bold),
                Location = new Point(pad, y)
            });
            Controls.Add(titleLabel);
            // 标题字号是 +5pt（19pt），行高必须用它的真实高，不能用表单行距：
            // 14pt 时标题实测 42px 而 RowStep 只有 36 → 标题下缘压住副标题
            y += titleLabel.Height + DpiScale.V(this, 6);

            var subtitleLabel = FormFontPolicy.FitSingleLineLabel(new AntdUI.Label {
                Text = L10n.T("配置 OpenAI 兼容 API 端点和模型参数"),
                Location = new Point(pad, y)
            });
            Controls.Add(subtitleLabel);
            y += subtitleLabel.Height + DpiScale.V(this, 10);

            _nameBox = AddField(L10n.T("配置名称"), labelX, boxX, y, boxW, fieldH);
            y += rowH;
            _endpointBox = AddField(L10n.T("端点 URL"), labelX, boxX, y, boxW, fieldH);
            _endpointBox.PlaceholderText = "https://api.openai.com/v1";
            y += rowH;

            // API Key（带掩码 + 显示切换）
            Controls.Add(MakeLabel("API Key", labelX, y, fieldH));
            _apiKeyBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW - DpiScale.V(this, 76), fieldH),
                Font = Services.FormFontPolicy.MonoFont(),
                UseSystemPasswordChar = true
            };
            _showKeyCheck = new AntdUI.Checkbox {
                Text = L10n.T("显示"),
                AutoSize = true,
                Location = new Point(boxX + boxW - DpiScale.V(this, 66), y + DpiScale.V(this, 10))
            };
            _showKeyCheck.CheckedChanged += (s, e) =>
            {
                _apiKeyBox.UseSystemPasswordChar = !_showKeyCheck.Checked;
            };
            Controls.Add(_apiKeyBox);
            Controls.Add(_showKeyCheck);
            // 「显示」勾选框在大字号下比输入框还高（14pt 实测 48 > 字段 38）：
            // 居中会把它压到下一行，行高必须让位给更高的那一个
            int chkH = _showKeyCheck.PreferredSize.Height;
            if (chkH > fieldH)
            {
                _showKeyCheck.Top = y;
                y += chkH + DpiScale.V(this, 10);
            }
            else
            {
                y += rowH;
            }

            _modelBox = AddField(L10n.T("模型名称"), labelX, boxX, y, boxW, fieldH);
            _modelBox.PlaceholderText = "gpt-4";
            y += rowH;

            // Max Tokens
            Controls.Add(MakeLabel(L10n.T("最大 Token"), labelX, y, fieldH));
            _maxTokensSpinner = new AntdUI.InputNumber {
                Location = new Point(boxX, y),
                Size = new Size(DpiScale.V(this, 140), fieldH),
                Minimum = 1,
                Maximum = 128000,
                Value = 2048,
                Increment = 256
            };
            Controls.Add(_maxTokensSpinner);
            y += rowH;

            // Temperature
            Controls.Add(MakeLabel(L10n.T("温度"), labelX, y, fieldH));
            _temperatureSpinner = new AntdUI.InputNumber {
                Location = new Point(boxX, y),
                Size = new Size(DpiScale.V(this, 140), fieldH),
                Minimum = 0m,
                Maximum = 2m,
                Value = 0.7m,
                Increment = 0.1m,
                DecimalPlaces = 1
            };
            Controls.Add(_temperatureSpinner);
            y += rowH;

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Text = "",
                Location = new Point(pad, y)
            };
            FormFontPolicy.FitSingleLineLabel(_statusLabel);
            Controls.Add(_statusLabel);
            y += Math.Max(DpiScale.V(this, 40), FormFontPolicy.RowStep(this));

            int btnW = DpiScale.V(this, 80);
            int btnGap = DpiScale.V(this, 8);
            // F11 导入/导出（API Key 由确认框决定是否含明文）+ 保存/取消；
            // 左组[导入 导出]右组[取消 保存]（Windows 惯例主按钮最右）
            var importButton = new AntdUI.Button {
                Text = L10n.T("导入…"),
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX, y)
            };
            importButton.Click += OnImportClick;
            Controls.Add(importButton);

            var exportButton = new AntdUI.Button {
                Text = L10n.T("导出…"),
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX + (btnW + btnGap), y)
            };
            exportButton.Click += OnExportClick;
            Controls.Add(exportButton);

            var cancelButton = new AntdUI.Button {
                Text = L10n.T("取消"),
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX + boxW - btnW, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            var saveButton = new AntdUI.Button {
                Text = L10n.T("保存"),
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX + boxW - btnW * 2 - btnGap, y)
            };
            saveButton.Click += OnSaveClick;
            Controls.Add(saveButton);

            // 客户区高度随内容自适应（修复：固定 500 在大字号下裁剪底部按钮）
            ClientSize = new Size(boxX + boxW + pad, y + fieldH + DpiScale.V(this, 16));

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private AntdUI.Label MakeLabel(string text, int x, int y, int fieldH)
        {
            int offset = Math.Max(4, (fieldH - FontHeight) / 2);
            return FormFontPolicy.FitSingleLineLabel(new AntdUI.Label { Text = text,  Location = new Point(x, y + offset) });
        }

        private AntdUI.Input AddField(string labelText, int labelX, int boxX, int y, int boxW, int fieldH)
        {
            Controls.Add(MakeLabel(labelText, labelX, y, fieldH));
            var box = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, fieldH)
            };
            Controls.Add(box);
            return box;
        }

        private void LoadCurrentConfig()
        {
            try
            {
                _currentConfig = _modelStore.GetDefault();
                if (_currentConfig != null)
                {
                    _nameBox.Text = _currentConfig.Name ?? "";
                    _endpointBox.Text = _currentConfig.Endpoint ?? "";
                    _apiKeyBox.Text = _currentConfig.ApiKey ?? "";
                    _modelBox.Text = _currentConfig.Model ?? "";
                    _maxTokensSpinner.Value = _currentConfig.MaxTokens ?? 2048;
                    _temperatureSpinner.Value = (decimal)(_currentConfig.Temperature ?? 0.7);
                    _statusLabel.Text = L10n.Tf("当前模型：{0}（已使用 {1} tokens）", _currentConfig.Model, _currentConfig.TotalTokensUsed.ToString("N0"));
                }
                else
                {
                    _nameBox.Text = L10n.T("默认模型");
                    _endpointBox.Text = "https://api.openai.com/v1";
                    _modelBox.Text = "gpt-4";
                    _maxTokensSpinner.Value = 2048;
                    _temperatureSpinner.Value = 0.7m;
                    _statusLabel.Text = L10n.T("未配置模型，请填写以上信息后保存");
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = L10n.T("加载配置失败：") + ex.Message;
                _statusLabel.ForeColor = GdtermColorTable.Danger;
            }
        }

        /// <summary>F11 导出——YesNo 决定是否含明文 API Key；掩码导出时文件可安全分享，导入端保留本机 Key。</summary>
        private void OnExportClick(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_endpointBox.Text) || string.IsNullOrWhiteSpace(_modelBox.Text))
            {
                _statusLabel.Text = L10n.T("端点 URL 和模型名称未填写完整，无法导出");
                _statusLabel.ForeColor = GdtermColorTable.Danger;
                return;
            }

            // 确认框语义：确认=含明文 Key，取消=掩码导出（AntdUI.Modal 无自定义按钮文本的重载）
            var include = AntdUI.Modal.open(this,
                L10n.T("导出 AI 设置"),
                L10n.T("是否将明文 API Key 写入导出文件？\n\n· 点「确认」：文件含明文密钥，请妥善保管、勿外传\n· 点「取消」：掩码导出（Key 留空），导入到别处时保留该机器现有 Key"),
                AntdUI.TType.Warn);
            bool includeKey = include == DialogResult.Yes || include == DialogResult.OK;
            if (include == DialogResult.Cancel) return; // 对话框被关闭（非按钮）= 放弃导出

            using (var ofd = new System.Windows.Forms.SaveFileDialog
            {
                Title = L10n.T("导出 AI 设置"),
                Filter = "AI 设置 (*.json)|*.json",
                FileName = "gdterm-ai-settings.json"
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    AiSettingsFile.ExportTo(
                        ofd.FileName,
                        _nameBox.Text.Trim(),
                        _endpointBox.Text.Trim(),
                        _modelBox.Text.Trim(),
                        (int)_maxTokensSpinner.Value,
                        (double)_temperatureSpinner.Value,
                        _apiKeyBox.Text,
                        includeKey);
                    _statusLabel.Text = L10n.T("已导出") + (includeKey ? "" : L10n.T("（掩码，不含 Key）"));
                    _statusLabel.ForeColor = GdtermColorTable.Success;
                }
                catch (System.Exception ex)
                {
                    _statusLabel.Text = L10n.Tf("导出失败: {0}", ex.Message);
                    _statusLabel.ForeColor = GdtermColorTable.Danger;
                }
            }
        }

        /// <summary>F11 导入——读文件回显到表单（不直接保存，用户确认后再点保存生效）；
        /// 文件 Key 为空时保留当前表单里的 Key。</summary>
        private void OnImportClick(object sender, EventArgs e)
        {
            using (var ofd = new System.Windows.Forms.OpenFileDialog
            {
                Title = L10n.T("导入 AI 设置"),
                Filter = "AI 设置 (*.json)|*.json"
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                AiSettingsFile.ImportResult r;
                try { r = AiSettingsFile.ImportFrom(ofd.FileName); }
                catch (System.Exception ex)
                {
                    _statusLabel.Text = L10n.Tf("导入失败: {0}", ex.Message);
                    _statusLabel.ForeColor = GdtermColorTable.Danger;
                    return;
                }
                if (!string.IsNullOrEmpty(r.Name)) _nameBox.Text = r.Name;
                _endpointBox.Text = r.Endpoint;
                _modelBox.Text = r.Model;
                if (!r.KeyPresent) _statusLabel.Text = L10n.T("已导入（掩码文件，API Key 保留本机现有值）");
                else
                {
                    _apiKeyBox.Text = r.ApiKey;
                    _statusLabel.Text = L10n.T("已导入（含明文 Key）");
                }
                if (r.MaxTokens.HasValue) _maxTokensSpinner.Value = r.MaxTokens.Value;
                if (r.Temperature.HasValue) _temperatureSpinner.Value = (decimal)r.Temperature.Value;
                _statusLabel.ForeColor = GdtermColorTable.Success;
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            // 验证必填项
            if (string.IsNullOrWhiteSpace(_endpointBox.Text))
            {
                _statusLabel.Text = L10n.T("端点 URL 不能为空");
                _statusLabel.ForeColor = GdtermColorTable.Danger;
                _endpointBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(_modelBox.Text))
            {
                _statusLabel.Text = L10n.T("模型名称不能为空");
                _statusLabel.ForeColor = GdtermColorTable.Danger;
                _modelBox.Focus();
                return;
            }

            try
            {
                if (_currentConfig != null)
                {
                    // 更新现有配置
                    _currentConfig.Name = _nameBox.Text.Trim();
                    _currentConfig.Endpoint = _endpointBox.Text.Trim();
                    _currentConfig.ApiKey = _apiKeyBox.Text;
                    _currentConfig.Model = _modelBox.Text.Trim();
                    _currentConfig.MaxTokens = (int)_maxTokensSpinner.Value;
                    _currentConfig.Temperature = (double)_temperatureSpinner.Value;
                    _modelStore.Update(_currentConfig);
                }
                else
                {
                    // 创建新配置
                    var config = new AiModelConfig
                    {
                        Name = _nameBox.Text.Trim(),
                        Endpoint = _endpointBox.Text.Trim(),
                        ApiKey = _apiKeyBox.Text,
                        Model = _modelBox.Text.Trim(),
                        MaxTokens = (int)_maxTokensSpinner.Value,
                        Temperature = (double)_temperatureSpinner.Value,
                        IsDefault = true
                    };
                    _modelStore.Add(config);
                }

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = L10n.T("保存失败：") + ex.Message;
                _statusLabel.ForeColor = GdtermColorTable.Danger;
            }
        }
    }
}
