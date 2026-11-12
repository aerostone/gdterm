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
        private readonly System.Windows.Forms.Label _title;
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

            // 标题用原生 Label 而不是 AntdUI.Label：AutoSize 用库内 sps 量，比 GDI 实测窄约 10%
            //（CI dump 实证：「还没有命令历史」需 100px，AutoSize 只给 89px），而且 AntdUI.Label
            // 不换行——UI 字号 14pt 时「选择左侧工具开始使用」需 202px，144px 栏里直接被裁。
            // 原生 Label 在 AutoSize=false 时按宽换行，窄栏/大字号两个方向都能兜住。
            _title = new System.Windows.Forms.Label
            {
                Text = title ?? string.Empty,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = GdtermColorTable.Foreground,
                Font = FormFontPolicy.UiFont(0.5f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, DpiScale.V(null, 6)),
                TextAlign = ContentAlignment.MiddleCenter
            };
            FitTitleWidth();
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
                _action = new AntdUI.Button {
                    Text = actionText,
                    Type = AntdUI.TTypeMini.Primary,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0)
                };
                FormFontPolicy.SizeActionButton(this, _action, 12, 4);
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

        /// <summary>
        /// 就地改主按钮文案（同一面板在“空”与“坏”下需要不同动作：打开终端 / 重试）。
        /// 只改文案不改可见性——宽度让位由 <see cref="EnsureActionFits"/> 统一管，两处都写会抖。
        /// </summary>
        public void SetAction(string text)
        {
            if (_action == null || string.IsNullOrEmpty(text)) return;
            if (_action.Text == text) return;
            _action.Text = text;
            _action.AccessibleName = text;
            // 文案变长会超出按钮宽（SizeActionButton 是按当时文案算的）——这里按新文案重量一次
            try { FormFontPolicy.SizeActionButton(this, _action, 12, 4); } catch (Exception) { }
        }

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
            SetEmpty(empty, title, description, false);
        }

        /// <summary>
        /// 同上，额外区分「空」与「坏」：isError=true 时标题取 Danger 语义色。
        /// 读取失败如果复用中性空态文案，用户会以为“本来就没有数据”——这是最常见的一类静默故障。
        /// </summary>
        public void SetEmpty(bool empty, string title, string description, bool isError)
        {
            if (title != null && _title.Text != title)
            {
                _title.Text = title;
                AccessibleName = title;
                FitTitleWidth();
            }
            var wantColor = isError ? GdtermColorTable.Danger : GdtermColorTable.Foreground;
            if (_title.ForeColor != wantColor) _title.ForeColor = wantColor;
            if (description != null && _desc != null && _desc.Text != description)
                _desc.Text = description;
            SetEmpty(empty);
            if (empty) CenterBox();
        }


        /// <summary>
        /// 按真实 GDI 度量定标题宽（AutoSize 给不准，见构造函数注释）。
        /// 高度用字体行盒，避免 10pt 中文被 17px 的默认高裁掉下缘。
        /// </summary>
        private void FitTitleWidth()
        {
            try
            {
                string text = _title.Text ?? string.Empty;
                int w = 0;
                foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    int m = TextRenderer.MeasureText(line, _title.Font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                    if (m > w) w = m;
                }
                int h = FormFontPolicy.LineBox(_title.Font, null, 1.45f);
                int want = Math.Max(DpiScale.V(null, 20), w + DpiScale.V(null, 4));
                // 宿主宽度够就单行，不够就换行（2 行封顶，再多说明文案该改了）
                int avail = AvailableWidth();
                int width = avail > 0 ? Math.Min(want, avail) : want;
                int lines = width > 0 ? (int)Math.Ceiling((double)want / width) : 1;
                _title.Size = new Size(width, h * Math.Min(2, Math.Max(1, lines)));
            }
            catch (Exception) { }
        }

        /// <summary>空间不足时舍掉主操作按钮；空间恢复再挂回来（不抖动：放不下就继续收着）。</summary>
        private void EnsureActionFits()
        {
            if (_action == null) return;
            if (_action.Visible)
            {
                if (_box.Height <= ClientSize.Height) { _actionSuppressed = false; return; }
                _action.Visible = false;
                _actionSuppressed = true;
                try { _box.PerformLayout(); } catch (Exception) { }
                return;
            }
            if (!_actionSuppressed) return;
            _action.Visible = true;                      // 试恢复
            try { _box.PerformLayout(); } catch (Exception) { }
            if (_box.Height > ClientSize.Height)
            {
                _action.Visible = false;
                try { _box.PerformLayout(); } catch (Exception) { }
            }
            else
            {
                _actionSuppressed = false;
            }
        }

        private bool _actionSuppressed;
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
        /// <summary>宿主内可用的内容宽（左 8 + 右 8 + 一点余量），未知时 -1。</summary>
        private int AvailableWidth()
        {
            if (Width <= DpiScale.V(this, 20)) return -1;
            return Width - DpiScale.V(this, 20);
        }

        private void ClampWidth()
        {
            // 标题也要夹：它是 AutoSize 面板里最宽的成员，不夹就会把 _box 一起顶宽
            //（toolbox f14 dump 实证：标题 156px 撑出 144px 栏 12px 越界）
            int availBox = AvailableWidth();
            if (availBox > 0)
            {
                _box.MaximumSize = new Size(availBox, 0);
                FitTitleWidth();
            }
            if (_desc == null) return;
            int avail = availBox;
            if (avail <= 0) return;
            int design = DpiScale.V(this, 360);
            // 窄宿主自适配的下限：地板本身也不能超过可用宽，否则 240px 侧栏里那一栏只有 130px 时
            // 固定 140 的地板反而造成横向溢出（toolbox-narrow dump 实证：Panel 140 > 父 130）。
            int floor = Math.Min(DpiScale.V(this, 140), avail);
            int want = Math.Max(floor, Math.Min(design, avail));
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
            // AntdUI.Tabs 隐藏页 0×0（CI 0.1.389 dump 实证）：空态盒是 AutoSize，宿主塌成
            // 0×0 后它仍保持理想尺寸画在 (0,0)——WinForms 子窗不裁剪父边界，会叠到 tab 头。
            // 零面积时收起盒体，宿主恢复尺寸后 OnResize 再展开。
            bool hasArea = ClientSize.Width > 0 && ClientSize.Height > 0;
            if (_box.Visible != hasArea) _box.Visible = hasArea;
            if (!hasArea) return;
            FitHeight();
            int w = _box.Width, h = _box.Height;
            if (w <= 0 || h <= 0) { _box.PerformLayout(); w = _box.Width; h = _box.Height; }
            // 连"标题 + 主操作"都塞不进这条窄带时逐级降级（书签页 SplitContainer 被拖到 53px，
            // 空态位只剩 32px：61px 的盒体会越界画到父容器外，ui-tree-check R-溢出 实测红）。
            // 顺序：先舍按钮（标题仍能说明"这里为什么是空的"）→ 再舍整个盒体（空白胜于越界）。
            EnsureActionFits();
            w = _box.Width; h = _box.Height;
            if (_box.Height > ClientSize.Height)
            {
                _box.Visible = false;
                return;
            }
            _box.Left = Math.Max(0, (ClientSize.Width - w) / 2);
            _box.Top = Math.Max(0, (ClientSize.Height - h) / 2);
        }
    }
}
