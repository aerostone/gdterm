using System;
using System.Windows.Forms;

namespace Gdterm.UI.Services
{
    /// <summary>
    /// MainForm 菜单构建——把菜单树从 InitializeComponent 中抽出（finding-10）。
    /// 事件仍由 MainForm 提供回调，避免菜单类持有全部服务依赖。
    /// </summary>
    public sealed class MainFormMenuBuilder
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

            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add("新建连接(&N)", null, cb.NewConnection);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("导入连接(&I)...", null, cb.ImportConnections);
            fileMenu.DropDownItems.Add("导出连接(&E)...", null, cb.ExportConnections);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("退出(&X)", null, cb.Exit);
            menu.Items.Add(fileMenu);

            var connectionMenu = new ToolStripMenuItem("连接(&C)");
            connectionMenu.DropDownItems.Add("新建连接", null, cb.NewConnection);
            connectionMenu.DropDownItems.Add("本地终端(&L)", null, cb.OpenLocalTerminal);
            connectionMenu.DropDownItems.Add("SFTP 浏览器", null, cb.OpenSftp);
            connectionMenu.DropDownItems.Add(new ToolStripSeparator());
            connectionMenu.DropDownItems.Add("重连当前标签 Ctrl+R", null, cb.ReconnectActive);
            connectionMenu.DropDownItems.Add("关闭当前标签 Ctrl+W", null, cb.CloseActive);
            menu.Items.Add(connectionMenu);

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
                ShortcutKeys = Keys.Control | Keys.L
            };
            toggleTree.Click += cb.ToggleTree;
            viewMenu.DropDownItems.Add(toggleTree);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add("水平分割", null, cb.SplitHorizontal);
            viewMenu.DropDownItems.Add("垂直分割", null, cb.SplitVertical);
            viewMenu.DropDownItems.Add(new ToolStripSeparator());
            viewMenu.DropDownItems.Add("快捷命令栏", null, cb.ToggleQuickBar);
            menu.Items.Add(viewMenu);

            var termMenu = new ToolStripMenuItem("终端(&E)");
            termMenu.DropDownItems.Add("查找 Ctrl+F", null, cb.ShowSearch);
            termMenu.DropDownItems.Add("片段搜索 Ctrl+P", null, cb.ShowSnippet);
            termMenu.DropDownItems.Add("高亮规则", null, cb.ShowHighlight);
            termMenu.DropDownItems.Add("快捷键绑定", null, cb.ShowKeyBinding);
            termMenu.DropDownItems.Add("登录脚本", null, cb.ShowLogonScript);
            termMenu.DropDownItems.Add(new ToolStripSeparator());
            termMenu.DropDownItems.Add("多通道广播", null, cb.ShowMultiChannel);
            termMenu.DropDownItems.Add("批量命令", null, cb.ShowBatch);
            termMenu.DropDownItems.Add("命令历史", null, cb.ShowHistory);
            termMenu.DropDownItems.Add("健康监控", null, cb.ShowHealth);
            termMenu.DropDownItems.Add("端口转发", null, cb.ShowPortForward);
            menu.Items.Add(termMenu);

            var toolsMenu = new ToolStripMenuItem("工具(&T)");
            toolsMenu.DropDownItems.Add("运维工具箱", null, cb.ShowToolbox);
            toolsMenu.DropDownItems.Add("敏感信息扫描", null, cb.ShowSecretScan);
            toolsMenu.DropDownItems.Add("书签 / 最近连接", null, cb.ShowBookmarks);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("密码库管理(&K)", null, cb.KeePassManager);
            toolsMenu.DropDownItems.Add("密码健康报告(&H)", null, cb.PasswordHealth);
            toolsMenu.DropDownItems.Add("🔑 密码生成器(&G)", null, cb.PasswordGenerator);
            toolsMenu.DropDownItems.Add("修改主密码(&M)...", null, cb.ChangeMasterPassword);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("外观设置(&R)...", null, cb.AppearanceSettings);
            toolsMenu.DropDownItems.Add("AI 助手设置(&I)", null, cb.AiSettings);
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("危险命令规则(&D)", null, cb.DangerousCmdSettings);
            menu.Items.Add(toolsMenu);

            var helpMenu = new ToolStripMenuItem("帮助(&H)");
            helpMenu.DropDownItems.Add("快捷键列表", null, cb.ShowHotkeys);
            helpMenu.DropDownItems.Add("关于 gdterm", null, cb.About);
            menu.Items.Add(helpMenu);

            return new Result
            {
                Menu = menu,
                ViewStandardItem = viewStandard,
                ViewFocusItem = viewFocus,
                ViewCompactItem = viewCompact
            };
        }
    }
}
