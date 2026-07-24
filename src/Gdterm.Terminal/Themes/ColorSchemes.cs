using System.Drawing;

namespace Gdterm.Terminal.Themes
{
    /// <summary>
    /// 终端配色方案——定义 16 色 ANSI 调色板 + 背景/前景
    /// </summary>
    public class TerminalColorScheme
    {
        /// <summary>
        /// 方案名称
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 方案描述
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 背景色
        /// </summary>
        public Color Background { get; set; }

        /// <summary>
        /// 默认前景色
        /// </summary>
        public Color Foreground { get; set; }

        /// <summary>
        /// ANSI 16 色（0-7 标准色，8-15 高亮色）
        /// </summary>
        public Color[] AnsiColors { get; set; }

        /// <summary>
        /// 光标颜色
        /// </summary>
        public Color CursorColor { get; set; }

        /// <summary>
        /// 选中文本背景色
        /// </summary>
        public Color SelectionBackground { get; set; }

        /// <summary>
        /// 选中文本前景色
        /// </summary>
        public Color SelectionForeground { get; set; }

        public TerminalColorScheme()
        {
            AnsiColors = new Color[16];
        }
    }

    /// <summary>
    /// 预定义配色方案集合
    /// </summary>
    public static class ColorSchemes
    {
        /// <summary>
        /// 经典暗色主题（默认）
        /// </summary>
        public static TerminalColorScheme Classic { get; } = new TerminalColorScheme
        {
            Name = "经典",
            Description = "默认暗色主题，适合日常使用",
            Background = Color.FromArgb(0, 0, 0),
            Foreground = Color.FromArgb(204, 204, 204),
            CursorColor = Color.FromArgb(204, 204, 204),
            SelectionBackground = Color.FromArgb(51, 153, 255),
            SelectionForeground = Color.White,
            AnsiColors = new Color[]
            {
                // 标准色 (0-7)
                Color.FromArgb(0, 0, 0),        // Black
                Color.FromArgb(205, 49, 49),     // Red
                Color.FromArgb(13, 188, 121),    // Green
                Color.FromArgb(229, 229, 16),    // Yellow
                Color.FromArgb(36, 114, 200),    // Blue
                Color.FromArgb(188, 63, 188),    // Magenta
                Color.FromArgb(17, 168, 205),    // Cyan
                Color.FromArgb(204, 204, 204),   // White
                // 高亮色 (8-15)
                Color.FromArgb(119, 119, 119),   // Bright Black (Gray)
                Color.FromArgb(241, 76, 76),     // Bright Red
                Color.FromArgb(35, 209, 139),    // Bright Green
                Color.FromArgb(245, 245, 67),    // Bright Yellow
                Color.FromArgb(59, 142, 234),    // Bright Blue
                Color.FromArgb(214, 112, 214),   // Bright Magenta
                Color.FromArgb(41, 184, 219),    // Bright Cyan
                Color.FromArgb(255, 255, 255),   // Bright White
            }
        };

        /// <summary>
        /// 高对比度主题——专为远程桌面、投影仪等低对比度场景设计
        /// </summary>
        public static TerminalColorScheme HighContrast { get; } = new TerminalColorScheme
        {
            Name = "高对比度",
            Description = "远程桌面/投影仪专用，最大亮度差异",
            Background = Color.FromArgb(0, 0, 0),
            Foreground = Color.FromArgb(255, 255, 255),
            CursorColor = Color.FromArgb(255, 255, 0),
            SelectionBackground = Color.FromArgb(0, 120, 215),
            SelectionForeground = Color.White,
            AnsiColors = new Color[]
            {
                // 标准色——高饱和度
                Color.FromArgb(0, 0, 0),        // Black
                Color.FromArgb(255, 0, 0),       // Red（纯红）
                Color.FromArgb(0, 255, 0),       // Green（纯绿）
                Color.FromArgb(255, 255, 0),     // Yellow（纯黄）
                Color.FromArgb(0, 120, 255),     // Blue（亮蓝）
                Color.FromArgb(255, 0, 255),     // Magenta（纯品红）
                Color.FromArgb(0, 255, 255),     // Cyan（纯青）
                Color.FromArgb(255, 255, 255),   // White（纯白）
                // 高亮色——最大亮度
                Color.FromArgb(180, 180, 180),   // Bright Black
                Color.FromArgb(255, 80, 80),     // Bright Red
                Color.FromArgb(80, 255, 80),     // Bright Green
                Color.FromArgb(255, 255, 80),    // Bright Yellow
                Color.FromArgb(80, 160, 255),    // Bright Blue
                Color.FromArgb(255, 100, 255),   // Bright Magenta
                Color.FromArgb(100, 255, 255),   // Bright Cyan
                Color.FromArgb(255, 255, 255),   // Bright White
            }
        };

