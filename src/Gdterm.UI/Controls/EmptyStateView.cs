using System;
using System.Drawing;
using System.Windows.Forms;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 列表型 UI 的标准空态引导层（UI/UX 审计 2026-10-07 C1）。
    ///
    /// 背景：15 个列表型面板里 11 个打开即是“一整片背景色”，用户无法区分
    /// “空 / 还在加载 / 坏了”。而 KeePass 空库引导（KeePassEmptyGuide）与
    /// ScannerCenter 的 UpdateEmptyHints 已证明该形态有效，只是没有统一组件。
    ///
    /// 形态（DL §4.3 标签规格 + §3.1 间距）：
    ///   标题（Foreground，+0.5pt 粗体）
    ///   一句说明（Muted，真实换行，宽度上限 360 设计 px）
    ///   可选主操作（Primary，每空态至多一个）
    ///
    /// 用法：
    ///   <code>
    ///   _empty = EmptyStateView.Attach(this, "还没有命令历史",
    ///               "执行过的命令会自动记录在这里", null);
    ///   ...
    ///   _empty.SetEmpty(_historyList.Items.Count == 0);
    ///   </code>
    /// 必须在列表控件加入之后再 Attach（Attach 会 BringToFront 盖在列表之上）。
    /// </summary>
    public sealed class EmptyStateView : UserControl
    {
        private readonly Panel _box;
        private readonly AntdUI.Label _title;
        private readonly System.Windows.Forms.Label _desc;
        private readonly AntdUI.Button _action;

        public event EventHandler ActionClicked;

        public EmptyStateView(string title, string description = null, string actionText = null)
        {
            Dock = DockStyle.Fill;
            BackColor = GdtermColorTable.Background;
            ForeColor = GdtermColorTable.Muted;
            Visible = false;
            TabStop = false;

            _box = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                TabStop = false
            };

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                TabStop = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            _title = new AntdUI.Label
            {
                Text = title ?? string.Empty,
                AutoSize = true,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont(0.5f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, DpiScale.V(null, 6)),
                TextAlign = ContentAlignment.MiddleCenter
            };
            flow.Controls.Add(_title);
            AccessibleName = title;

            if (!string.IsNullOrEmpty(description))
            {
                // 说明用原生 Label 而不是 AntdUI.Label：AntdUI 的 Label 不换行，
                // 长句会被裁（本项目历史最高频 bug），原生 Label 在 AutoSize=false 时按宽换行。
                _desc = new System.Windows.Forms.Label
                {
                    Text = description,
                    AutoSize = false,
                    BackColor = Color.Transparent,
                    ForeColor = GdtermColorTable.Muted,
                    Font = FormFontPolicy.UiFont(-0.5f),
                    TextAlign = ContentAlignment.MiddleCenter,
                    // 每行 ~360 设计 px，高度按真实 GDI 行高推导（2 行说明不挤成一行）
                    Size = new Size(DpiScale.V(null, 360),
                        FormFontPolicy.LineBox(FormFontPolicy.UiFont(-0.5f), null, 1.7f) * 2),
                    Margin = new Padding(0, 0, 0, DpiScale.V(null, 10))
                };
                flow.Controls.Add(_desc);
            }

            if (!string.IsNullOrEmpty(actionText))
            {
                _action = new AntdUI.Button
                {
                    Text = actionText,
                    Type = AntdUI.TTypeMini.Primary,
                    AutoSize = true,
                    Cursor = Cursors.Hand,
                    // CI 0.1.349 实证：AutoSize 钮不设 Padding 会被 tree-check R-贴边 规则红
                    // （命令历史「打开终端」/ 端口转发「新建规则」双红）；对齐设计语言按钮内边距。
                    Padding = new Padding(DpiScale.V(this, 12), DpiScale.V(this, 4), DpiScale.V(this, 12), DpiScale.V(this, 4)),
                    Margin = new Padding(0)
                };
                _action.AccessibleName = actionText;
                _action.Click += (s, e) =>
                {
                    var h = ActionClicked;
                    if (h != null) h(this, EventArgs.Empty);
                };
                flow.Controls.Add(_action);
            }

            _box.Controls.Add(flow);
            Controls.Add(_box);
        }

        /// <summary>
        /// 给空态层与内部子控件起名，供 ui-smoke 按名定位（Form.Name 一旦被断言依赖，
        /// 替换实现时不能让它凭空消失：KeePassManagerEmpty 断言 KeePassEmptyGuide*）。
        /// </summary>
        public EmptyStateView WithNames(string rootName, string actionName = null, string hintName = null)
        {
            if (!string.IsNullOrEmpty(rootName)) Name = rootName;
            if (_action != null && !string.IsNullOrEmpty(actionName)) _action.Name = actionName;
            if (_desc != null && !string.IsNullOrEmpty(hintName)) _desc.Name = hintName;
            return this;
        }

        /// <summary>空态主按钮（未提供 actionText 时为 null）。</summary>
        public AntdUI.Button ActionButton { get { return _action; } }

        /// <summary>切换显示：数据源为空时 true。加载中请用 BusyScope，不要复用本组件。</summary>
        public void SetEmpty(bool empty)
        {
            if (Visible != empty) Visible = empty;
        }

        /// <summary>
        /// 切换显示并就地改写文案（同一面板要区分“确实没有”与“筛选后为空”时用）。
        /// 文案未变化时不触发重排。
        /// </summary>
        public void SetEmpty(bool empty, string title, string description = null)
        {
            if (title != null && _title.Text != title)
            {
                _title.Text = title;
                AccessibleName = title;
            }
            if (description != null && _desc != null && _desc.Text != description)
                _desc.Text = description;
            SetEmpty(empty);
            if (empty) CenterBox();
        }

        /// <summary>
        /// 把空态层挂到宿主（通常是面板自身）上并置于最前。
        /// list 仅用于可读性/顺序约定：本方法不订阅列表事件，调用方在刷新末尾显式 SetEmpty。
        /// </summary>
        public static EmptyStateView Attach(Control host, string title, string description = null,
            string actionText = null, EventHandler onAction = null)
        {
            if (host == null) return null;
            var v = new EmptyStateView(title, description, actionText);
            if (onAction != null) v.ActionClicked += onAction;
            host.Controls.Add(v);
            try { v.BringToFront(); } catch { }
            return v;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampWidth();
            CenterBox();
        }

        /// <summary>
        /// 窄宿主自适配：说明文本的设计宽是 360（两行排版的最佳阅读宽），
        /// 但侧栏/三栏面板的可用宽常只有 280 左右——固定 360 会横向溢出
        /// （ui-tree-check 的 R-溢出 会红）。这里按宿主实际宽夹一次。
        /// </summary>
        private void ClampWidth()
        {
            if (_desc == null) return;
            int avail = Width - DpiScale.V(this, 20);
            if (avail <= 0) return;
            int design = DpiScale.V(this, 360);
            int want = Math.Max(DpiScale.V(this, 140), Math.Min(design, avail));
            if (_desc.Width == want) return;
            _desc.Width = want;
            // 宽度变了行数也变：按真实 GDI 行高重算高度（宽 140~360 之间最多 3 行）
            int line = FormFontPolicy.LineBox(FormFontPolicy.UiFont(-0.5f), null, 1.7f);
            int lines = Math.Max(1, (int)Math.Ceiling((double)(_desc.Text ?? "").Length * 14 / Math.Max(1, want)) );
            _desc.Height = line * Math.Min(3, Math.Max(2, lines));
            try { _box.PerformLayout(); } catch (Exception) { }
        }

        /// <summary>
        /// 高度自适应（CI 0.1.381 实证）：书签页底部空态位只有 94px，而「标题+两行说明+按钮」
        /// 实测 121px —— 硬居中只会把按钮顶到宿主外面（越界 + 点不到）。
        /// 空间不足就先舍说明（标题 + 主操作仍完整可读），空间恢复再挂回来。
        /// </summary>
        private void FitHeight()
        {
            if (_desc == null) return;
            try { _box.PerformLayout(); } catch (Exception) { }
            int avail = ClientSize.Height;
            if (avail <= 0) return;
            bool wantCollapse = _box.Height > avail;
            if (_desc.Visible == wantCollapse)   // 需要收起却还在显示 / 需要恢复却已收起
            {
                _desc.Visible = !wantCollapse;
                try { _box.PerformLayout(); } catch (Exception) { }
                // 恢复说明后仍然放不下 → 退回收起态（避免抖动）
                if (!wantCollapse && _box.Height > avail)
                {
                    _desc.Visible = false;
                    try { _box.PerformLayout(); } catch (Exception) { }
                }
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) CenterBox();
        }

        private void CenterBox()
        {
            if (_box == null) return;
            FitHeight();
            int w = _box.Width, h = _box.Height;
            if (w <= 0 || h <= 0) { _box.PerformLayout(); w = _box.Width; h = _box.Height; }
            _box.Left = Math.Max(0, (ClientSize.Width - w) / 2);
            _box.Top = Math.Max(0, (ClientSize.Height - h) / 2);
        }
    }
}
