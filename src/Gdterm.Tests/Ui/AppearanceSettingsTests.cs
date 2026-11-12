using System;
using System.IO;
using System.Text;
using Gdterm.UI.Forms;

namespace Gdterm.Tests.Ui
{
    /// <summary>
    /// appearance.ini 的往返。这张表是"整文件重写"模型：任何一处只带着自己那几个字段去
    /// Save()，就会静默清掉别人的键（QuickBarGroup/PinTmux 已经踩过一次）。视图状态
    /// （窗口几何/侧板宽度）加进来后键更多了，所以必须有往返测试钉住"存进去 = 读出来"。
    /// </summary>
    public static class AppearanceSettingsTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] AppearanceSettings");

            string dir = Path.Combine(Path.GetTempPath(), "gdterm-appearance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                RoundTrip(dir);
                BadValuesRejected(dir);
                MissingFileYieldsDefaults(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void RoundTrip(string dir)
        {
            string path = Path.Combine(dir, "appearance.ini");
            var s = new AppearanceSettings
            {
                FontName = "Consolas",
                FontSize = 13,
                CjkFontName = "Microsoft YaHei",
                ColorScheme = "Classic",
                UiTheme = "Dark",
                DpiAware = true,
                UIFontName = "Microsoft YaHei UI",
                UIFontSize = 10,
                QuickBarGroup = "运维",
                PinTmux = true,
                Language = "en",
                WindowOpacity = 88,
                WindowX = 120,
                WindowY = 60,
                WindowWidth = 1450,
                WindowHeight = 900,
                WindowMaximized = true,
                SidePanelWidth = 420
            };
            s.Save(path);

            var back = AppearanceSettings.Load(path);
            Assert.Equal("Consolas", back.FontName, "fontName");
            Assert.Equal(13, back.FontSize, "fontSize");
            Assert.Equal("Microsoft YaHei", back.CjkFontName, "cjkFontName");
            Assert.Equal("Dark", back.UiTheme, "uiTheme");
            Assert.True(back.DpiAware, "dpiAware");
            Assert.Equal(10, back.UIFontSize, "uiFontSize");
            Assert.Equal("运维", back.QuickBarGroup, "quickBarGroup (中文值不能被编码搞坏)");
            Assert.True(back.PinTmux, "pinTmux");
            Assert.Equal("en", back.Language, "language");
            Assert.Equal(88, back.WindowOpacity, "windowOpacity");
            // 视图状态：一条不落地回来（漏一个键 = 窗口每天都跳回默认位置）
            Assert.Equal(120, back.WindowX, "windowX");
            Assert.Equal(60, back.WindowY, "windowY");
            Assert.Equal(1450, back.WindowWidth, "windowWidth");
            Assert.Equal(900, back.WindowHeight, "windowHeight");
            Assert.True(back.WindowMaximized, "windowMax");
            Assert.Equal(420, back.SidePanelWidth, "sidePanelWidth");

            // 再存一次（等价于"用户在对话框里改了字号再保存"）：别的键不能丢
            back.FontSize = 14;
            back.Save(path);
            var again = AppearanceSettings.Load(path);
            Assert.Equal(14, again.FontSize, "fontSize updated");
            Assert.Equal(1450, again.WindowWidth, "window geometry survives a re-save");
            Assert.Equal(420, again.SidePanelWidth, "sidePanelWidth survives a re-save");
            Assert.Equal("运维", again.QuickBarGroup, "quickBarGroup survives a re-save");
        }

        private static void BadValuesRejected(string dir)
        {
            string path = Path.Combine(dir, "bad.ini");
            // 最小化时被记下来的"标题条"尺寸 + 越界宽度：读的时候就得挡掉，
            // 否则下次启动窗口只有 160×28 高，用户以为程序坏了
            File.WriteAllText(path,
                "[appearance]\r\n" +
                "windowWidth=160\r\n" +
                "windowHeight=28\r\n" +
                "sidePanelWidth=99999\r\n" +
                "windowOpacity=5\r\n" +
                "fontSize=99\r\n",
                new UTF8Encoding(false));
            var s = AppearanceSettings.Load(path);
            Assert.Equal(0, s.WindowWidth, "too-narrow windowWidth rejected");
            Assert.Equal(0, s.WindowHeight, "too-short windowHeight rejected");
            Assert.Equal(0, s.SidePanelWidth, "absurd sidePanelWidth rejected");
            Assert.Equal(100, s.WindowOpacity, "opacity out of range falls back to 100");
            Assert.Equal(12, s.FontSize, "fontSize out of range keeps default");
        }

        private static void MissingFileYieldsDefaults(string dir)
        {
            var s = AppearanceSettings.Load(Path.Combine(dir, "nope.ini"));
            Assert.Equal(0, s.WindowWidth, "no saved geometry -> 0 (MainForm 走默认尺寸)");
            Assert.Equal(int.MinValue, s.WindowX, "no saved X -> MinValue sentinel");
            Assert.True(!s.WindowMaximized, "not maximized by default");
            Assert.Equal(0, AppearanceSettings.Load(null).SidePanelWidth,
                "unset sidePanelWidth stays 0 (0 = 用设计值 360)");
        }
    }
}
