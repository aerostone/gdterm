using System;
using System.Drawing;
using System.Windows.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// 主窗体菜单构建器。
    ///
    /// 菜单组织约定（2024-08 与用户对齐）：
    ///   文件   = 打开类动作 + 导入导出数据 + 退出
    ///   连接   = 当前会话操作（重连/关闭）+ 连接资产（书签/最近）
    ///   视图   = 外观与布局
    ///   终端   = 终端内搜索/会话功能/监控
    ///   工具   = 独立工具 + 安全（KeePass 集群）+ 设置集群
    ///   帮助   = 快捷键/日志/关于
    ///
    /// 快捷键约定：UI 动作一律 Ctrl+Shift+字母（Windows Terminal / VS Code 惯例），
    /// 普通 Ctrl 组合属于 shell readline（Ctrl+R 反向搜索 / Ctrl+W 删词…），不能被菜单抢走。
    /// 图标由 MenuIconFactory 纯 GDI+ 手绘，主题同色系、零资源依赖。
    /// </summary>
    public class MainFormMenuBuilder
    {
        public sealed class Callbacks
        {
            public EventHandler NewConnection { get; set; }
            public EventHandler ImportConnections { get; set; }
            public EventHandler ExportConnections { get; set; }
            public EventHandler Exit { get; set; }
            public EventHandler OpenLocalTerminal { get; set; }
            public EventHandler OpenSftp { get; set; }
            public EventHandler ReconnectActive { get; set; }
            public EventHandler CloseActive { get; set; }
            public EventHandler ViewStandard { get; set; }
            public EventHandler ViewFocus { get; set; }
            public EventHandler ViewCompact { get; set; }
            public EventHandler ToggleTree { get; set; }
            public EventHandler ToggleTreePin { get; set; }
            public EventHandler SplitHorizontal { get; set; }
            public EventHandler SplitVertical { get; set; }
            public EventHandler ToggleQuickBar { get; set; }
            public EventHandler ShowSearch { get; set; }
            public EventHandler ShowSnippet { get; set; }
            public EventHandler ShowHighlight { get; set; }
            public EventHandler ShowKeyBinding { get; set; }
            public EventHandler ShowLogonScript { get; set; }
            public EventHandler ShowMultiChannel { get; set; }
            public EventHandler ShowBatch { get; set; }
            public EventHandler ShowHistory { get; set; }
            public EventHandler ShowHealth { get; set; }
            public EventHandler ShowPortForward { get; set; }
            public EventHandler ShowToolbox { get; set; }
            public EventHandler ShowSecretScan { get; set; }
            public EventHandler ShowScannerCenter { get; set; }
            public EventHandler ShowBookmarks { get; set; }
            public EventHandler KeePassManager { get; set; }
            public EventHandler PasswordHealth { get; set; }
            public EventHandler PasswordGenerator { get; set; }
            public EventHandler ChangeMasterPassword { get; set; }
            public EventHandler AiSettings { get; set; }
            public EventHandler DangerousCmdSettings { get; set; }
            public EventHandler ShowHotkeys { get; set; }
            public EventHandler About { get; set; }
            public EventHandler AppearanceSettings { get; set; }
            public EventHandler SshKeyManager { get; set; }
            public EventHandler ShowTransferCenter { get; set; }
            public EventHandler ShowNotificationCenter { get; set; }
            public EventHandler QuickJump { get; set; }
            public EventHandler ShowLogsFolder { get; set; }
        }

        public sealed class Result
        {
            public MenuStrip Menu { get; set; }
            public ToolStripMenuItem ViewStandardItem { get; set; }
            public ToolStripMenuItem ViewFocusItem { get; set; }
            public ToolStripMenuItem ViewCompactItem { get; set; }
        }

        public Result Build(Callbacks cb)
        {
            if (cb == null) throw new ArgumentNullException("cb");

            var menu = new MenuStrip();

            // 带图标条目的统一入口；图标缺失时自动回退纯文本
            ToolStripMenuItem Mk(string text, EventHandler handler, string icon)
            {
                var it = new ToolStripMenuItem(text);
                if (handler != null) it.Click += handler;
                if (icon != null)
                {
                    try
                    {
                        var img = MenuIconFactory.Get(icon);
                        if (img != null) it.Image = img;
                    }
                    catch { }
                }
                return it;
            }

            // ===== 文件：打开类动作 + 数据 + 退出 =====
            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add(Mk("新建连接(&N)...", cb.NewConnection, "new"));
            fileMenu.DropDownItems.Add(Mk("快速跳转连接 Ctrl+Shift+K", cb.QuickJump, "quickjump"));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(Mk("本地终端(&L)", cb.OpenLocalTerminal, "terminal"));
            fileMenu.DropDownItems.Add(Mk("SFTP 浏览器(&S)", cb.OpenSftp, "folder"));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(Mk("导入连接(&I)...", cb.ImportConnections, "import"));
            fileMenu.DropDownItems.Add(Mk("导出连接(&E)...", cb.ExportConnections, "export"));
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(Mk("退出(&X)", cb.Exit, "exit"));
            menu.Items.Add(fileMenu);

            // ===== 连接：当前会话操作 + 连接资产 =====
            var connectionMenu = new ToolStripMenuItem("连接(&C)");
            connectionMenu.DropDownItems.Add(Mk("重连当前标签 Ctrl+Shift+R", cb.ReconnectActive, "reconnect"));
            connectionMenu.DropDownItems.Add(Mk("关闭当前标签 Ctrl+Shift+W", cb.CloseActive, "close"));
            connectionMenu.DropDownItems.Add(new ToolStripSeparator());
            connectionMenu.DropDownItems.Add(Mk("书签 / 最近连接(&B)", cb.ShowBookmarks, "bookmark"));
            menu.Items.Add(connectionMenu);

            // ===== 视图：外观与布局 =====
            var viewMenu = new ToolStripMenuItem("视图(&V)");
            var viewStandard = new ToolStripMenuItem("标准视图(&S)") { Checked = true };
            viewStandard.Click += cb.ViewStandard;
            var viewFocus = new ToolStripMenuItem("专注模式(&F)");
            viewFocus.Click += cb.ViewFocus;
            var viewCompact = new ToolStripMenuItem("紧凑模式(&C)");
            viewCompact.Click += cb.ViewCompact;
            viewMenu.DropDownItems.Add(viewStandard);
            viewMenu.DropDownItems.Add(viewFocus);
            viewMenu.DropDownItems.Add(viewCompact);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            var toggleTree = new ToolStripMenuItem("切换连接面板(&T)")
            {
                // Ctrl+Shift+L：普通 Ctrl+L 是 shell 清屏，不能被菜单抢走
                ShortcutKeys = Keys.Control | Keys.Shift | Keys.L
            };
            toggleTree.Click += cb.ToggleTree;
            AttachIcon(toggleTree, "globe");
            viewMenu.DropDownItems.Add(toggleTree);
            var toggleTreePin = new ToolStripMenuItem("自动隐藏连接面板(&P)");
            toggleTreePin.Click += cb.ToggleTreePin;
            AttachIcon(toggleTreePin, "splith");
            viewMenu.DropDownItems.Add(toggleTreePin);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(Mk("水平分割", cb.SplitHorizontal, "splith"));
            viewMenu.DropDownItems.Add(Mk("垂直分割", cb.SplitVertical, "splitv"));
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add(Mk("快捷命令栏", cb.ToggleQuickBar, "batch"));
            menu.Items.Add(viewMenu);

            // ===== 终端：终端内搜索 + 会话功能 =====
            var termMenu = new ToolStripMenuItem("终端(&E)");
            termMenu.DropDownItems.Add(Mk("查找 Ctrl+Shift+F", cb.ShowSearch, "search"));
            termMenu.DropDownItems.Add(Mk("片段搜索 Ctrl+Shift+P", cb.ShowSnippet, "snippet"));
            termMenu.DropDownItems.Add(new ToolStripSeparator());
            termMenu.DropDownItems.Add(Mk("高亮规则", cb.ShowHighlight, "highlight"));
            termMenu.DropDownItems.Add(Mk("登录脚本", cb.ShowLogonScript, "script"));
            termMenu.DropDownItems.Add(new ToolStripSeparator());
            termMenu.DropDownItems.Add(Mk("多通道广播", cb.ShowMultiChannel, "broadcast"));
            termMenu.DropDownItems.Add(Mk("批量命令", cb.ShowBatch, "batch"));
            termMenu.DropDownItems.Add(Mk("命令历史", cb.ShowHistory, "history"));
            termMenu.DropDownItems.Add(new ToolStripSeparator());
            termMenu.DropDownItems.Add(Mk("健康监控", cb.ShowHealth, "health"));
            termMenu.DropDownItems.Add(Mk("端口转发", cb.ShowPortForward, "forward"));
            menu.Items.Add(termMenu);

            // ===== 工具：独立工具 + 安全集群 + 设置集群 =====
            var toolsMenu = new ToolStripMenuItem("工具(&T)");
            toolsMenu.DropDownItems.Add(Mk("运维工具箱", cb.ShowToolbox, "toolbox"));
            toolsMenu.DropDownItems.Add(Mk("敏感信息扫描", cb.ShowSecretScan, "scaneye"));
            toolsMenu.DropDownItems.Add(Mk("扫描中心（插件）", cb.ShowScannerCenter, "radar")); // finding-16：与敏感信息扫描图标区分
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(Mk("传输中心", cb.ShowTransferCenter, "transfer"));
            toolsMenu.DropDownItems.Add(Mk("通知中心", cb.ShowNotificationCenter, "notify"));
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(Mk("密码库管理(&K)", cb.KeePassManager, "lock"));
            toolsMenu.DropDownItems.Add(Mk("密码健康报告(&H)", cb.PasswordHealth, "shield"));
            toolsMenu.DropDownItems.Add(Mk("密码生成器(&G)", cb.PasswordGenerator, "key"));
            toolsMenu.DropDownItems.Add(Mk("SSH 密钥管理", cb.SshKeyManager, "key"));
            toolsMenu.DropDownItems.Add(Mk("修改主密码(&M)...", cb.ChangeMasterPassword, "pencil"));
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add(Mk("外观设置(&R)...", cb.AppearanceSettings, "brush"));
            toolsMenu.DropDownItems.Add(Mk("AI 助手设置(&I)", cb.AiSettings, "ai"));
            toolsMenu.DropDownItems.Add(Mk("危险命令规则(&D)", cb.DangerousCmdSettings, "warning"));
            toolsMenu.DropDownItems.Add(Mk("快捷键绑定", cb.ShowKeyBinding, "keyboard"));
            menu.Items.Add(toolsMenu);

            // ===== 帮助 =====
            var helpMenu = new ToolStripMenuItem("帮助(&H)");
            helpMenu.DropDownItems.Add(Mk("快捷键列表", cb.ShowHotkeys, "helpq"));
            helpMenu.DropDownItems.Add(Mk("打开日志文件夹", cb.ShowLogsFolder, "logs"));
            helpMenu.DropDownItems.Add(new ToolStripSeparator());
            helpMenu.DropDownItems.Add(Mk("关于 gdterm", cb.About, "info"));
            menu.Items.Add(helpMenu);

            return new Result
            {
                Menu = menu,
                ViewStandardItem = viewStandard,
                ViewFocusItem = viewFocus,
                ViewCompactItem = viewCompact
            };
        }

        private static void AttachIcon(ToolStripItem item, string icon)
        {
            if (item == null || icon == null) return;
            try
            {
                var img = MenuIconFactory.Get(icon);
                if (img != null) item.Image = img;
            }
            catch { }
        }
    }
}
