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
            foreach (var item in new[] {
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
                    host.Controls.Add(panel);
                    host.Show();
                    var method = panel.GetType().GetMethod(item[1], BindingFlags.NonPublic | BindingFlags.Instance);
                    object[] args;
                    if (item[1] == "PromptFor") args = new object[] { "port", "Layout" };
                    else if (item[1] == "ShowVariableDialog") args = new object[] {
                        new Gdterm.Core.Models.QuickCommand { Name = "Layout", Command = "echo {value}" } };
                    else args = method.GetParameters().Length == 0 ? new object[0] : new object[] { null };
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
                using (var security = new Gdterm.Security.SecurityManager())
                {
                    security.Unlock("Layout-Smoke!2026-Button");
                    CaptureModal(host, "editor-master-password", directory,
                        () => MasterPasswordPrompt.Confirm(host, security, "layout"), check);
                }
            }
        }

        private static void CaptureModal(Form host, string name, string directory, Action open, Action<bool, string> check)
        {
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
            }
            check(failure == null && captured, name + (failure == null ? " captured and cancelled" : " " + failure.Message));
        }
    }
}
