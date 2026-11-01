using System;
using System.Collections.Generic;
using System.Drawing;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 自绘命中区（审计 C5 命中区 + C7-③ dump 可观测）：
    /// 自绘控件的交互区域不是 Control 节点，UiTreeDumper 的 R1 触击目标检查看不到它们
    /// （历史实证：标签关闭钮 16×18 画在 TabPage 上，R1 报"全部达标"但用户点不准）。
    /// 暴露自绘交互区的控件实现本接口，dump 会收集为 <c>hitRegions</c> 字段，
    /// ui-tree-check 按"最小目标 24×24 逻辑 px"（WCAG 2.2 SC 2.5.8）判尺寸。
    /// </summary>
    public interface IHitRegionProvider
    {
        /// <summary>
        /// 当前交互命中区（屏幕坐标）。无自绘交互区或控件不可见时返回空列表。
        /// 实现必须无副作用、可重复调用（dump 与命中测试各调一次）。
        /// </summary>
        List<HitRegion> GetHitRegions();
    }

    /// <summary>一个自绘交互命中区。</summary>
    public struct HitRegion
    {
        /// <summary>人话标签（dump 里定位用），如 <c>tab-close:ssh-01</c>。</summary>
        public string Label;

        /// <summary>命中区（屏幕坐标，含 DPI 缩放）。</summary>
        public Rectangle Bounds;

        public HitRegion(string label, Rectangle bounds)
        {
            Label = label;
            Bounds = bounds;
        }
    }
}
