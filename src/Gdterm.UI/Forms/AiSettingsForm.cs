using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.AI;
using Gdterm.AI.Models;
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
            Size = new Size(520, 500);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Resizable = false; // AntdUI 自绘边框忽略 FixedDialog 语义，显式禁边缘拉伸
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int pad = 20;
            int labelX = pad;
            int boxX = pad + 110;
            int boxW = 350;
            int rowH = 48;
            int y = 18;

            var titleLabel = new AntdUI.Label {
                Text = "AI 模型配置",
                Font = Gdterm.UI.Services.FormFontPolicy.UiFont(+5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(pad, y)
            };
            y += 36;

            var subtitleLabel = new AntdUI.Label {
                Text = "配置 OpenAI 兼容 API 端点和模型参数",
                AutoSize = true,
                Location = new Point(pad, y)
            };
            y += 36;

            _nameBox = AddField("配置名称", labelX, boxX, y, boxW, false);
            y += rowH;
            _endpointBox = AddField("端点 URL", labelX, boxX, y, boxW, false);
            _endpointBox.PlaceholderText = "https://api.openai.com/v1";
            y += rowH;

            // API Key（带掩码 + 显示切换）
            Controls.Add(MakeLabel("API Key", labelX, y));
            _apiKeyBox = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW - 76, 38),
                Font = new Font("Consolas", 9.5f),
                UseSystemPasswordChar = true
            };
            _showKeyCheck = new AntdUI.Checkbox {
                Text = "显示",
                AutoSize = true,
                Location = new Point(boxX + boxW - 66, y + 10)
            };
            _showKeyCheck.CheckedChanged += (s, e) =>
            {
                _apiKeyBox.UseSystemPasswordChar = !_showKeyCheck.Checked;
            };
            Controls.Add(_apiKeyBox);
            Controls.Add(_showKeyCheck);
            y += rowH;

            _modelBox = AddField("模型名称", labelX, boxX, y, boxW, false);
            _modelBox.PlaceholderText = "gpt-4";
            y += rowH;

            // Max Tokens
            Controls.Add(MakeLabel("最大 Token", labelX, y));
            _maxTokensSpinner = new AntdUI.InputNumber {
                Location = new Point(boxX, y),
                Size = new Size(140, 38),
                Minimum = 1,
                Maximum = 128000,
                Value = 2048,
                Increment = 256
            };
            Controls.Add(_maxTokensSpinner);
            y += rowH;

            // Temperature
            Controls.Add(MakeLabel("温度", labelX, y));
            _temperatureSpinner = new AntdUI.InputNumber {
                Location = new Point(boxX, y),
                Size = new Size(140, 38),
                Minimum = 0m,
                Maximum = 2m,
                Value = 0.7m,
                Increment = 0.1m,
                DecimalPlaces = 1
            };
            Controls.Add(_temperatureSpinner);
            y += rowH + 4;

            // 状态栏
            _statusLabel = new AntdUI.Label {
                Text = "",
                AutoSize = true,
                Location = new Point(pad, y)
            };
            Controls.Add(_statusLabel);
            y += 40;

            // 按钮（主按钮最右）
            var saveButton = new AntdUI.Button {
                Text = "保存",
                Type = AntdUI.TTypeMini.Primary,
                Size = new Size(90, 38),
                Location = new Point(boxX + boxW - 190, y)
            };
            saveButton.Click += OnSaveClick;
            Controls.Add(saveButton);

            var cancelButton = new AntdUI.Button {
                Text = "取消",
                Size = new Size(90, 38),
                Location = new Point(boxX + boxW - 90, y)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private static AntdUI.Label MakeLabel(string text, int x, int y)
        {
            return new AntdUI.Label { Text = text, AutoSize = true, Location = new Point(x, y + 10) };
        }

        private AntdUI.Input AddField(string labelText, int labelX, int boxX, int y, int boxW, bool password)
        {
            Controls.Add(MakeLabel(labelText, labelX, y));
            var box = new AntdUI.Input {
                Location = new Point(boxX, y),
                Size = new Size(boxW, 38)
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