        /// <summary>
        /// Solarized Dark 主题
        /// </summary>
        public static TerminalColorScheme SolarizedDark { get; } = new TerminalColorScheme
        {
            Name = "Solarized 暗色",
            Description = "低对比度护眼主题，长时间使用不疲劳",
            Background = Color.FromArgb(0, 43, 54),
            Foreground = Color.FromArgb(131, 148, 150),
            CursorColor = Color.FromArgb(131, 148, 150),
            SelectionBackground = Color.FromArgb(7, 54, 66),
            SelectionForeground = Color.FromArgb(131, 148, 150),
            AnsiColors = new Color[]
            {
                Color.FromArgb(7, 54, 66),       // base02
                Color.FromArgb(220, 50, 47),     // red
                Color.FromArgb(133, 153, 0),     // green
                Color.FromArgb(181, 137, 0),     // yellow
                Color.FromArgb(38, 139, 210),    // blue
                Color.FromArgb(211, 54, 130),    // magenta
                Color.FromArgb(42, 161, 152),    // cyan
                Color.FromArgb(203, 75, 22),     // orange (as white)
                Color.FromArgb(0, 43, 54),       // base03
                Color.FromArgb(203, 75, 22),     // orange (as bright black)
                Color.FromArgb(88, 110, 117),    // base01
                Color.FromArgb(101, 123, 131),   // base00
                Color.FromArgb(108, 113, 196),   // violet
                Color.FromArgb(147, 161, 161),   // base1
                Color.FromArgb(131, 148, 150),   // base0
                Color.FromArgb(253, 246, 227),   // base3
            }
        };

        /// <summary>
        /// Monokai 主题
        /// </summary>
        public static TerminalColorScheme Monokai { get; } = new TerminalColorScheme
        {
            Name = "Monokai",
            Description = "经典代码编辑器配色",
            Background = Color.FromArgb(39, 40, 34),
            Foreground = Color.FromArgb(248, 248, 242),
            CursorColor = Color.FromArgb(248, 248, 242),
            SelectionBackground = Color.FromArgb(73, 72, 62),
            SelectionForeground = Color.FromArgb(248, 248, 242),
            AnsiColors = new Color[]
            {
                Color.FromArgb(39, 40, 34),      // Black
                Color.FromArgb(249, 38, 114),    // Red
                Color.FromArgb(166, 226, 46),    // Green
                Color.FromArgb(230, 219, 116),   // Yellow
                Color.FromArgb(102, 217, 239),   // Blue
                Color.FromArgb(174, 129, 255),   // Magenta
                Color.FromArgb(117, 113, 94),    // Cyan (as comment)
                Color.FromArgb(248, 248, 242),   // White
                Color.FromArgb(117, 113, 94),    // Bright Black (comment)
                Color.FromArgb(249, 38, 114),    // Bright Red
                Color.FromArgb(166, 226, 46),    // Bright Green
                Color.FromArgb(230, 219, 116),   // Bright Yellow
                Color.FromArgb(102, 217, 239),   // Bright Blue
                Color.FromArgb(174, 129, 255),   // Bright Magenta
                Color.FromArgb(166, 226, 46),    // Bright Cyan
                Color.FromArgb(249, 248, 244),   // Bright White
            }
        };

        /// <summary>
        /// Dracula 主题
        /// </summary>
        public static TerminalColorScheme Dracula { get; } = new TerminalColorScheme
        {
            Name = "Dracula",
            Description = "流行的暗紫色主题",
            Background = Color.FromArgb(40, 42, 54),
            Foreground = Color.FromArgb(248, 248, 242),
            CursorColor = Color.FromArgb(68, 71, 90),
            SelectionBackground = Color.FromArgb(68, 71, 90),
            SelectionForeground = Color.FromArgb(248, 248, 242),
            AnsiColors = new Color[]
            {
                Color.FromArgb(0, 0, 0),         // Black
                Color.FromArgb(255, 85, 85),     // Red
                Color.FromArgb(80, 250, 123),    // Green
                Color.FromArgb(241, 250, 140),   // Yellow
                Color.FromArgb(98, 114, 164),    // Blue (comment)
                Color.FromArgb(255, 121, 198),   // Magenta
                Color.FromArgb(139, 233, 253),   // Cyan
                Color.FromArgb(183, 183, 183),   // White
                Color.FromArgb(68, 71, 90),      // Bright Black (selection)
                Color.FromArgb(255, 85, 85),     // Bright Red
                Color.FromArgb(80, 250, 123),    // Bright Green
                Color.FromArgb(241, 250, 140),   // Bright Yellow
                Color.FromArgb(98, 114, 164),    // Bright Blue
                Color.FromArgb(255, 121, 198),   // Bright Magenta
                Color.FromArgb(139, 233, 253),   // Bright Cyan
                Color.FromArgb(248, 248, 242),   // Bright White
            }
        };

