using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 菜单图标工厂——16x16 纯 GDI+ 手绘单色图标，与深色主题同色系。
    /// 不引入任何图片资源或图标字体依赖（Win7 没有 Segoe MDL2 Assets），
    /// 与 ConnectionTreeControl.DrawIcon 同一思路；按 key 绘制并缓存，
    /// 进程生命周期内共享同一份位图。
    /// </summary>
    internal static class MenuIconFactory
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, Bitmap> _cache;

        /// <summary>取图标；未知 key 或绘制异常返回 null（菜单项自动回退为无图标）。</summary>
        public static Bitmap Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (Gate)
            {
                if (_cache == null) _cache = new Dictionary<string, Bitmap>();
                Bitmap bmp;
                if (_cache.TryGetValue(key, out bmp)) return bmp;
                bmp = Render(key);
                _cache[key] = bmp;
                return bmp;
            }
        }

        private static readonly Color Ink = Color.FromArgb(205, 205, 210);
        private const float W = 1.6f;

        private static Bitmap Render(string key)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Ink, W) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                using (var fill = new SolidBrush(Ink))
                {
                    try { Draw(g, pen, fill, key); } catch { }
                }
            }
            return bmp;
        }

        private static void L(Graphics g, Pen p, double x1, double y1, double x2, double y2)
        {
            g.DrawLine(p, (float)x1, (float)y1, (float)x2, (float)y2);
        }

        private static void Rect(Graphics g, Pen p, double x, double y, double w2, double h2)
        {
            g.DrawRectangle(p, (float)x, (float)y, (float)w2, (float)h2);
        }

        private static void ArrowDown(Graphics g, Pen p, double cx, double tipY, double tailY)
        {
            L(g, p, cx, tailY, cx, tipY);
            L(g, p, cx - 2.5, tipY - 2.5, cx, tipY);
            L(g, p, cx + 2.5, tipY - 2.5, cx, tipY);
        }

        private static void Draw(Graphics g, Pen p, Brush fill, string key)
        {
            switch (key)
            {
                case "new": // 新建连接：加号
                    L(g, p, 8, 3, 8, 13); L(g, p, 3, 8, 13, 8);
                    break;

                case "quickjump": // 快速跳转：闪电
                    var bolt = new[]
                    {
                        new PointF(9.5f, 1.5f), new PointF(4.5f, 9f), new PointF(8f, 9f),
                        new PointF(6.5f, 14.5f), new PointF(11.5f, 7f), new PointF(8f, 7f)
                    };
                    g.DrawPolygon(p, bolt);
                    break;

                case "terminal": // 本地终端：提示符框 >_
                    Rect(g, p, 1.5, 3, 13, 10);
                    L(g, p, 4, 6, 6, 8); L(g, p, 6, 8, 4, 10); L(g, p, 7.5, 10.5, 10.5, 10.5);
                    break;

                case "folder": // SFTP / 分组：文件夹
                    g.DrawLines(p, new[]
                    {
                        new PointF(1.5f, 13f), new PointF(1.5f, 4f), new PointF(6f, 4f),
                        new PointF(7.5f, 5.5f), new PointF(14.5f, 5.5f), new PointF(14.5f, 13f)
                    });
                    L(g, p, 1.5, 13, 14.5, 13);
                    break;

                case "import": // 导入：入托盘箭头
                    ArrowDown(g, p, 8, 9.5, 2.5);
                    g.DrawLines(p, new[] { new PointF(3f, 11f), new PointF(3f, 13.5f), new PointF(13f, 13.5f), new PointF(13f, 11f) });
                    break;

                case "export": // 导出：出托盘箭头
                    ArrowDown(g, p, 8, 2.5, 9.5);
                    g.DrawLines(p, new[] { new PointF(3f, 11f), new PointF(3f, 13.5f), new PointF(13f, 13.5f), new PointF(13f, 11f) });
                    break;

                case "exit": // 退出：电源符号
                    g.DrawArc(p, 4f, 5f, 8f, 8f, -75f, 330f);
                    L(g, p, 8, 2, 8, 8.5);
                    break;

                case "reconnect": // 重连：循环箭头
                    g.DrawArc(p, 3.5f, 3.5f, 9f, 9f, 30f, 280f);
                    g.FillPolygon(fill, new[] { new PointF(13.5f, 4f), new PointF(10.5f, 4.5f), new PointF(13f, 7f) });
                    break;

                case "close": // 关闭：X
                    L(g, p, 4, 4, 12, 12); L(g, p, 12, 4, 4, 12);
                    break;

                case "bookmark": // 书签：五角星
                    g.DrawPolygon(p, StarPoints(8, 8, 6, 2.6));
                    break;

                case "splith": // 水平分割：上下双栏
                    Rect(g, p, 2.5, 2.5, 11, 11);
                    L(g, p, 2.5, 8, 13.5, 8);
                    break;

                case "splitv": // 垂直分割：左右双栏
                    Rect(g, p, 2.5, 2.5, 11, 11);
                    L(g, p, 8, 2.5, 8, 13.5);
                    break;

                case "search": // 查找：放大镜
                    g.DrawEllipse(p, 3f, 3f, 7f, 7f);
                    L(g, p, 9.5, 9.5, 13, 13);
                    break;

                case "snippet": // 片段搜索：文档行
                    Rect(g, p, 3.5, 2, 9, 12);
                    L(g, p, 5.5, 5, 10.5, 5); L(g, p, 5.5, 7.5, 10.5, 7.5); L(g, p, 5.5, 10, 9, 10);
                    break;

                case "highlight": // 高亮规则：荧光笔斜线
                    using (var thick = new Pen(Ink, 3.4f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                        L(g, thick, 4, 12, 10.5, 5.5);
                    L(g, p, 11, 3.5, 13, 5.5);
                    L(g, p, 3, 14, 7.5, 14);
                    break;

                case "script": // 登录脚本：脚本页 >
                    Rect(g, p, 3, 2, 10, 12);
                    L(g, p, 5.5, 5.5, 7.5, 7.5); L(g, p, 7.5, 7.5, 5.5, 9.5); L(g, p, 9, 10, 11, 10);
                    break;

                case "broadcast": // 多通道广播：中心点+电波
                    g.FillEllipse(fill, 7f, 7f, 2.2f, 2.2f);
                    g.DrawArc(p, 4f, 4f, 8f, 8f, -55f, 110f);
                    g.DrawArc(p, 1.5f, 1.5f, 13f, 13f, -55f, 110f);
                    break;

                case "batch": // 批量命令：清单
                    L(g, p, 3, 4.5, 13, 4.5); L(g, p, 3, 8, 13, 8); L(g, p, 3, 11.5, 10, 11.5);
                    break;

                case "history": // 命令历史：时钟
                    g.DrawEllipse(p, 3f, 3f, 10f, 10f);
                    L(g, p, 8, 5, 8, 8.5); L(g, p, 8, 8.5, 10.8, 10.3);
                    break;

                case "health": // 健康监控：心形
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(2.5f, 5.5f, 2.5f, 2f, 7f, 2f, 8f, 5.5f);
                        path.AddBezier(8f, 5.5f, 9f, 2f, 13.5f, 2f, 13.5f, 5.5f);
                        path.AddBezier(13.5f, 5.5f, 13.5f, 9f, 10f, 11.5f, 8f, 13.5f);
                        path.AddBezier(8f, 13.5f, 6f, 11.5f, 2.5f, 9f, 2.5f, 5.5f);
                        g.DrawPath(p, path);
                    }
                    break;

                case "forward": // 端口转发：双向箭头
                    L(g, p, 2.5, 5.5, 13, 5.5); L(g, p, 10.5, 3, 13.5, 5.5); L(g, p, 10.5, 8, 13.5, 5.5);
                    L(g, p, 13.5, 10.5, 3, 10.5); L(g, p, 5.5, 8, 2.5, 10.5); L(g, p, 5.5, 13, 2.5, 10.5);
                    break;

                case "toolbox": // 运维工具箱：扳手
                    g.DrawArc(p, 8f, 2f, 6f, 6f, -40f, 200f);
                    L(g, p, 8.5, 7, 3, 12.5);
                    g.DrawLines(p, new[] { new PointF(3f, 12.5f), new PointF(2f, 13.5f), new PointF(3.5f, 14f), new PointF(4.5f, 13f) });
                    break;

                case "scaneye": // 敏感信息扫描：眼睛
                    g.DrawArc(p, 2f, 2f, 12f, 11.5f, 195f, 150f);
                    g.DrawArc(p, 2f, 2.5f, 12f, 11.5f, 15f, 150f);
                    g.FillEllipse(fill, 6.8f, 6.8f, 2.6f, 2.6f);
                    break;

                case "transfer": // 传输中心：上下对流箭头
                    ArrowDown(g, p, 5.5, 11, 3);
                    L(g, p, 10.5, 11.5, 10.5, 4); L(g, p, 8, 6.5, 10.5, 4); L(g, p, 13, 6.5, 10.5, 4);
                    break;

                case "notify": // 通知中心：铃铛
                    g.DrawArc(p, 4f, 2.5f, 8f, 9f, 180f, 180f);
                    L(g, p, 4, 7, 4, 11); L(g, p, 12, 7, 12, 11);
                    L(g, p, 4, 11, 12, 11); L(g, p, 6.5, 12.5, 9.5, 12.5);
                    break;

                case "lock": // 密码库管理：锁
                    Rect(g, p, 4, 7.5, 8, 6);
                    g.DrawArc(p, 5.5f, 3f, 5f, 6f, 180f, 180f);
                    break;

                case "shield": // 密码健康报告：盾牌
                    g.DrawLines(p, new[]
                    {
                        new PointF(8f, 1.8f), new PointF(13.2f, 3.8f), new PointF(13.2f, 8f),
                        new PointF(8f, 14.2f), new PointF(2.8f, 8f), new PointF(2.8f, 3.8f), new PointF(8f, 1.8f)
                    });
                    break;

                case "key": // 密码生成器 / SSH 密钥：钥匙
                    g.DrawEllipse(p, 2.5f, 2.5f, 5.5f, 5.5f);
                    L(g, p, 7.2, 7.2, 13, 13);
                    L(g, p, 10.5, 10.5, 12, 9);
                    break;

                case "pencil": // 修改主密码 / 编辑：铅笔
                    L(g, p, 4.5, 11.5, 11.5, 4.5);
                    L(g, p, 11.5, 4.5, 12.8, 3.2); L(g, p, 12.8, 3.2, 13.8, 4.2); L(g, p, 13.8, 4.2, 12.5, 5.5);
                    L(g, p, 4.5, 11.5, 3, 13.5);
                    break;

                case "brush": // 外观设置：画笔
                    L(g, p, 10.5, 2.5, 13, 5);
                    g.DrawLines(p, new[] { new PointF(10.5f, 2.5f), new PointF(6f, 7f), new PointF(8.5f, 9.5f), new PointF(13f, 5f) });
                    g.FillPolygon(fill, new[] { new PointF(6f, 7f), new PointF(2.5f, 11f), new PointF(4.5f, 13f), new PointF(8.5f, 9.5f) });
                    break;

                case "ai": // AI 助手：灯泡
                    g.DrawEllipse(p, 5f, 1.8f, 6f, 6f);
                    L(g, p, 6.2, 10.5, 9.8, 10.5);
                    L(g, p, 6.8, 12.5, 9.2, 12.5);
                    L(g, p, 6.5, 8, 6.5, 10.5); L(g, p, 9.5, 8, 9.5, 10.5);
                    break;

                case "warning": // 危险命令规则：警告三角
                    g.DrawPolygon(p, new[] { new PointF(8f, 2f), new PointF(14.2f, 13f), new PointF(1.8f, 13f) });
                    L(g, p, 8, 6, 8, 9.5);
                    g.FillEllipse(fill, 7.5f, 10.8f, 1.4f, 1.4f);
                    break;

                case "keyboard": // 快捷键绑定：键盘
                    Rect(g, p, 1.5, 4, 13, 8);
                    L(g, p, 4, 6.5, 4.2, 6.5); L(g, p, 6.5, 6.5, 6.7, 6.5); L(g, p, 9, 6.5, 9.2, 6.5); L(g, p, 11.5, 6.5, 11.7, 6.5);
                    L(g, p, 5, 9.5, 11, 9.5);
                    break;

                case "helpq": // 快捷键列表：问号圈
                    g.DrawEllipse(p, 2.5f, 2.5f, 11f, 11f);
                    g.DrawArc(p, 5.8f, 4.2f, 4.4f, 4.4f, 170f, 260f);
                    L(g, p, 8, 8.6, 8, 9.8);
                    g.FillEllipse(fill, 7.5f, 10.9f, 1.4f, 1.4f);
                    break;

                case "logs": // 打开日志文件夹
                    g.DrawLines(p, new[]
                    {
                        new PointF(1.5f, 13f), new PointF(1.5f, 4f), new PointF(6f, 4f),
                        new PointF(7.5f, 5.5f), new PointF(12f, 5.5f), new PointF(14.5f, 13f)
                    });
                    g.DrawLines(p, new[] { new PointF(3f, 7f), new PointF(15f, 7f), new PointF(13f, 13f), new PointF(1.5f, 13f) });
                    break;

                case "info": // 关于
                    g.DrawEllipse(p, 2.5f, 2.5f, 11f, 11f);
                    g.FillEllipse(fill, 7.4f, 4.4f, 1.6f, 1.6f);
                    L(g, p, 8, 7.2, 8, 11);
                    break;

                case "globe": // 连接：地球
                    g.DrawEllipse(p, 2.5f, 2.5f, 11f, 11f);
                    g.DrawEllipse(p, 5.5f, 2.5f, 5f, 11f);
                    L(g, p, 3, 8, 13, 8);
                    break;

                case "copy": // 复制主机地址：双层矩形
                    Rect(g, p, 5.5, 5.5, 8, 8);
                    g.DrawLines(p, new[]
                    {
                        new PointF(10.5f, 3.5f), new PointF(2.5f, 3.5f), new PointF(2.5f, 11.5f)
                    });
                    break;

                default:
                    break;
            }
        }

        private static PointF[] StarPoints(double cx, double cy, double outer, double inner)
        {
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? outer : inner;
                double ang = -Math.PI / 2 + i * Math.PI / 5;
                pts[i] = new PointF((float)(cx + r * Math.Cos(ang)), (float)(cy + r * Math.Sin(ang)));
            }
            return pts;
        }
    }
}
