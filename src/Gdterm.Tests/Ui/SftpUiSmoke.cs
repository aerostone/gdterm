using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gdterm.Core.Models;
using Gdterm.Sftp;
using Gdterm.Sftp.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;

namespace Gdterm.Tests.Ui
{
    internal static class SftpUiSmoke
    {
        private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        private static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, args);
        private static void PumpUntil(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            if (!done()) throw new TimeoutException("SFTP UI operation did not settle");
            Application.DoEvents();
        }
        private static ConnectionConfig Config() => new ConnectionConfig { Id = "sftp-ui", Host = "files.example.internal", Port = 22, Username = "operator" };
        internal static SftpDualPanePanel Scene(bool disconnected = false)
        {
            return new SftpDualPanePanel(Config(), new CredentialPayload { Username = "operator" }, new Factory { Fail = disconnected }, null);
        }
        internal static void Run(string temp, string outDir, Action<bool, string> check)
        {
            check(Application.MessageLoop && SynchronizationContext.Current is WindowsFormsSynchronizationContext,
                "sftp runs within production WinForms message loop");
            foreach (var mode in new[] { 600, 640, 644, 755 })
            {
                var bits = Convert.ToInt32(mode.ToString(), 8);
                using (var form = new SftpPermissionForm("report.txt", SftpEnhancements.OctalToPermissionString(bits), bits))
                {
                    form.Show(); Application.DoEvents();
                    check(form.OctalMode == mode && Field<AntdUI.InputNumber>(form, "_octalBox").Value == mode, "sftp permission initializes " + mode);
                    var checks = Field<AntdUI.Checkbox[,]>(form, "_chk");
                    Field<AntdUI.InputNumber>(form, "_octalBox").Value = 600;
                    check(checks[0, 0].Checked && checks[0, 1].Checked && !checks[0, 2].Checked && !checks[1, 0].Checked, "sftp octal updates checkboxes");
                    checks[0, 2].Checked = true;
                    check(form.OctalMode == 700, "sftp checkbox updates octal");
                    ((AntdUI.Button)form.AcceptButton).PerformClick();
                    check(form.DialogResult == DialogResult.OK && form.OctalMode == 700, "sftp apply retains selected mode");
                }
            }
            using (var form = new SftpPermissionForm("fallback", null, 493)) check(form.OctalMode == 755, "sftp missing rwx uses bit value");
            using (var form = new SftpPermissionForm("special", "rwSr--r-T", 0)) check(form.OctalMode == 644, "uppercase S/T do not imply execute");

            UiSmokeRunner.Progress("sftp:create-panel");
            var factory = new Factory();
            using (var host = new Form { ClientSize = new System.Drawing.Size(1000, 600) })
            using (var panel = new SftpDualPanePanel(Config(), new CredentialPayload(), factory, null))
            {
                try
                {
                UiSmokeRunner.Progress("sftp:show-panel");
                host.Controls.Add(panel); host.Show();
                PumpUntil(() => Field<FilePaneControl>(panel, "_remote")?.CurrentPath == "/");
                var remote = Field<FilePaneControl>(panel, "_remote");
                var local = Field<FilePaneControl>(panel, "_local");
                string localPath = Path.Combine(temp, "sftp-local"); Directory.CreateDirectory(localPath);
                local.Navigate(localPath); PumpUntil(() => !local.IsBusy && local.CurrentPath == localPath);
                check(SynchronizationContext.Current is WindowsFormsSynchronizationContext,
                    "sftp navigation retains UI synchronization context after pumping messages");
                Capture(host, outDir, "sftp-dual-pane-wide");
                var entries = new[] { new FileEntry { Name = "report.txt", FullPath = "/report.txt" } };
                check(local.CanAcceptDrop(new FilePaneDrag(remote.Provider, entries)), "sftp accepts actual opposite pane drag");
                check(!local.CanAcceptDrop(new FilePaneDrag(new SftpFilePaneProvider(new Fake(), "other server"), entries)), "sftp rejects other server drag");
                check(!local.CanAcceptDrop(new FilePaneDrag(local.Provider, entries)), "sftp rejects same side drag");
                int drops = 0;
                local.EntriesDropped += e => drops++;
                var empty = Field<EmptyStateView>(local, "_empty");
                var data = new DataObject(); data.SetData("GdtermFileEntries", false, new FilePaneDrag(remote.Provider, entries));
                typeof(Control).GetMethod("OnDragDrop", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(empty,
                    new object[] { new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy) });
                check(empty.AllowDrop && drops == 1, "sftp empty pane receives valid drop");
                PumpUntil(() => Field<int>(panel, "_activeTransfers") == 0 && !local.IsBusy);
                check(File.Exists(Path.Combine(localPath, "report.txt")), "actual drop downloads into captured local directory");
                foreach (var transfer in Application.OpenForms.OfType<TransferProgressDialog>().ToArray()) { transfer.Close(); transfer.Dispose(); }
                var otherConfig = Config(); otherConfig.Id = "other-credential";
                check(!panel.MatchesConnection(otherConfig), "sftp follow rejects same host different connection");
                otherConfig = Config(); otherConfig.Port = 2222;
                check(!panel.MatchesConnection(otherConfig) && panel.MatchesConnection(Config()), "sftp follow checks port and identity");
                UiSmokeRunner.Progress("sftp:queued-navigation");
                factory.Last.ListGate = new TaskCompletionSource<IList<SftpFileInfo>>();
                remote.Navigate("/slow"); panel.FollowTo("/latest");
                factory.Last.ListGate.SetResult(new List<SftpFileInfo>());
                PumpUntil(() => remote.CurrentPath == "/latest" && !remote.IsBusy);
                check(remote.CurrentPath == "/latest", "sftp busy navigation retains latest followed path");
                UiSmokeRunner.Progress("sftp:reconnect");
                factory.Last.Connected = false;
                Call(panel, "CheckConnection");
                check(!remote.Enabled && Field<AntdUI.Button>(panel, "_reconnect").Enabled, "sftp disconnect disables stale pane and offers reconnect");
                Field<AntdUI.Button>(panel, "_reconnect").PerformClick();
                PumpUntil(() => factory.Created == 2 && Field<FilePaneControl>(panel, "_remote").CurrentPath != null);
                check(factory.Last.Connected && remote.IsDisposed, "sftp reconnect replaces old pane and service");
                remote = Field<FilePaneControl>(panel, "_remote");
                remote.Navigate("/"); PumpUntil(() => !remote.IsBusy);
                UiSmokeRunner.Progress("sftp:active-transfer-cancel");
                string file = Path.Combine(temp, "upload.txt"); File.WriteAllText(file, "upload fixture");
                factory.Last.HoldUpload = true;
                Call(panel, "OnTransfer", new[] { new FileEntry { Name = "upload.txt", FullPath = file } }, true, false);
                PumpUntil(() => factory.Last.UploadStarted);
                var dialog = Application.OpenForms.OfType<TransferProgressDialog>().Single();
                check(Field<AntdUI.Label>(dialog, "_detailLabel").Text.Contains("upload.txt"), "production transfer dialog displays current file");
                Capture(dialog, outDir, "transfer-progress-active");
                CheckTransferText(dialog, outDir, "transfer-progress-active", check);
                Field<AntdUI.Button>(dialog, "_cancelButton").PerformClick();
                PumpUntil(() => factory.Last.UploadCanceled && Field<int>(panel, "_activeTransfers") == 0);
                check(dialog.IsCancelled && Field<AntdUI.Button>(panel, "_reconnect").Enabled, "production cancel reaches active transfer and allows reconnect");
                Capture(dialog, outDir, "transfer-progress-canceled");
                CheckTransferText(dialog, outDir, "transfer-progress-canceled", check);
                dialog.Close(); dialog.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("SFTP UI detail: thread=" + Thread.CurrentThread.ManagedThreadId
                        + " context=" + SynchronizationContext.Current?.GetType().Name + " " + ex);
                    foreach (var name in new[] { "_local", "_remote" })
                    {
                        var pane = Field<FilePaneControl>(panel, name);
                        if (pane != null) Console.WriteLine(name + ": path=" + pane.CurrentPath
                            + " busy=" + pane.IsBusy + " handle=" + pane.IsHandleCreated
                            + " invokeRequired=" + pane.InvokeRequired
                            + " status=" + Field<AntdUI.Label>(pane, "_status").Text);
                    }
                    Gdterm.UI.Diagnostics.CrashLog.FlushSync();
                    throw;
                }
            }
            var delayed = new Factory { DelayConnect = true };
            using (var host = new Form())
            {
                var closed = new SftpDualPanePanel(Config(), new CredentialPayload(), delayed, null);
                host.Controls.Add(closed); host.Show();
                PumpUntil(() => delayed.Last != null);
                closed.Dispose();
                PumpUntil(() => delayed.Last.Disposed);
                check(delayed.Last.Disposed && !delayed.Last.Connected, "sftp panel close cancels connection without resurrection");
            }
            var neverShown = new Factory();
            new SftpDualPanePanel(Config(), new CredentialPayload(), neverShown, null).Dispose();
            check(neverShown.Created == 0, "unshown panel does not start background connection");
        }
        private static void Capture(Form form, string directory, string name)
        {
            form.Refresh(); Application.DoEvents();
            File.WriteAllText(Path.Combine(directory, name + ".json"), UiTreeDumper.Dump(form), System.Text.Encoding.UTF8);
            UiSmokeRunner.Shot(form, Path.Combine(directory, name + ".png"));
        }
        private static void CheckTransferText(TransferProgressDialog form, string directory, string name, Action<bool, string> check)
        {
            foreach (var field in new[] { "_titleLabel", "_detailLabel" })
            {
                var label = Field<AntdUI.Label>(form, field);
                var bounds = form.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
                int ink = UiStateSmoke.CountColorPixels(Path.Combine(directory, name + ".png"), bounds,
                    label.ForeColor ?? Gdterm.UI.Diagnostics.GdtermColorTable.Foreground, 40);
                check(ink >= 20, name + " renders " + field + " text (pixels=" + ink + ")");
            }
        }
        private sealed class Factory : ISftpServiceFactory
        {
            internal bool Fail, DelayConnect;
            internal Fake Last;
            internal int Created;
            public ISftpService Create() { Created++; return Last = new Fake { Fail = Fail, DelayConnect = DelayConnect }; }
        }
        private sealed class Fake : ISftpService
        {
            internal bool Connected, Fail, DelayConnect, Disposed, HoldUpload, UploadStarted, UploadCanceled;
            internal TaskCompletionSource<IList<SftpFileInfo>> ListGate;
            public bool IsConnected => Connected && !Disposed;
            public async Task ConnectAsync(ConnectionConfig config, CredentialPayload credential, CancellationToken ct)
            {
                if (Fail) throw new IOException("Offline fixture: server unavailable");
                if (DelayConnect) await Task.Delay(10000, ct);
                ct.ThrowIfCancellationRequested();
                if (Disposed) throw new ObjectDisposedException("fake");
                Connected = true;
            }
            public Task ConnectViaTunnelAsync(ConnectionConfig c, CredentialPayload p, TunnelEndpoint e, CancellationToken ct) => ConnectAsync(c, p, ct);
            public Task<IList<SftpFileInfo>> ListDirectoryAsync(string path, CancellationToken ct)
            {
                if (ListGate != null && path == "/slow") return ListGate.Task;
                return Task.FromResult<IList<SftpFileInfo>>(new List<SftpFileInfo> {
                    new SftpFileInfo { Name = "reports", FullPath = "/reports", IsDirectory = true, Permissions = "rwxr-x---" },
                    new SftpFileInfo { Name = "report.txt", FullPath = "/report.txt", SizeBytes = 4096, Permissions = "rw-r-----", LastModified = new DateTime(2026, 11, 28) }
                });
            }
            public Task<SftpFileInfo> GetEntryAsync(string path, CancellationToken ct) => Task.FromResult(path == "/"
                ? new SftpFileInfo { FullPath = path, IsDirectory = true }
                : path == "/report.txt" ? new SftpFileInfo { Name = "report.txt", FullPath = path, SizeBytes = 4 } : null);
            public Task<byte[]> ReadFilePrefixAsync(string p, int max, CancellationToken ct) => Task.FromResult(new byte[0]);
            public async Task UploadAsync(string local, string remote, IProgress<FileTransferProgress> progress, CancellationToken ct)
            {
                UploadStarted = true;
                progress?.Report(new FileTransferProgress { BytesTransferred = 1, TotalBytes = new FileInfo(local).Length });
                try { if (HoldUpload) await Task.Delay(10000, ct); }
                catch (OperationCanceledException) { UploadCanceled = true; Connected = false; throw; }
            }
            public Task DownloadAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t)
            {
                t.ThrowIfCancellationRequested();
                File.WriteAllText(b, "data");
                p?.Report(new FileTransferProgress { BytesTransferred = 4, TotalBytes = 4 });
                return Task.CompletedTask;
            }
            public Task<bool> UploadResumeAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t) => throw new NotSupportedException();
            public Task<bool> DownloadResumeAsync(string a, string b, IProgress<FileTransferProgress> p, CancellationToken t) => throw new NotSupportedException();
            public Task DeleteAsync(string p, bool recursive, CancellationToken t) => Task.CompletedTask;
            public Task CreateDirectoryAsync(string p, CancellationToken t) => Task.CompletedTask;
            public Task RenameAsync(string a, string b, CancellationToken t) => Task.CompletedTask;
            public Task ChmodAsync(string p, int mode, CancellationToken t) => Task.CompletedTask;
            public void Disconnect() { Connected = false; }
            public void Dispose() { Disposed = true; Connected = false; }
        }
    }
}
