using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace Gdterm.UI.Forms
{
    /// <summary>
    /// 密码生成器——独立工具
    /// 支持：长度调节、字符集选择、一键复制、批量生成
    /// </summary>
    public class PasswordGeneratorForm : Form
    {
        private NumericUpDown _lengthSpinner;
        private CheckBox _upperCheck;
        private CheckBox _lowerCheck;
        private CheckBox _digitCheck;
        private CheckBox _specialCheck;
        private CheckBox _ambiguousCheck;
        private TextBox _resultBox;
        private ListBox _historyList;
        private Label _strengthLabel;

        private static readonly string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private static readonly string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
        private static readonly string DigitChars = "0123456789";
        private static readonly string SpecialChars = "!@#$%^&*()-_=+[]{}|;:',.<>?/";
        private static readonly string AmbiguousChars = "Il1O0";

        public PasswordGeneratorForm()
        {
            InitializeComponent();
            GeneratePassword();
        }

        private void InitializeComponent()
        {
            Text = "密码生成器";
            Size = new Size(500, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(35, 35, 35);

            // 标题
            var titleLabel = new Label
            {
                Text = "🔑 密码生成器",
                Font = new Font("Microsoft YaHei", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(15, 12),
                Size = new Size(200, 30)
            };

            // 密码长度
            var lengthLabel = new Label
            {
                Text = "密码长度：",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(15, 55),
                Size = new Size(80, 25)
            };

            _lengthSpinner = new NumericUpDown
            {
                Location = new Point(100, 53),
                Size = new Size(70, 25),
                Minimum = 8,
                Maximum = 128,
                Value = 16,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f)
            };
            _lengthSpinner.ValueChanged += (s, e) => GeneratePassword();

            // 快捷长度按钮
            int[] quickLengths = { 12, 16, 20, 24, 32 };
            int btnX = 180;
            foreach (var len in quickLengths)
            {
                var btn = new Button
                {
                    Text = len.ToString(),
                    Location = new Point(btnX, 52),
                    Size = new Size(40, 26),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei", 9f),
                    BackColor = Color.FromArgb(60, 60, 60),
                    ForeColor = Color.White,
                    Tag = len
                };
                btn.Click += (s, e) => { _lengthSpinner.Value = (int)((Button)s).Tag; };
                Controls.Add(btn);
                btnX += 45;
            }

            // 字符集选项
            var charsetGroup = new GroupBox
            {
                Text = "字符集",
                Font = new Font("Microsoft YaHei", 9.5f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(15, 90),
                Size = new Size(455, 100)
            };

            _upperCheck = CreateCheck("大写字母 (A-Z)", 15, 25, true);
            _lowerCheck = CreateCheck("小写字母 (a-z)", 15, 50, true);
            _digitCheck = CreateCheck("数字 (0-9)", 250, 25, true);
            _specialCheck = CreateCheck("特殊字符 (!@#$...)", 250, 50, true);
            _ambiguousCheck = CreateCheck("排除易混淆字符 (Il1O0)", 15, 75, false);
            _ambiguousCheck.CheckedChanged += (s, e) => GeneratePassword();

            charsetGroup.Controls.AddRange(new Control[]
            {
                _upperCheck, _lowerCheck, _digitCheck, _specialCheck, _ambiguousCheck
            });

            // 生成结果
            var resultLabel = new Label
            {
                Text = "生成结果：",
                Font = new Font("Microsoft YaHei", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(15, 200),
                Size = new Size(80, 25)
            };

            _resultBox = new TextBox
            {
                Location = new Point(15, 228),
                Size = new Size(350, 35),
                Font = new Font("Consolas", 14f, FontStyle.Bold),
                BackColor = Color.FromArgb(25, 25, 25),
                ForeColor = Color.FromArgb(80, 220, 80),
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true
            };

            // 密码强度指示
            _strengthLabel = new Label
            {
                Text = "强度：—",
                Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(200, 200, 200),
                Location = new Point(15, 270),
                Size = new Size(200, 25)
            };

            // 操作按钮
            var generateBtn = new Button
            {
                Text = "🔄 重新生成",
                Location = new Point(375, 226),
                Size = new Size(95, 38),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 10f),
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White
            };
            generateBtn.Click += (s, e) => GeneratePassword();

            var copyBtn = new Button
            {
                Text = "📋 复制",
                Location = new Point(375, 268),
                Size = new Size(95, 38),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 10f),
                BackColor = Color.FromArgb(60, 130, 60),
                ForeColor = Color.White
            };
            copyBtn.Click += OnCopyClick;

            var generate10Btn = new Button
            {
                Text = "批量生成 10 个",
                Location = new Point(15, 300),
                Size = new Size(120, 30),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9f),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            generate10Btn.Click += OnBatchGenerate;

            // 历史记录
            var historyLabel = new Label
            {
                Text = "本次生成记录（双击复制）：",
                Font = new Font("Microsoft YaHei", 9.5f),
                ForeColor = Color.FromArgb(180, 180, 180),
                Location = new Point(15, 340),
                Size = new Size(250, 22)
            };

            _historyList = new ListBox
            {
                Location = new Point(15, 365),
                Size = new Size(455, 105),
                Font = new Font("Consolas", 10f),
                BackColor = Color.FromArgb(25, 25, 25),
                ForeColor = Color.FromArgb(200, 200, 200),
                BorderStyle = BorderStyle.FixedSingle
            };
            _historyList.DoubleClick += OnHistoryDoubleClick;

            Controls.AddRange(new Control[]
            {
                titleLabel,
                lengthLabel, _lengthSpinner, charsetGroup,
                resultLabel, _resultBox, _strengthLabel,
                generateBtn, copyBtn, generate10Btn,
                historyLabel, _historyList
            });
        }

        private CheckBox CreateCheck(string text, int x, int y, bool isChecked)
        {
            var cb = new CheckBox
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(220, 22),
                Font = new Font("Microsoft YaHei", 9.5f),
                ForeColor = Color.FromArgb(200, 200, 200),
                Checked = isChecked
            };
            cb.CheckedChanged += (s, e) => GeneratePassword();
            return cb;
        }

        private void GeneratePassword()
        {
            var charset = BuildCharset();
            if (charset.Length == 0)
            {
                _resultBox.Text = "";
                _strengthLabel.Text = "强度：请至少选择一种字符集";
                _strengthLabel.ForeColor = Color.FromArgb(255, 100, 100);
                return;
            }

            int length = (int)_lengthSpinner.Value;
            _resultBox.Text = GenerateRandomString(charset, length);
            UpdateStrength(length, charset.Length);
        }

        private string BuildCharset()
        {
            var sb = new StringBuilder();
            if (_upperCheck.Checked) sb.Append(UppercaseChars);
            if (_lowerCheck.Checked) sb.Append(LowercaseChars);
            if (_digitCheck.Checked) sb.Append(DigitChars);
            if (_specialCheck.Checked) sb.Append(SpecialChars);

            var charset = sb.ToString();

            // 排除易混淆字符
            if (_ambiguousCheck.Checked)
            {
                foreach (var c in AmbiguousChars)
                    charset = charset.Replace(c.ToString(), "");
            }

            return charset;
        }

        private static string GenerateRandomString(string charset, int length)
        {
            var result = new char[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                var buffer = new byte[4];
                for (int i = 0; i < length; i++)
                {
                    rng.GetBytes(buffer);
                    var index = (int)(BitConverter.ToUInt32(buffer, 0) % (uint)charset.Length);
                    result[i] = charset[index];
                }
            }
            return new string(result);
        }

        private void UpdateStrength(int length, int charsetSize)
        {
            // 简单强度评估：熵位数
            double entropy = length * Math.Log2(charsetSize);

            string strength;
            Color color;
            if (entropy < 40) { strength = "弱"; color = Color.FromArgb(255, 80, 80); }
            else if (entropy < 60) { strength = "中"; color = Color.FromArgb(255, 200, 60); }
            else if (entropy < 80) { strength = "强"; color = Color.FromArgb(80, 220, 80); }
            else { strength = "极强"; color = Color.FromArgb(60, 200, 255); }

            _strengthLabel.Text = $"强度：{strength}（{entropy:F0} bit 熵）";
            _strengthLabel.ForeColor = color;
        }

        private void OnCopyClick(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_resultBox.Text))
            {
                try
                {
                    Clipboard.SetText(_resultBox.Text);
                    AddToHistory(_resultBox.Text);

                    // 短暂提示
                    var btn = (Button)sender;
                    var originalText = btn.Text;
                    btn.Text = "✓ 已复制";
                    var timer = new Timer { Interval = 1500 };
                    timer.Tick += (s, ev) => { btn.Text = originalText; timer.Stop(); timer.Dispose(); };
                    timer.Start();
                }
                catch { }
            }
        }

        private void OnBatchGenerate(object sender, EventArgs e)
        {
            var charset = BuildCharset();
            if (charset.Length == 0) return;

            int length = (int)_lengthSpinner.Value;
            for (int i = 0; i < 10; i++)
            {
                var pwd = GenerateRandomString(charset, length);
                AddToHistory(pwd);
            }

            // 用最后一个作为当前结果
            if (_historyList.Items.Count > 0)
                _resultBox.Text = _historyList.Items[0].ToString();
        }

        private void OnHistoryDoubleClick(object sender, EventArgs e)
        {
            if (_historyList.SelectedItem is string selected)
            {
                try
                {
                    Clipboard.SetText(selected);
                }
                catch { }
            }
        }

        private void AddToHistory(string password)
        {
            // 限制历史记录
            if (_historyList.Items.Count >= 50)
                _historyList.Items.RemoveAt(_historyList.Items.Count - 1);

            _historyList.Items.Insert(0, password);
        }
    }
}
