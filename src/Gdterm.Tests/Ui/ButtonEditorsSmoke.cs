using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Gdterm.UI.Controls;
using Gdterm.UI.Services;

namespace Gdterm.Tests.Ui
{
    // 打开真实编辑入口并以取消结束。避免只采集主面板，遗漏运行时构造的按钮。
    internal static class ButtonEditorsSmoke
    {
        public static void Run(string directory, Action<bool, string> check)
        {
            var previous = Gdterm.UI.Program.GlobalAppearance;
            try
            {
                foreach (var font in new[] { 9f, 14f })
                {
                    Gdterm.UI.Program.GlobalAppearance = new Gdterm.UI.Forms.AppearanceSettings { UIFontSize = (int)font };
                    RunFont(directory, check);
                }
            }
            finally { Gdterm.UI.Program.GlobalAppearance = previous; }
        }

        private static void RunFont(string directory, Action<bool, string> check)
        {
            foreach (var item in new[] {
                new[] { "bookmarks", "OnAddBookmark", "bookmark" },
                new[] { "port-forward", "AddRule", "forward" },
                new[] { "highlight-rule", "ShowEditor", "highlight" },
                new[] { "logon-script", "ShowEditor", "logon" },
                new[] { "logon-script", "AddStepDialog", "logon-step" },
                new[] { "key-binding", "ShowBindingEditor", "key-binding" },
                new[] { "command-template", "PromptFor", "template" },
                new[] { "snippet-search", "ShowVariableDialog", "snippet" }
            })
            {
                using (var scope = new UiTempScope("button-editors"))
                using (var host = new Form { ClientSize = new Size(540, 480) })
                {
                    var page = UiPageCatalog.Panels().Single(p => p.Name == item[0]);
                    var panel = page.Create(scope);
                    if (item[0] == "bookmarks")
                    {
                        var store = (Gdterm.Connections.IConnectionStore)panel.GetType()
                            .GetField("_connectionStore", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                        store.Add(new Gdterm.Core.Models.ConnectionConfig { Name = "Layout", Host = "localhost" });
                    }
                    host.Controls.Add(panel);
                    host.Show();
                    var method = panel.GetType().GetMethod(item[1], BindingFlags.NonPublic | BindingFlags.Instance);
                    object[] args;
                    if (item[1] == "PromptFor") args = new object[] { "port", "Layout" };
                    else if (item[1] == "ShowVariableDialog") args = new object[] {
                        new Gdterm.Core.Models.QuickCommand { Name = "Layout", Command = "echo {value}" } };
                    else args = new object[method.GetParameters().Length];
                    CaptureModal(host, "editor-" + item[2], directory,
                        () => method.Invoke(panel, args), check);
                }
            }
            using (var host = new Form { ClientSize = new Size(540, 480) })
            {
                host.Show();
                CaptureModal(host, "editor-file-input", directory,
                    () => InputBox.Show(host, "Rename", "Name", "file.txt"), check);
                CaptureModal(host, "editor-file-preview", directory,
                    () => PreviewBox.Show(host, "file.txt", "layout fixture"), check);
                host.BackColor = Gdterm.UI.Diagnostics.GdtermColorTable.Background;
                host.Font = FormFontPolicy.UiFont();
                using (var side = SidePanelHost.CreateHost(null))
                {
                    host.Controls.Add(side);
                    side.Visible = true;
                    CaptureHost(host, "editor-side-close", directory);
                }
                var view = new ViewModeController(null, null, null, null, null, null, null, null, host);
                view.SetViewMode(Gdterm.UI.Forms.ViewMode.Focus);
                CaptureHost(host, "editor-focus-exit", directory);
                view.SetViewMode(Gdterm.UI.Forms.ViewMode.Standard);
                var launcher = new ToolsDialogsLauncher(host, null, new FakeKeePassService(), null);
                CaptureModal(host, "editor-update", directory, () => launcher.GetType()
                    .GetMethod("ShowUpdateResult", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(launcher, new object[] { new UpdateInfo { Version = "0.0.0", Name = "Offline layout", Body = "Layout fixture" } }), check);
                using (var scanner = new Gdterm.Security.SecretScanner(new Gdterm.Security.SecretScanConfig()))
                using (var panel = new SecretScanPanel(scanner))
                {
                    host.Controls.Add(panel);
                    var list = (ListView)panel.GetType().GetField("_lvFindings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                    var item = new ListViewItem("fixture") { Tag = new Gdterm.Security.SecretFinding {
                        RuleName = "Layout", FilePath = "fixture.txt", MatchedContent = "layout", Description = "Layout fixture" } };
                    list.Items.Add(item);
                    item.Selected = true;
                    CaptureModal(host, "editor-secret-details", directory, () => panel.GetType()
                        .GetMethod("OnFindingDoubleClick", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(panel, new object[] { list, EventArgs.Empty }), check);
                }
                using (var security = new Gdterm.Security.SecurityManager())
                {
                    security.Unlock("Layout-Smoke!2026-Button");
                    CaptureModal(host, "editor-master-password", directory,
                        () => MasterPasswordPrompt.Confirm(host, security, "layout"), check);
                }
            }
        }

        private static void CaptureHost(Form form, string name, string directory)
        {
            if (FormFontPolicy.UiFontSize > 9) name += "-f14";
            Application.DoEvents();
            File.WriteAllText(Path.Combine(directory, name + ".json"), UiTreeDumper.Dump(form), System.Text.Encoding.UTF8);
            UiSmokeRunner.Shot(form, Path.Combine(directory, name + ".png"));
        }

        private static void CaptureModal(Form host, string name, string directory, Action open, Action<bool, string> check)
        {
            if (FormFontPolicy.UiFontSize > 9) name += "-f14";
            UiSmokeRunner.Progress(name);
            bool captured = false;
            Exception failure = null;
            using (var timer = new System.Windows.Forms.Timer { Interval = 250 })
            {
                timer.Tick += (s, e) =>
                {
                    var form = Application.OpenForms.Cast<Form>().LastOrDefault(f => f != host && f.Visible);
                    if (form == null) return;
                    timer.Stop();
                    try
                    {
                        form.PerformLayout();
                        File.WriteAllText(Path.Combine(directory, name + ".json"), UiTreeDumper.Dump(form), System.Text.Encoding.UTF8);
                        UiSmokeRunner.Shot(form, Path.Combine(directory, name + ".png"));
                        UiSmokeRunner.AssertDialogActionStyle(form);
                        captured = true;
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.DialogResult = DialogResult.Cancel; form.Close(); }
                };
                timer.Start();
                open();
                // 发现详情是非模态 Show，同样等待一次真实布局和截图后关闭。
                var until = DateTime.UtcNow.AddSeconds(2);
                while (!captured && failure == null && DateTime.UtcNow < until)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(20);
                }
            }
            check(failure == null && captured, name + (failure == null ? " captured and cancelled" : " " + failure.Message));
        }
    }
}
