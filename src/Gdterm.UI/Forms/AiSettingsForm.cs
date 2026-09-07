using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

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
        }

        private void InitializeComponent()
        {
            Text = "AI 设置";
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
            int boxX = pad + DpiScale.V(this, 110);
            int boxW = DpiScale.V(this, 350);
            int fieldH = Math.Max(DpiScale.V(this, 38), FormFontPolicy.RowStep(this));
            int rowH = fieldH + DpiScale.V(this, 10);
            int y = DpiScale.V(this, 18);

            var titleLabel = new AntdUI.Label {
                Text = "AI 模型配置",
                Font = Gdterm.UI.Services.FormFontPolicy.UiFont(+5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(titleLabel);
            y += Math.Max(DpiScale.V(this, 36), FormFontPolicy.RowStep(this));

            var subtitleLabel = new AntdUI.Label {
                Text = "配置 OpenAI 兼容 API 端点和模型参数",
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(subtitleLabel);
            y += Math.Max(DpiScale.V(this, 36), FormFontPolicy.RowStep(this));

            _nameBox = AddField("配置名称", labelX, boxX, y, boxW, fieldH);
            y += rowH;
            _endpointBox = AddField("端点 URL", labelX, boxX, y, boxW, fieldH);
            _endpointBox.PlaceholderText = "https://api.openai.com/v1";
            y += rowH;

            // API Key（带掩码 + 显示切换）
            Controls.Add(MakeLabel("API Key", labelX, y, fieldH));
            _apiKeyBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW - DpiScale.V(this, 76), fieldH),
                Font = new Font("Consolas", 9.5f),
                UseSystemPasswordChar = true
            };
            _showKeyCheck = new AntdUI.Checkbox {
                Text = "显示",
                AutoSize = true,
                Location = new Point(boxX + boxW - DpiScale.V(this, 66), y + DpiScale.V(this, 10))
            };
            _showKeyCheck.CheckedChanged += (s, e) =>
            {
                _apiKeyBox.UseSystemPasswordChar = !_showKeyCheck.Checked;
            };
            Controls.Add(_apiKeyBox);
            Controls.Add(_showKeyCheck);
            y += rowH;

            _modelBox = AddField("模型名称", labelX, boxX, y, boxW, fieldH);
            _modelBox.PlaceholderText = "gpt-4";
            y += rowH;

            // Max Tokens
            Controls.Add(MakeLabel("最大 Token", labelX, y, fieldH));
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
            Controls.Add(MakeLabel("温度", labelX, y, fieldH));
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
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(_statusLabel);
            y += Math.Max(DpiScale.V(this, 40), FormFontPolicy.RowStep(this));

            int btnW = DpiScale.V(this, 90);
            int btnGap = DpiScale.V(this, 8);
            // 按钮：Windows 惯例主按钮最右，取消在其左（修复：原先主按钮在左侧）
            var cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(btnW, fieldH),
                Location = new Point(boxX + boxW - btnW, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            var saveButton = new AntdUI.Button {
                Text = "保存",
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
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + offset) };
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
                    _statusLabel.Text = $"当前模型：{_currentConfig.Model}（已使用 {_currentConfig.TotalTokensUsed:N0} tokens）";
                }
                else
                {
                    _nameBox.Text = "默认模型";
                    _endpointBox.Text = "https://api.openai.com/v1";
                    _modelBox.Text = "gpt-4";
                    _maxTokensSpinner.Value = 2048;
                    _temperatureSpinner.Value = 0.7m;
                    _statusLabel.Text = "未配置模型，请填写以上信息后保存";
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = $"加载配置失败：{ex.Message}";
                _statusLabel.ForeColor = GdtermColorTable.Danger;
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            // 验证必填项
            if (string.IsNullOrWhiteSpace(_endpointBox.Text))
            {
                _statusLabel.Text = "端点 URL 不能为空";
                _statusLabel.ForeColor = GdtermColorTable.Danger;
                _endpointBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(_modelBox.Text))
            {
                _statusLabel.Text = "模型名称不能为空";
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
                _statusLabel.Text = $"保存失败：{ex.Message}";
                _statusLabel.ForeColor = GdtermColorTable.Danger;
            }
        }
    }
}