        /// <summary>
        /// 绿色终端主题——复古风
        /// </summary>
        public static TerminalColorScheme GreenTerminal { get; } = new TerminalColorScheme
        {
            Name = "绿色终端",
            Description = "复古绿色终端风格",
            Background = Color.FromArgb(0, 0, 0),
            Foreground = Color.FromArgb(0, 255, 0),
            CursorColor = Color.FromArgb(0, 255, 0),
            SelectionBackground = Color.FromArgb(0, 100, 0),
            SelectionForeground = Color.FromArgb(0, 255, 0),
            AnsiColors = new Color[]
            {
                Color.FromArgb(0, 0, 0),         // Black
                Color.FromArgb(0, 180, 0),       // Red (暗绿)
                Color.FromArgb(0, 255, 0),       // Green
                Color.FromArgb(0, 220, 0),       // Yellow (中绿)
                Color.FromArgb(0, 140, 0),       // Blue (深绿)
                Color.FromArgb(0, 200, 0),       // Magenta (亮绿)
                Color.FromArgb(0, 240, 0),       // Cyan (青绿)
                Color.FromArgb(0, 255, 0),       // White (纯绿)
                Color.FromArgb(0, 100, 0),       // Bright Black (灰绿)
                Color.FromArgb(0, 200, 0),       // Bright Red
                Color.FromArgb(0, 255, 0),       // Bright Green
                Color.FromArgb(0, 240, 0),       // Bright Yellow
                Color.FromArgb(0, 160, 0),       // Bright Blue
                Color.FromArgb(0, 220, 0),       // Bright Magenta
                Color.FromArgb(0, 255, 128),     // Bright Cyan
                Color.FromArgb(128, 255, 128),   // Bright White
            }
        };

        /// <summary>
        /// 白色主题——适合强光环境
        /// </summary>
        public static TerminalColorScheme Light { get; } = new TerminalColorScheme
        {
            Name = "白色",
            Description = "浅色背景，适合强光环境",
            Background = Color.FromArgb(255, 255, 255),
            Foreground = Color.FromArgb(0, 0, 0),
            CursorColor = Color.FromArgb(0, 0, 0),
            SelectionBackground = Color.FromArgb(51, 153, 255),
            SelectionForeground = Color.White,
            AnsiColors = new Color[]
            {
                Color.FromArgb(0, 0, 0),         // Black
                Color.FromArgb(205, 49, 49),     // Red
                Color.FromArgb(0, 150, 0),       // Green
                Color.FromArgb(150, 120, 0),     // Yellow
                Color.FromArgb(36, 114, 200),    // Blue
                Color.FromArgb(150, 50, 150),    // Magenta
                Color.FromArgb(0, 150, 150),     // Cyan
                Color.FromArgb(100, 100, 100),   // White (灰)
                Color.FromArgb(80, 80, 80),      // Bright Black
                Color.FromArgb(230, 80, 80),     // Bright Red
                Color.FromArgb(0, 180, 0),       // Bright Green
                Color.FromArgb(180, 150, 0),     // Bright Yellow
                Color.FromArgb(59, 142, 234),    // Bright Blue
                Color.FromArgb(180, 80, 180),    // Bright Magenta
                Color.FromArgb(0, 180, 180),     // Bright Cyan
                Color.FromArgb(0, 0, 0),         // Bright White (黑)
            }
        };

        /// <summary>
        /// 获取所有预定义方案
        /// </summary>
        public static TerminalColorScheme[] GetAll()
        {
            return new[]
            {
                Classic,
                HighContrast,
                SolarizedDark,
                Monokai,
                Dracula,
                GreenTerminal,
                Light
            };
        }

        /// <summary>
        /// 根据名称获取方案
        /// </summary>
        public static TerminalColorScheme GetByName(string name)
        {
            foreach (var scheme in GetAll())
            {
                if (scheme.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    return scheme;
            }
            return Classic; // 默认返回经典主题
        }
    }
}
