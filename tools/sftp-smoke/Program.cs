using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Gdterm.Core.Security;
using Gdterm.Sftp;
using Gdterm.Sftp.Models;
using Gdterm.Tunnel;
using Gdterm.UI.Controls;
using Gdterm.UI.Services;

internal static class Program
{
    private static int _checks;
    private static readonly CancellationToken None = CancellationToken.None;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL " + label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }
    private static async Task Reject<T>(Func<Task> operation, string label) where T : Exception
    {
        try { await operation(); }
        catch (T) { Check(true, label); return; }
        throw new Exception("FAIL expected " + typeof(T).Name + ": " + label);
    }
    public static void Main(string[] args) { Run(args).GetAwaiter().GetResult(); }
    private static async Task Run(string[] args)
    {
        var config = new ConnectionConfig { Id = "sftp-smoke", Host = "127.0.0.1", Port = int.Parse(args[0]), Username = Environment.UserName };
        var credential = new CredentialPayload { Username = Environment.UserName, SshPrivateKey = File.ReadAllBytes(args[1]) };
        var store = new KnownHostsStore(args[2]);
        var key = File.ReadAllText(args[3]).Split(' ');
        store.Save(config.Host, config.Port, key[0], key[1]);
        SshHostKeyGuard.Configure(store, null);
        string root = args[4], localDir = Path.Combine(root, "local"), remoteDir = Path.Combine(root, "remote");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(remoteDir);
        var local = Path.Combine(localDir, "sample.txt");
        var remote = Path.Combine(remoteDir, "sample.txt");
        using (var service = new SftpService())
        {
            await service.ConnectAsync(config, credential, None);
            Check(service.IsConnected, "real key authentication");
            File.WriteAllText(local, "NEW");
            File.WriteAllText(remote, "OLD");
            await service.UploadAsync(local, remote, null, None);
            Check(File.ReadAllText(remote) == "NEW", "ordinary upload replaces equal-length stale contents");
            File.WriteAllText(local, "OLD");
            await service.DownloadAsync(remote, local, null, None);
            Check(File.ReadAllText(local) == "NEW", "ordinary download replaces equal-length stale contents");
            File.WriteAllText(local, "abcdef");
            foreach (string stale in new[] { "XYZ", "UVWXYZ", "toolong!!" })
            {
                File.WriteAllText(remote, stale);
                await Reject<IOException>(() => service.UploadResumeAsync(local, remote, null, None), "upload rejects unverified prefix " + stale.Length);
                Check(File.ReadAllText(remote) == stale, "rejected upload leaves destination intact");
            }
            File.WriteAllText(remote, "abc");
            Check(await service.UploadResumeAsync(local, remote, null, None), "verified upload resumes");
            Check(File.ReadAllText(remote) == "abcdef", "resumed upload bytes");
            Check(!await service.UploadResumeAsync(local, remote, null, None), "identical upload skips only after comparison");
            foreach (string stale in new[] { "XYZ", "UVWXYZ", "toolong!!" })
            {
                File.WriteAllText(local, stale);
                await Reject<IOException>(() => service.DownloadResumeAsync(remote, local, null, None), "download rejects unverified prefix " + stale.Length);
                Check(File.ReadAllText(local) == stale, "rejected download leaves destination intact");
            }
            File.WriteAllText(local, "abc");
            await service.DownloadResumeAsync(remote, local, null, None);
            Check(File.ReadAllText(local) == "abcdef", "verified download resumes");
            Check(!await service.DownloadResumeAsync(remote, local, null, None), "identical download skips only after comparison");
            foreach (int mode in new[] { 600, 640, 644, 700, 755, 777, 0 })
            {
                await service.ChmodAsync(remote, mode, None);
                Check(Convert.ToString((int)File.GetUnixFileMode(remote), 8) == mode.ToString(), "chmod exact octal " + mode);
            }
            await service.ChmodAsync(remote, 600, None);
            foreach (int invalid in new[] { -1, 778, 789, 1755 })
                await Reject<ArgumentOutOfRangeException>(() => service.ChmodAsync(remote, invalid, None), "reject invalid chmod " + invalid);
            string target = Path.Combine(remoteDir, "target"), link = Path.Combine(remoteDir, "link");
            File.WriteAllText(target, "KEEP");
            File.CreateSymbolicLink(link, target);
            Check((await service.GetEntryAsync(link, None)).IsSymbolicLink, "metadata preserves link identity");
            await Reject<IOException>(() => service.UploadAsync(local, link, null, None), "upload refuses destination link");
            await Reject<IOException>(() => service.DownloadAsync(link, local, null, None), "download refuses source link");
            await Reject<IOException>(() => service.ChmodAsync(link, 777, None), "chmod refuses link");
            await service.DeleteAsync(link, false, None);
            Check(!Directory.EnumerateFileSystemEntries(remoteDir).Contains(link) && File.ReadAllText(target) == "KEEP", "delete removes link and preserves target");
            foreach (bool dangling in new[] { false, true })
            {
                File.CreateSymbolicLink(link, dangling ? target + "-absent" : target);
                string renamed = link + "-renamed";
                await service.RenameAsync(link, renamed, None);
                Check(!Directory.EnumerateFileSystemEntries(remoteDir).Contains(link)
                    && (File.GetAttributes(renamed) & FileAttributes.ReparsePoint) != 0 && File.ReadAllText(target) == "KEEP", "rename original link, dangling=" + dangling);
                await service.DeleteAsync(renamed, false, None);
            }
            string tree = Path.Combine(remoteDir, "tree"), keep = Path.Combine(remoteDir, "keep-dir");
            Directory.CreateDirectory(keep);
            File.WriteAllText(Path.Combine(keep, "keep"), "KEEP");
            Directory.CreateDirectory(Path.Combine(tree, "child"));
            Directory.CreateSymbolicLink(Path.Combine(tree, "link-dir"), keep);
            File.WriteAllText(Path.Combine(tree, "child", "leaf"), "data");
            await service.DeleteAsync(tree, true, None);
            Check(!Directory.Exists(tree) && File.Exists(Path.Combine(keep, "keep")), "recursive delete removes selected root without following directory link");
            Directory.CreateDirectory(tree);
            await service.DeleteAsync(tree, true, None);
            Check(!Directory.Exists(tree), "delete empty directory");
            string a = Path.Combine(remoteDir, "a"), b = Path.Combine(remoteDir, "b");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            a = Path.Combine(a, "same.txt"); b = Path.Combine(b, "same.txt");
            File.WriteAllText(a, "FIRST\n"); File.WriteAllText(b, "SECOND\n");
            var previews = await Task.WhenAll(SftpEnhancements.PreviewTextFileAsync(service, a), SftpEnhancements.PreviewTextFileAsync(service, b));
            Check(previews[0] == "FIRST" && previews[1] == "SECOND", "concurrent basename previews remain independent");
            File.WriteAllText(a, new string('x', 8 * 1024 * 1024));
            Check((await service.ReadFilePrefixAsync(a, 4097, None)).Length == 4097, "preview reads bounded bytes");
            Check((await SftpEnhancements.PreviewTextFileAsync(service, a)).Length < 263000, "single-line preview has byte bound");
            File.WriteAllText(a, string.Join("\n", Enumerable.Range(0, 1000)));
            Check((await SftpEnhancements.PreviewTextFileAsync(service, a)).Split('\n').Length == 101, "preview caps lines and marks truncation");
            var runner = new SftpTransferRunner(service);
            var entry = new FileEntry { Name = "sample.txt", FullPath = remote };
            var plan = await runner.PlanAsync(new[] { entry }, false, localDir, None);
            Check(plan.Conflicts == 1, "planner counts overwrite conflicts before writing");
            File.WriteAllText(local, "stale");
            await runner.ExecuteAsync(plan, false, null, None);
            Check(File.ReadAllText(local) == "abcdef", "production transfer runner overwrites without implicit resume");
            File.WriteAllText(local, "SYNC!!");
            var syncPlan = new System.Collections.Generic.List<SyncPlanItem> { new SyncPlanItem {
                LocalFullPath = local, RemoteFullPath = remote, RelativePath = "sample.txt", Action = SyncAction.Transfer } };
            var sync = await SftpSyncRunner.RunUploadAsync(service, syncPlan, null, None);
            Check(sync.Transferred == 1 && File.ReadAllText(remote) == "SYNC!!", "sync runner uses overwrite for changed content");
            File.WriteAllText(remote, "abcdef");
            sync = await SftpSyncRunner.RunDownloadAsync(service, syncPlan, null, None);
            Check(sync.Transferred == 1 && File.ReadAllText(local) == "abcdef", "download sync overwrites changed content");
            string nested = Path.Combine(localDir, "nested");
            Directory.CreateDirectory(Path.Combine(nested, "empty"));
            Directory.CreateDirectory(Path.Combine(nested, "child"));
            File.WriteAllText(Path.Combine(nested, "child", "data"), "nested fixture");
            plan = await runner.PlanAsync(new[] { new FileEntry { Name = "nested", FullPath = nested, IsDirectory = true } }, true, remoteDir, None);
            await runner.ExecuteAsync(plan, false, null, None);
            Check(Directory.Exists(Path.Combine(remoteDir, "nested", "empty")) && File.ReadAllText(Path.Combine(remoteDir, "nested", "child", "data")) == "nested fixture", "production recursive upload preserves empty directories and bytes");
            string downloaded = Path.Combine(localDir, "downloaded"); Directory.CreateDirectory(downloaded);
            plan = await runner.PlanAsync(new[] { new FileEntry { Name = "nested", FullPath = Path.Combine(remoteDir, "nested"), IsDirectory = true } }, false, downloaded, None);
            await runner.ExecuteAsync(plan, false, null, None);
            Check(Directory.Exists(Path.Combine(downloaded, "nested", "empty")) && File.ReadAllText(Path.Combine(downloaded, "nested", "child", "data")) == "nested fixture", "production recursive download preserves empty directories and bytes");
            var upper = new FileEntry { Name = "SAMPLE.txt", FullPath = remote };
            await Reject<IOException>(() => runner.PlanAsync(new[] { entry, upper }, false, localDir, None), "planner rejects Windows case collisions");
            foreach (var name in new[] { "..\\outside.txt", "../outside.txt", "C:evil", "CON", "COM1.txt", "NUL.txt", "tail.", "tail ", "a:b", "bad\nname" })
                await Reject<IOException>(() => runner.PlanAsync(new[] { new FileEntry { Name = name, FullPath = remote } }, false, localDir, None), "planner rejects unsafe name " + name.Replace("\n", "\\n"));
            string junction = Path.Combine(localDir, "junction");
            Directory.CreateSymbolicLink(junction, remoteDir);
            await Reject<IOException>(() => runner.PlanAsync(new[] { entry }, false, junction, None), "planner refuses destination reparse point");
            var uploadTree = Path.Combine(localDir, "upload-tree");
            Directory.CreateDirectory(uploadTree);
            Directory.CreateSymbolicLink(Path.Combine(uploadTree, "cycle"), uploadTree);
            await Reject<IOException>(() => runner.PlanAsync(new[] { new FileEntry { Name = "upload-tree", FullPath = uploadTree, IsDirectory = true } }, true, remoteDir, None), "planner rejects local recursive symlink");
            var canceled = new CancellationToken(true);
            await Reject<OperationCanceledException>(() => service.UploadAsync(local, remote, null, canceled), "pre-canceled upload cannot change bytes");
            Check(File.ReadAllText(remote) == "abcdef", "pre-canceled upload preserves destination");
            File.WriteAllText(local, new string('z', 16 * 1024 * 1024));
            using (var cts = new CancellationTokenSource())
            {
                var progress = new CancelProgress(cts);
                await Reject<OperationCanceledException>(() => service.UploadAsync(local, remote, progress, cts.Token), "active upload cancellation propagates");
                Check(progress.Called, "real transfer emits progress");
            }
            service.Disconnect();
            await service.ConnectAsync(config, credential, None);
            Check(service.IsConnected, "reconnect after cancellation");
        }
        using (var service = new SftpService())
        {
            await Reject<OperationCanceledException>(() => service.ConnectAsync(config, credential, new CancellationToken(true)), "pre-canceled connect rejected");
            service.Dispose();
            await Reject<ObjectDisposedException>(() => service.ConnectAsync(config, credential, None), "disposed service cannot resurrect");
            Check(!service.IsConnected, "disposed service stays disconnected");
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stalledConfig = new ConnectionConfig { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        using (var stalled = new SftpService())
        {
            var watch = Stopwatch.StartNew();
            var pending = stalled.ConnectAsync(stalledConfig, credential, None);
            Check(watch.ElapsedMilliseconds < 200, "handshake returns pending task without blocking caller");
            using (var socket = await listener.AcceptTcpClientAsync())
            {
                stalled.Dispose();
                Check(await Task.WhenAny(pending, Task.Delay(3000)) == pending, "dispose interrupts pending handshake promptly");
                await Reject<OperationCanceledException>(() => pending, "disposed pending connection never published");
            }
        }
        listener.Stop();
        Gdterm.Tests.Sftp.SftpPermissionTests.Run();
        Gdterm.Tests.Sftp.SftpSyncPlannerTests.Run();
        Check(Gdterm.Tests.Assert.Failures == 0, "existing permission and sync planner tests");
        Console.WriteLine("SFTP integration: " + _checks + " passed; pure assertions: " + Gdterm.Tests.Assert.Passes);
    }
    private sealed class CancelProgress : IProgress<FileTransferProgress>
    {
        private readonly CancellationTokenSource _cts;
        internal bool Called;
        internal CancelProgress(CancellationTokenSource cts) { _cts = cts; }
        public void Report(FileTransferProgress value) { if (value.BytesTransferred > 0) { Called = true; _cts.Cancel(); } }
    }
}
