using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.AI;
using Gdterm.AI.Models;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// AI 设置对话框——配置 API 端点、密钥、模型参数
    /// 从 AiModelStore 加载默认模型配置，保存后回写
    /// </summary>
    public class AiSettingsForm : Form
    {
        private readonly AiModelStore _modelStore;
        private AiModelConfig _currentConfig;

        private TextBox _nameBox;
        private TextBox _endpointBox;
        private TextBox _apiKeyBox;
        private TextBox _modelBox;
        private NumericUpDown _maxTokensSpinner;
        private NumericUpDown _temperatureSpinner;
        private Label _statusLabel;
        private CheckBox _showKeyCheck;

        public AiSettingsForm(AiModelStore modelStore)
        {
            _modelStore = modelStore;
            InitializeComponent();
            // 高/低 DPI 自适应：声明设计基准 96 DPI，让 .NET 自动按当前 DPI 缩放控件。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            LoadCurrentConfig();
        }

        private void InitializeComponent()
        {
            Text = "AI 设置";
            Size = new Size(500, 400);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 30, 30);

            // 标题
            var titleLabel = new Label
            {
                Text = "AI 模型配置",
                Font = new Font("Microsoft YaHei", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 12),
                Size = new Size(200, 30)
            };

            var subtitleLabel = new Label
            {
                Text = "配置 OpenAI 兼容 API 端点和模型参数",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(160, 160, 160),
                Location = new Point(20, 42),
                Size = new Size(400, 20)
            };

            // 表单区域
            int y = 75;
            int labelW = 90;
            int boxX = 120;
            int boxW = 340;

            _nameBox = AddField("配置名称：", ref y, labelW, boxX, boxW);
            _endpointBox = AddField("端点 URL：", ref y, labelW, boxX, boxW);
            WinFormsCompat.SetCueBanner(_endpointBox, "https://api.openai.com/v1");

            // API Key（带掩码）
            var apiKeyLabel = new Label
            {
                Text = "API Key：",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(20, y),
                Size = new Size(labelW, 22)
            };
            _apiKeyBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW - 70, 22),
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = true
            };
            _showKeyCheck = new CheckBox
            {
                Text = "显示",
                Location = new Point(boxX + boxW - 60, y),
                Size = new Size(55, 22),
                Font = new Font("Microsoft YaHei", 8.5f),
                ForeColor = Color.FromArgb(160, 160, 160)
            };
            _showKeyCheck.CheckedChanged += (s, e) =>
            {
                _apiKeyBox.UseSystemPasswordChar = !_showKeyCheck.Checked;
            };
            Controls.Add(apiKeyLabel);
            Controls.Add(_apiKeyBox);
            Controls.Add(_showKeyCheck);
            y += 30;

            _modelBox = AddField("模型名称：", ref y, labelW, boxX, boxW);
            WinFormsCompat.SetCueBanner(_modelBox, "gpt-4");

            // Max Tokens
            var maxTokensLabel = new Label
            {
                Text = "最大Token：",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(20, y),
                Size = new Size(labelW, 22)
            };
            _maxTokensSpinner = new NumericUpDown
            {
                Location = new Point(boxX, y),
                Size = new Size(120, 22),
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                Minimum = 1,
                Maximum = 128000,
                Value = 2048,
                Increment = 256
            };
            Controls.Add(maxTokensLabel);
            Controls.Add(_maxTokensSpinner);
            y += 30;

            // Temperature
            var tempLabel = new Label
            {
                Text = "温度：",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(20, y),
                Size = new Size(labelW, 22)
            };
            _temperatureSpinner = new NumericUpDown
            {
                Location = new Point(boxX, y),
                Size = new Size(120, 22),
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                Minimum = 0m,
                Maximum = 2m,
                Value = 0.7m,
                DecimalPlaces = 1,
                Increment = 0.1m
            };
            Controls.Add(tempLabel);
            Controls.Add(_temperatureSpinner);
            y += 40;

            // 分隔线
            var separator = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Location = new Point(20, y),
                Size = new Size(440, 2)
            };
            Controls.Add(separator);
            y += 15;

            // 状态栏
            _statusLabel = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei", 8.5f),
                ForeColor = Color.FromArgb(160, 160, 160),
                Location = new Point(20, y),
                Size = new Size(300, 20)
            };
            Controls.Add(_statusLabel);
            y += 25;

            // 按钮
            var saveButton = new Button
            {
                Text = "保存",
                Size = new Size(90, 32),
                Location = new Point(boxX + boxW - 190, y),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9.5f),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            saveButton.Click += OnSaveClick;

            var cancelButton = new Button
            {
                Text = "取消",
                Size = new Size(90, 32),
                Location = new Point(boxX + boxW - 90, y),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9.5f),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[]
            {
                titleLabel, subtitleLabel,
                saveButton, cancelButton
            });

            AcceptButton = saveButton;
            CancelButton = cancelButton;
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
                _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            // 验证必填项
            if (string.IsNullOrWhiteSpace(_endpointBox.Text))
            {
                _statusLabel.Text = "端点 URL 不能为空";
                _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
                _endpointBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(_modelBox.Text))
            {
                _statusLabel.Text = "模型名称不能为空";
                _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
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
                _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
            }
        }

        private TextBox AddField(string labelText, ref int y, int labelW, int boxX, int boxW)
        {
            var label = new Label
            {
                Text = labelText,
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(204, 204, 204),
                Location = new Point(20, y),
                Size = new Size(labelW, 22)
            };

            var textBox = new TextBox
            {
                Location = new Point(boxX, y),
                Size = new Size(boxW, 22),
                Font = new Font("Microsoft YaHei", 9f),
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.FromArgb(204, 204, 204),
                BorderStyle = BorderStyle.FixedSingle
            };

            Controls.Add(label);
            Controls.Add(textBox);
            y += 30;
            return textBox;
        }
    }
}
