using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace Gdterm.Tests.Ui
{
    /// <summary>面板/对话框空态层的期望（与 UiSmokeRunner 的口径一致，这里只是把它搬到公共处）。</summary>
    internal enum PanelEmptyExpect
    {
        /// <summary>不判（面板没有空态层，或数据源不可控）。</summary>
        Ignore,
        /// <summary>空数据源：层必须在树上且可见。</summary>
        Visible,
        /// <summary>有数据源：层必须在树上且隐藏。</summary>
        Hidden
    }

    /// <summary>
    /// 一页 UI 的构造配方。<see cref="Shape"/> 是"这页覆盖了什么控件形态"的人工标注，
    /// 矩阵用它来保证控件形态不是同一种重复 38 遍（表格/多行输入/搜索/树/皮肤 各自至少有页）。
    /// </summary>
    internal sealed class UiPage
    {
        public string Name;
        public string Group;            // "panel" / "dialog"
        public string Shape;            // 形态标签（表格/多行输入/搜索/树/按钮条/纯展示）
        public PanelEmptyExpect Empty;
        /// <summary>构造一页。tmp 是本次专用的临时目录（自动清理），返回的 IDisposable 会被一起释放。</summary>
        public Func<UiTempScope, Control> Create;
    }

    /// <summary>
    /// 一次性临时目录 + 附带 IDisposable 收集器。
    ///
    /// 为什么要这个：面板构造依赖各式 store/manager/scanner，以前每个用例自己
    /// new + finally Dispose + 删 tmp，三份代码（UiSmokeRunner 侧栏、矩阵、状态轴）各写一遍，
    /// 漏一个就是 data/ 被污染或句柄泄漏。收起成一句 using 后，加一页只需要写构造。
    /// </summary>
    internal sealed class UiTempScope : IDisposable
    {
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public UiTempScope(string tag)
        {
            Dir = Path.Combine(Path.GetTempPath(), "gdterm-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; private set; }

        /// <summary>登记一个随作用域释放的对象（原样返回，方便链式 new）。</summary>
        public T Track<T>(T disposable) where T : class
        {
            var d = disposable as IDisposable;
            if (d != null) _owned.Add(d);
            return disposable;
        }

        public string Path_(string name) { return System.IO.Path.Combine(Dir, name); }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                try { _owned[i].Dispose(); } catch (Exception) { }
            }
            _owned.Clear();
            try { Directory.Delete(Dir, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// **一页 UI 只能在这里登记一次**——侧栏冒烟、L2 矩阵、失败态冒烟都从这里取。
    ///
    /// 存在理由（2026-11 审计）：同一批面板曾经有三份构造代码（UiSmokeRunner.SidePanels、
    /// UiMatrixSmoke.Targets、DialogsSmoke），加一个面板要改三处；漏改一处就出现
    /// "矩阵里跑的分明是旧几何"这种假绿。目录化之后新增页面 = 加一条 <see cref="UiPage"/>。
    ///
    /// 不在这里登记的页面（附原因，便于将来补）：
    ///   - sftp-dual-pane / file-pane：需要活的 SFTP 会话（ISftpClient），没有可用的轻量替身；
    ///   - terminal：需要真实 PTY/渲染循环，属于 E2E（docs/UI-UX-VERIFICATION.md §5）；
    ///   - ai-chat：需要 IAiAssistantService 完整替身（流式回调），成本高于本页布局价值。
    /// </summary>
    internal static class UiPageCatalog
    {
        /// <summary>侧栏面板（宿主 360×480 侧栏几何）。</summary>
        public static List<UiPage> Panels()
        {
            var list = new List<UiPage>();
            Add(list, "command-history", "表格/空态", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.CommandHistoryPanel(
                    t.Track(new Gdterm.Logging.CommandHistoryStore(t.Path_("cmdhist")))));
            Add(list, "port-forward", "表格/空态", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.PortForwardPanel(t.Track(new Gdterm.Tunnel.PortForwardManager())));
            Add(list, "secret-scan", "进度/多按钮", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.SecretScanPanel(
                    t.Track(new Gdterm.Security.SecretScanner(Gdterm.Security.SecretScanConfig.GetDefault())), null));
            Add(list, "key-binding", "多列表格", PanelEmptyExpect.Hidden,
                t => new Gdterm.UI.Controls.KeyBindingPanel(
                    t.Track(new Gdterm.Connections.TerminalKeyBindingStore(t.Path_("keybind.json")))));
            Add(list, "toolbox", "按钮网格/空态", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.ToolboxPanel(t.Track(new Gdterm.Tools.ToolRegistry())));
            Add(list, "logon-script", "多行输入", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.LogonScriptPanel(
                    t.Track(new Gdterm.Connections.LogonScriptStore(t.Path_("logon.json")))));
            Add(list, "macro", "表格+按钮排", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.MacroPanel(t.Path_("macros"), null));
            Add(list, "bookmarks", "表格/空态", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.SessionBookmarksPanel(
                    t.Track(new Gdterm.Connections.BookmarkStoreJson(t.Path_("bookmarks"))),
                    t.Track(new Gdterm.Connections.ConnectionStoreJson(t.Path_("conns.json")))));
            Add(list, "command-template", "表格/空态", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.CommandTemplatePanel(
                    t.Track(new Gdterm.Connections.CommandTemplateStore(t.Path_("tpl.json"))), null));
            Add(list, "highlight-rule", "表格", PanelEmptyExpect.Hidden,
                t => new Gdterm.UI.Controls.HighlightRulePanel(
                    t.Track(new Gdterm.Connections.HighlightStore(t.Path_("highlight.json")))));
            Add(list, "transfer-center", "表格/空态", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.TransferCenterPanel());
            // 通知中心用进程级静态缓冲，前面用例可能已写入 → 空态不可控，只 dump 不判
            Add(list, "notification-center", "列表", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.NotificationCenterPanel());
            Add(list, "multi-channel", "多行输入/表格", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.MultiChannelPanel(t.Track(new Gdterm.Terminal.MultiChannelManager())));
            Add(list, "batch-command", "多行输入", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.BatchCommandPanel());
            Add(list, "health-monitor", "纯展示", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.HealthMonitorPanel());
            Add(list, "snippet-search", "搜索框+结果列表/覆盖式", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.SnippetSearchPanel(
                    new List<Gdterm.Core.Models.QuickCommand>()));

            // ===== 2026-11 补齐：此前"渲染成空白页也没人发现"的 6 页 =====
            Add(list, "remote-monitor", "纯展示/度量条", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.RemoteMonitorPanel(null));
            Add(list, "bottom-bar", "状态条/图标按钮", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.BottomBarPanel());
            Add(list, "lock-overlay", "覆盖层", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.LockOverlayControl(t.Track(new Gdterm.Security.SecurityManager())));
            Add(list, "welcome", "欢迎页/空态", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.WelcomePanel(
                    t.Track(new Gdterm.Connections.ConnectionStoreJson(t.Path_("conns.json"))),
                    t.Track(new Gdterm.Connections.BookmarkStoreJson(t.Path_("bookmarks")))));
            // 空库（tmp 空 conns.json）→ 树上必须挂可见的空态引导层（C1/R09）
            Add(list, "connection-tree", "树", PanelEmptyExpect.Visible,
                t => new Gdterm.UI.Controls.ConnectionTreeControl(
                    t.Track(new Gdterm.Connections.ConnectionStoreJson(t.Path_("conns.json"))),
                    new FakeKeePassService(empty: true)));
            Add(list, "terminal-search", "搜索条", PanelEmptyExpect.Ignore,
                t => new Gdterm.UI.Controls.TerminalSearchBar());
            return list;
        }

        /// <summary>
        /// 对话框（独立窗体）。矩阵里跑它们是为了把"对话框在高 DPI/大字号下"也纳入证据——
        /// 此前 26 个对话框只在 96dpi/9pt 下验过一次。
        /// 带后置步骤的页面（session-recordings / log-viewer / import-preview / hostkey /
        /// connection-{ssh,rdp,serial}）留在 DialogsSmoke 里跑，它们的断言是真行为断言，不是几何。
        /// </summary>
        public static List<UiPage> Dialogs()
        {
            var list = new List<UiPage>();
            AddDialog(list, "ai-settings", "表单",
                t => new Gdterm.UI.Forms.AiSettingsForm(
                    t.Track(new Gdterm.AI.AiModelStore(t.Path_("ai.json")))));
            AddDialog(list, "appearance-settings", "表单/配色",
                t => new Gdterm.UI.Forms.AppearanceSettingsForm(t.Dir));
            AddDialog(list, "change-masterpwd", "表单",
                t => new Gdterm.UI.Forms.ChangeMasterPasswordForm(t.Track(new Gdterm.Security.SecurityManager())));
            AddDialog(list, "dangerous-cmd", "表格/多行输入",
                t => new Gdterm.UI.Forms.DangerousCommandConfigForm(
                    t.Track(new Gdterm.Security.DangerousCommandDetector(t.Path_("dangerous.json")))));
            AddDialog(list, "keepass-picker", "表格",
                t => new Gdterm.UI.Forms.KeePassEntryPicker(new FakeKeePassService()));
            AddDialog(list, "keepass-unlock", "表单",
                t => new Gdterm.UI.Forms.KeePassUnlockForm(new FakeKeePassService()));
            AddDialog(list, "keepass-manager", "表格/搜索",
                t => new Gdterm.UI.Forms.KeePassManagerForm(new FakeKeePassService()));
            AddDialog(list, "otp-manager", "表格",
                t => new Gdterm.UI.Forms.OtpManagerForm(
                    t.Track(new Gdterm.Security.Otp.OtpStoreJson(t.Path_("otp.json")))));
            AddDialog(list, "add-otp", "表单",
                t => new Gdterm.UI.Forms.AddOtpForm());
            AddDialog(list, "pwd-generator", "表单/滑块",
                t => new Gdterm.UI.Forms.PasswordGeneratorForm());
            AddDialog(list, "quickcmd-editor", "表单/多行输入",
                t => new Gdterm.UI.Forms.QuickCommandEditorForm());
            AddDialog(list, "sshkey-manager", "表格",
                t => new Gdterm.UI.Forms.SshKeyManagerForm(new FakeKeePassService()));
            AddDialog(list, "transfer-progress", "进度条",
                t => new Gdterm.UI.Controls.TransferProgressDialog("matrix"));
            AddDialog(list, "trash", "表格/空态",
                t => new Gdterm.UI.Forms.TrashForm());
            AddDialog(list, "sftp-permission", "复选组",
                t => new Gdterm.UI.Forms.SftpPermissionForm("report.csv", "rw-r--r--", 644));
            AddDialog(list, "setup-wizard", "向导/步骤条",
                t => new Gdterm.UI.Forms.SetupWizardForm(t.Track(new Gdterm.Security.SecurityManager())));
            return list;
        }

        private static void Add(List<UiPage> list, string name, string shape, PanelEmptyExpect empty,
            Func<UiTempScope, Control> create)
        {
            list.Add(new UiPage { Name = name, Group = "panel", Shape = shape, Empty = empty, Create = create });
        }

        private static void AddDialog(List<UiPage> list, string name, string shape,
            Func<UiTempScope, Form> create)
        {
            // 表单页在矩阵里也要过可见性下限，空态层统一不判（对话框的空态由 DialogsSmoke 负责）
            list.Add(new UiPage
            {
                Name = name,
                Group = "dialog",
                Shape = shape,
                Empty = PanelEmptyExpect.Ignore,
                Create = t => create(t)
            });
        }
    }
}
