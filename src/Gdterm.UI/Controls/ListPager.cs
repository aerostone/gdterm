using System;
using System.Windows.Forms;
using Gdterm.UI.Diagnostics;
using Gdterm.UI.Services;
using GdtermColorTable = Gdterm.UI.Diagnostics.GdtermColorTable;

namespace Gdterm.UI.Controls
{
    /// <summary>
    /// 分页条（ListShell / TableShell 共用底座，FEATURE-MAP §4「分页/虚拟滚动」加分项）。
    ///
    /// 机制：AntdUI.Pagination 只负责页码 UI，不自动切数据（AntdUI 2.4.8 实证：
    /// Total 设总数、PageTotal 自动算、ValueChanged(object, PagePageEventArgs)）。
    /// 宿主在 <see cref="PageChanged"/> 里重切片（壳的 Apply()），本页条只管：
    /// ① 总条数→页数换算与页码钳位 ② 单页时整条隐藏（短列表观感不变）
    /// ③ 宿主 0×0（AntdUI.Tabs 未激活页）时同步隐藏，防子控件画到宿主左上角
    ///    （与工具条同机制，CI 0.1.389 dump 实证的越界泄漏）。
    /// 数据量有界（几百~几千条）→ 真分页足够，不做虚拟滚动（v0.2 裁决口径）。
    /// </summary>
    internal sealed class ListPager : Panel
    {
        private readonly AntdUI.Pagination _pager;
        private bool _syncing;   // 程序写 Current/Total 时的回环抑制
        private bool _on;        // 页数 > 1（是否该显示分页条）
        private bool _hasArea;   // 宿主有真实面积（0×0 隐藏页不算）

        /// <summary>每页条数。短列表（≤1 页）不显示分页条，50 是桌面行高的合理密度。</summary>
        public int PageSize { get; set; }

        /// <summary>当前页（1 起）。宿主切片用。</summary>
        public int CurrentPage { get; private set; }

        /// <summary>用户翻页（页码钳位后 ≠ 当前页才触发）。</summary>
        public event Action<int> PageChanged;

        public ListPager()
        {
            Dock = DockStyle.Bottom;
            Height = Math.Max(DpiScale.V(this, 36), FormFontPolicy.LineBox(FormFontPolicy.UiFont(), this));
            BackColor = GdtermColorTable.Background;
            Padding = new Padding(DpiScale.V(this, 8), 0, DpiScale.V(this, 8), DpiScale.V(this, 4));

            _pager = new AntdUI.Pagination
            {
                Name = "ListPagerControl",
                Dock = DockStyle.Fill,
                PageSize = 50,
                ShowSizeChanger = false,   // 数据量有界，页大小固定即可
                MaxPageTotal = 7,          // 页按钮上限（超出省略号），窄面板里不炸宽
                Gap = DpiScale.V(this, 8)
            };
            _pager.ValueChanged += OnPagerValueChanged;
            Controls.Add(_pager);

            CurrentPage = 1;
            PageSize = 50;
            Visible = false;
        }

        private void OnPagerValueChanged(object sender, AntdUI.PagePageEventArgs e)
        {
            if (_syncing) return;
            int page = Math.Max(1, e.Current);
            if (page == CurrentPage) return;
            CurrentPage = page;
            var h = PageChanged;
            if (h != null) h(page);
        }

        /// <summary>
        /// 同步总条数（过滤/排序后的可见总量）。钳位页码、切换显示、同步页码 UI。
        /// 返回总页数（宿主切片用）。
        /// </summary>
        public int SetTotal(int total)
        {
            int pages = Math.Max(1, (total + PageSize - 1) / PageSize);
            if (CurrentPage > pages) CurrentPage = pages;
            if (CurrentPage < 1) CurrentPage = 1;

            _syncing = true;
            try
            {
                _pager.PageSize = PageSize;
                _pager.Total = total;
                _pager.Current = CurrentPage;
            }
            finally { _syncing = false; }

            bool on = pages > 1;
            if (_on != on) _on = on;
            ApplyVisibility();
            return pages;
        }

        /// <summary>回到第 1 页（过滤条件变化时调用：新结果集从第 1 页看起）。</summary>
        public void Reset()
        {
            if (CurrentPage == 1) return;
            CurrentPage = 1;
            _syncing = true;
            try { _pager.Current = 1; }
            finally { _syncing = false; }
        }

        /// <summary>宿主面积变化（0×0 隐藏页机制）。宿主在 OnClientSizeChanged 里调。</summary>
        public void SyncArea(bool hasArea)
        {
            if (_hasArea == hasArea) return;
            _hasArea = hasArea;
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            bool show = _on && _hasArea;
            if (Visible != show) Visible = show;
        }
    }
}
