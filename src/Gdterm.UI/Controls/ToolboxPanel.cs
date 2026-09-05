using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Tools;
using Gdterm.UI.Diagnostics;

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
        private ISshRemoteSession _remoteSession;

        public ToolboxPanel(ToolRegistry registry)
        {
            _registry = registry;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            // 左侧工具列表
            var split = new Splitter { Dock = DockStyle.Left, Width = 3, BackColor = GdtermColorTable.Surface };

            _lvTools = new ListView
            {
                Dock = DockStyle.Left,
                Width = 220,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(),
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
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(15)
            };

            _lblTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 35,
                Font = Services.FormFontPolicy.UiFont(+5f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Foreground,
                Text = "运维工具箱"
            };

            _lblDescription = new Label
            {
                Dock = DockStyle.Top,
                Height = 25,
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = GdtermColorTable.Muted,
                Text = "选择左侧工具开始使用"
            };

            var outputSplit = new Splitter { Dock = DockStyle.Top, Height = 3, BackColor = GdtermColorTable.Surface };

            _txtOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
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
                groupItem.ForeColor = GdtermColorTable.Info;
                groupItem.Font = Services.FormFontPolicy.UiFont(0f, FontStyle.Bold);
                _lvTools.Items.Add(groupItem);

                foreach (var tool in _registry.GetByCategory(cat))
                {
                    var item = new ListViewItem("    " + tool.DisplayName);
                    item.Tag = tool;
                    item.ForeColor = GdtermColorTable.Foreground;
                    _lvTools.Items.Add(item);
                }
            }
        }

        /// <summary>
        /// 绑定当前活动远程会话，并注入所有 IRemoteToolModule。
        /// session 为 null 时清除远程会话。
        /// </summary>
        public void SetRemoteSession(ISshRemoteSession session)
        {
            _remoteSession = session;
            try
            {
                foreach (var tool in _registry.GetAllTools())
                {
                    var remote = tool as IRemoteToolModule;
                    if (remote == null) continue;
                    if (session != null && session.IsConnected)
                        remote.SetSshSession(session);
                    else
                        remote.ClearSshSession();
                }
            }
            catch (Exception ex) { Gdterm.UI.Diagnostics.DiagLog.Swallowed("Toolbox.SetRemoteSession", ex); }
        }

        /// <summary>兼容旧调用名</summary>
        public void SetSshClient(ISshRemoteSession client) { SetRemoteSession(client); }

        private void OnToolSelected(object sender, EventArgs e)
        {
            if (_lvTools.SelectedItems.Count == 0) return;

            var item = _lvTools.SelectedItems[0];
            var tool = item.Tag as IToolModule;
            if (tool == null) return;

            // 每次切换工具时刷新远程会话绑定
            var remote = tool as IRemoteToolModule;
            if (remote != null)
            {
                if (_remoteSession != null && _remoteSession.IsConnected)
                    remote.SetSshSession(_remoteSession);
                else
                    remote.ClearSshSession();
            }

            _lblTitle.Text = tool.DisplayName;
            _lblDescription.Text = tool.Description;

            // 如果工具有面板，显示面板
            var panel = tool.CreatePanel();
            if (panel != null)
            {
                panel.Dock = DockStyle.Fill;
                _txtOutput.Visible = false;
                // finding-04：移除旧工具面板并显式 Dispose——此前只 Remove 不释放，
                // 每次切换都在 Controls 树里残留一整棵面板子树（句柄/事件订阅）。
                for (int i = _pnlDetail.Controls.Count - 1; i >= 0; i--)
                {
                    var old = _pnlDetail.Controls[i];
                    // 常驻控件（标题/描述/输出区/分隔器）不在此容器内，这里只会命中工具面板；
                    // 但仍排除本方法刚创建、即将加入的实例以防万一。
                    if (!ReferenceEquals(old, panel) && !(old is Label))
                    {
                        _pnlDetail.Controls.RemoveAt(i);
                        old.Dispose();
                    }
                }
                _pnlDetail.Controls.Add(panel);
                panel.BringToFront();
            }
            else
            {
                _txtOutput.Visible = true;
                var hint = (_remoteSession != null && _remoteSession.IsConnected)
                    ? "已绑定活动 SSH 会话，可在工具内执行远程操作。"
                    : "未绑定 SSH：请先打开并连接一个 SSH 终端标签，再执行远程工具。";
                _txtOutput.Text = string.Format("工具: {0}\n分类: {1}\n描述: {2}\n\n{3}",
                    tool.DisplayName, tool.Category, tool.Description, hint);
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
