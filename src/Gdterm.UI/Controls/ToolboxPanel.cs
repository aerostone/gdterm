using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.Tools;
using Gdterm.UI.Diagnostics;
using Gdterm.Core;
using Gdterm.UI.Services;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 运维工具箱面板——统一管理所有运维工具
    /// </summary>
    public class ToolboxPanel : UserControl
    {
        private readonly ToolRegistry _registry;
        private readonly ListView _lvTools;
        private readonly AntdUI.Input _filterTools;
        private readonly Panel _pnlDetail;
        private readonly Panel _pnlToolList;
        private readonly AntdUI.Label _lblTitle;
        private readonly AntdUI.Label _lblDescription;
        private readonly RichTextBox _txtOutput;
        private ISshRemoteSession _remoteSession;
        private EmptyStateView _emptyTools;

        public ToolboxPanel(ToolRegistry registry)
        {
            _registry = registry;
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;

            // 左侧工具列表（F5：上方加过滤框）
            var split = new Splitter { Dock = DockStyle.Left, Width = 3, BackColor = GdtermColorTable.Surface };

            _pnlToolList = new Panel
            {
                Dock = DockStyle.Left,
                Width = Services.DpiScale.V(this, 220),
                BackColor = GdtermColorTable.Surface
            };

            _filterTools = new AntdUI.Input
            {
                Dock = DockStyle.Top,
                MinimumSize = new Size(0, Services.FormFontPolicy.FieldHeight(this)),
                Margin = Services.DpiScale.Pad(this, 4, 4, 4, 2),
                PlaceholderText = L10n.T("过滤工具…"),
                AllowClear = true,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont()
            };
            _filterTools.TextChanged += (s, e) => ApplyToolFilter();

            _lvTools = new ListView
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Surface,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.UiFont(),
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.None
            };
            _lvTools.Columns.Add("", 210);
            _lvTools.SelectedIndexChanged += OnToolSelected;

            _pnlToolList.Controls.Add(_lvTools);
            _pnlToolList.Controls.Add(_filterTools);
            // 列表空态：过滤把工具滤空时提示清搜索，而不是留一整片背景色（R09 / UX 审计 C1）
            _emptyTools = EmptyStateView.Attach(_pnlToolList, L10n.T("没有匹配的工具"),
                L10n.T("清空上方过滤词试试"));

            // 右侧详情面板
            _pnlDetail = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                Padding = new Padding(15)
            };

            _lblTitle = new AntdUI.Label {
                Dock = DockStyle.Top,
                Height = Math.Max(Services.DpiScale.V(this, 35), Services.FormFontPolicy.LineBox(Services.FormFontPolicy.UiFont(+5f), this)),
                Font = Services.FormFontPolicy.UiFont(+5f, FontStyle.Bold),
                ForeColor = GdtermColorTable.Foreground,
                Text = L10n.T("运维工具箱")
            };

            _lblDescription = new AntdUI.Label {
                Dock = DockStyle.Top,
                Height = Math.Max(Services.DpiScale.V(this, 25), Services.FormFontPolicy.LineBox(Services.FormFontPolicy.UiFont(), this)),
                Font = Services.FormFontPolicy.UiFont(),
                ForeColor = GdtermColorTable.Muted,
                Text = L10n.T("选择左侧工具开始使用")
            };

            var outputSplit = new Splitter { Dock = DockStyle.Top, Height = 3, BackColor = GdtermColorTable.Surface };

            _txtOutput = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = GdtermColorTable.Background,
                ForeColor = GdtermColorTable.Foreground,
                Font = Services.FormFontPolicy.MonoFont(),
                ReadOnly = true
            };
            // 结果复制：右键菜单（全选/复制/清空），路线图 tools-ui“结果复制”口径。
            var outputMenu = new ContextMenuStrip
            {
                BackColor = GdtermColorTable.Surface2,
                ForeColor = GdtermColorTable.Foreground
            };
            var miCopyAll = new ToolStripMenuItem(L10n.T("复制全部"));
            miCopyAll.Click += (s, e) => { try { Clipboard.SetText(_txtOutput.Text ?? ""); } catch { } };
            var miCopySel = new ToolStripMenuItem(L10n.T("复制选中"));
            miCopySel.Click += (s, e) => { try { if (!string.IsNullOrEmpty(_txtOutput.SelectedText)) Clipboard.SetText(_txtOutput.SelectedText); } catch { } };
            var miClear = new ToolStripMenuItem(L10n.T("清空"));
            miClear.Click += (s, e) => { try { _txtOutput.Clear(); } catch { } };
            outputMenu.Items.Add(miCopySel);
            outputMenu.Items.Add(miCopyAll);
            outputMenu.Items.Add(new ToolStripSeparator());
            outputMenu.Items.Add(miClear);
            _txtOutput.ContextMenuStrip = outputMenu;

            _pnlDetail.Controls.Add(_txtOutput);
            _pnlDetail.Controls.Add(outputSplit);
            _pnlDetail.Controls.Add(_lblDescription);
            _pnlDetail.Controls.Add(_lblTitle);

            Controls.Add(_pnlDetail);
            Controls.Add(split);
            Controls.Add(_pnlToolList);

            LoadTools();
            // 空态默认 Visible=false；LoadTools 之后才知道列表是否真的空，
            // 否则「没有工具」这层要等用户敲过滤词才出现（或反过来盖住已填好的列表）
            if (_emptyTools != null) _emptyTools.SetEmpty(_lvTools.Items.Count == 0);
            RestoreLastTool();
            // 侧板是 360 宽，工具列表写死 220 的话右侧详情只剩 ~137（减掉 15px padding 后正文 107px），
            // 输出一行日志要折三折——等于读不了。列表宽按宿主比例给，并给详情留硬下限。
            ApplyToolListWidth();
            SizeChanged += (s2, e) => ApplyToolListWidth();
            // A11y：自绘/符号控件回填无障碍名（UiA11y 统一入口，幂等——只填空名）
            UiA11y.AutoName(this);
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

        /// <summary>按关键词过滤工具列表（F5；分类头仅在其下有可见工具时保留）。</summary>
        private void ApplyToolFilter()
        {
            if (_registry == null) return;
            var q = (_filterTools.Text ?? "").Trim().ToLowerInvariant();
            string currentToolId = null;
            foreach (ListViewItem it in _lvTools.SelectedItems)
            {
                var t = it.Tag as IToolModule;
                if (t != null) { currentToolId = t.ToolId; break; }
            }

            _lvTools.BeginUpdate();
            _lvTools.Items.Clear();
            var visibleToolId = "";
            foreach (var cat in _registry.GetCategories())
            {
                var catVisible = false;
                foreach (var tool in _registry.GetByCategory(cat))
                {
                    if (q.Length > 0)
                    {
                        var hay = ((tool.DisplayName ?? "") + " " + (tool.ToolId ?? "") + " " + (tool.Description ?? "")).ToLowerInvariant();
                        if (!hay.Contains(q)) continue;
                    }
                    if (!catVisible)
                    {
                        var groupItem = new ListViewItem("▸ " + cat);
                        groupItem.ForeColor = GdtermColorTable.Info;
                        groupItem.Font = Services.FormFontPolicy.UiFont(0f, FontStyle.Bold);
                        _lvTools.Items.Add(groupItem);
                        catVisible = true;
                    }
                    var item = new ListViewItem("    " + tool.DisplayName);
                    item.Tag = tool;
                    item.ForeColor = GdtermColorTable.Foreground;
                    _lvTools.Items.Add(item);
                    if (tool.ToolId == currentToolId) visibleToolId = tool.ToolId;
                }
            }
            _lvTools.EndUpdate();

            if (_emptyTools != null)
            {
                bool none = _lvTools.Items.Count == 0;
                bool filtered = none && q.Length > 0;
                if (filtered)
                    _emptyTools.SetEmpty(true, L10n.T("没有匹配的工具"), L10n.T("清空上方过滤词试试"));
                else
                    _emptyTools.SetEmpty(true, L10n.T("工具箱是空的"), L10n.T("没有注册任何运维工具"));
            }

            // 过滤后保持选中（当前工具仍可见时）
            if (visibleToolId.Length > 0)
            {
                foreach (ListViewItem it in _lvTools.Items)
                {
                    var t = it.Tag as IToolModule;
                    if (t != null && t.ToolId == visibleToolId) { it.Selected = true; break; }
                }
            }
        }

        /// <summary>恢复上次使用的工具（form-settings.ini [toolbox] lastTool；缺失/已移除则不动）。</summary>
        /// <summary>
        /// 工具列表宽度自适应：宿主越窄，列表越窄（详情下限 200 设计 px 先保）。
        /// 宽宿主（独立窗口/大侧板）仍是原来的 220，视觉不变。
        /// </summary>
        private void ApplyToolListWidth()
        {
            try
            {
                if (_pnlToolList == null) return;
                int host = ClientSize.Width;
                if (host <= 0) return;
                int minDetail = Services.DpiScale.S(this, 200, 0).Width;   // 详情区硬下限（设计 px → 当前 DPI）
                int want = Math.Min((int)(host * 0.4), Services.DpiScale.S(this, 220, 0).Width);
                int maxList = Math.Max(Services.DpiScale.S(this, 130, 0).Width, host - minDetail - 3 /*splitter*/);
                if (want > maxList) want = maxList;
                int floor = Services.DpiScale.S(this, 130, 0).Width;
                if (want < floor) want = floor;
                if (_pnlToolList.Width != want) _pnlToolList.Width = want;
                // 列宽跟着列表走：列宽写死 210 而列表只有 130，会在列表里生出横向滚动条（比省略号更难用）
                try
                {
                    if (_lvTools != null && _lvTools.Columns.Count > 0)
                    {
                        int colW = Math.Max(Services.DpiScale.S(this, 100, 0).Width, want - Services.DpiScale.S(this, 6, 0).Width);
                        if (_lvTools.Columns[0].Width != colW) _lvTools.Columns[0].Width = colW;
                    }
                }
                catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ToolboxPanel.ApplyToolListWidth.col", exSwallowed); } catch { } }
            }
            catch (System.Exception exSwallowed) { try { DiagLog.Swallowed("ToolboxPanel.ApplyToolListWidth", exSwallowed); } catch { } }
        }

        private void RestoreLastTool()
        {
            try
            {
                var last = FormSettings.Get("toolbox", "lastTool", "");
                if (string.IsNullOrEmpty(last)) return;
                foreach (ListViewItem it in _lvTools.Items)
                {
                    var t = it.Tag as IToolModule;
                    if (t != null && t.ToolId == last)
                    {
                        it.Selected = true;
                        break;
                    }
                }
            }
            catch (Exception ex) { Gdterm.UI.Diagnostics.DiagLog.Swallowed("Toolbox.RestoreLastTool", ex); }
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

            // 记住上次使用的工具（form-settings.ini [toolbox] lastTool）
            try { FormSettings.Set("toolbox", "lastTool", tool.ToolId); } catch { }

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
                    ? L10n.T("已绑定活动 SSH 会话，可在工具内执行远程操作。")
                    : L10n.T("未绑定 SSH：请先打开并连接一个 SSH 终端标签，再执行远程工具。");
                _txtOutput.Text = string.Format(L10n.T("工具: {0}\n分类: {1}\n描述: {2}\n\n{3}"),
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
