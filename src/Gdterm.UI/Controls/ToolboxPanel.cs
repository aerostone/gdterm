using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Tools;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 运维工具箱面板——统一管理所有运维工具
    /// </summary>
    public class ToolboxPanel : UserControl
    {
        private readonly ToolRegistry _registry;
        private readonly ListView _lvTools;
        private readonly Panel _pnlDetail;
        private readonly Label _lblTitle;
        private readonly Label _lblDescription;
        private readonly RichTextBox _txtOutput;

        public ToolboxPanel(ToolRegistry registry)
        {
            _registry = registry;
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 30, 30);

            // 左侧工具列表
            var split = new Splitter { Dock = DockStyle.Left, Width = 3, BackColor = Color.FromArgb(50, 50, 50) };

            _lvTools = new ListView
            {
                Dock = DockStyle.Left,
                Width = 220,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.FromArgb(204, 204, 204),
                Font = new Font("Microsoft YaHei", 9f),
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.None,
                BorderStyle = BorderStyle.None
            };
            _lvTools.Columns.Add("", 210);
            _lvTools.SelectedIndexChanged += OnToolSelected;

            // 右侧详情面板
            _pnlDetail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(15)
            };

            _lblTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 35,
                Font = new Font("Microsoft YaHei", 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 220, 220),
                Text = "运维工具箱"
            };

            _lblDescription = new Label
            {
                Dock = DockStyle.Top,
                Height = 25,
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Color.FromArgb(150, 150, 150),
                Text = "选择左侧工具开始使用"
            };

            var outputSplit = new Splitter { Dock = DockStyle.Top, Height = 3, BackColor = Color.FromArgb(50, 50, 50) };

            _txtOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Consolas", 9f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            _pnlDetail.Controls.Add(_txtOutput);
            _pnlDetail.Controls.Add(outputSplit);
            _pnlDetail.Controls.Add(_lblDescription);
            _pnlDetail.Controls.Add(_lblTitle);

            Controls.Add(_pnlDetail);
            Controls.Add(split);
            Controls.Add(_lvTools);

            LoadTools();
        }

        private void LoadTools()
        {
            if (_registry == null) return;

            var tools = _registry.GetAllTools();
            var categories = _registry.GetCategories();

            foreach (var cat in categories)
            {
                // 分类头
                var groupItem = new ListViewItem("▸ " + cat);
                groupItem.ForeColor = Color.FromArgb(86, 156, 214);
                groupItem.Font = new Font("Microsoft YaHei", 9f, FontStyle.Bold);
                _lvTools.Items.Add(groupItem);

                foreach (var tool in _registry.GetByCategory(cat))
                {
                    var item = new ListViewItem("    " + tool.DisplayName);
                    item.Tag = tool;
                    item.ForeColor = Color.FromArgb(204, 204, 204);
                    _lvTools.Items.Add(item);
                }
            }
        }

        private void OnToolSelected(object sender, EventArgs e)
        {
            if (_lvTools.SelectedItems.Count == 0) return;

            var item = _lvTools.SelectedItems[0];
            var tool = item.Tag as IToolModule;
            if (tool == null) return;

            _lblTitle.Text = tool.DisplayName;
            _lblDescription.Text = tool.Description;

            // 如果工具有面板，显示面板
            var panel = tool.CreatePanel();
            if (panel != null)
            {
                panel.Dock = DockStyle.Fill;
                _txtOutput.Visible = false;
                // 移除旧面板
                for (int i = _pnlDetail.Controls.Count - 1; i >= 0; i--)
                {
                    if (_pnlDetail.Controls[i] is UserControl)
                        _pnlDetail.Controls.RemoveAt(i);
                }
                _pnlDetail.Controls.Add(panel);
                panel.BringToFront();
            }
            else
            {
                _txtOutput.Visible = true;
                _txtOutput.Text = string.Format("工具: {0}\n分类: {1}\n描述: {2}\n\n（选择连接后在此执行工具操作）",
                    tool.DisplayName, tool.Category, tool.Description);
            }
        }

        /// <summary>向输出面板追加文本</summary>
        public void AppendOutput(string text)
        {
            if (_txtOutput.InvokeRequired)
            {
                _txtOutput.BeginInvoke(new Action(() => AppendOutput(text)));
                return;
            }
            _txtOutput.AppendText(text + "\n");
            _txtOutput.ScrollToCaret();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lvTools?.Dispose();
                _txtOutput?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
