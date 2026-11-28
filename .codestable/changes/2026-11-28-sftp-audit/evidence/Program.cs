using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Collections.Generic;
using Gdterm.Core.Models;
using Gdterm.Core.Security;
using Gdterm.Sftp;
using Gdterm.Tunnel;

internal static class Program
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("Unexpected probe result: " + label);
        Console.WriteLine("CONFIRMED " + label);
    }

    public static void Main(string[] args)
    {
        int port = int.Parse(args[0]);
        string root = args[4];
        var credential = new CredentialPayload { Username = Environment.UserName, SshPrivateKey = File.ReadAllBytes(args[1]) };
        var config = new ConnectionConfig { Id = "audit", Host = "127.0.0.1", Port = port, Username = Environment.UserName };
        var store = new KnownHostsStore(args[2]);
        var hostKey = File.ReadAllText(args[3]).Split(' ');
        store.Save(config.Host, port, hostKey[0], hostKey[1]);
        SshHostKeyGuard.Configure(store, null);
        string localDir = Path.Combine(root, "local");
        string remoteDir = Path.Combine(root, "remote");
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(remoteDir);
        string local = Path.Combine(localDir, "sample.txt");
        string remote = Path.Combine(remoteDir, "sample.txt");
        var none = CancellationToken.None;
        using (var service = new SftpService())
        {
            service.ConnectAsync(config, credential, none).GetAwaiter().GetResult();
            File.WriteAllText(local, "NEW");
            service.UploadAsync(local, remote, null, none).GetAwaiter().GetResult();
            Check(File.ReadAllText(remote) == "NEW", "fresh upload writes exact bytes");
            File.Delete(local);
            service.DownloadAsync(remote, local, null, none).GetAwaiter().GetResult();
            Check(File.ReadAllText(local) == "NEW", "fresh download writes exact bytes");

            File.WriteAllText(remote, "OLD");
            bool transferred = service.UploadResumeAsync(local, remote, null, none).GetAwaiter().GetResult();
            Check(!transferred && File.ReadAllText(remote) == "OLD", "DEFECT equal-length upload silently retains OLD instead of NEW");
            File.WriteAllText(local, "abcdef");
            File.WriteAllText(remote, "XYZ");
            service.UploadResumeAsync(local, remote, null, none).GetAwaiter().GetResult();
            Check(File.ReadAllText(remote) == "XYZdef", "DEFECT shorter destination upload corrupts result to XYZdef instead of abcdef");
            File.WriteAllText(remote, "NEW");
            File.WriteAllText(local, "OLD");
            transferred = service.DownloadResumeAsync(remote, local, null, none).GetAwaiter().GetResult();
            Check(!transferred && File.ReadAllText(local) == "OLD", "DEFECT equal-length download silently retains OLD instead of NEW");
            File.WriteAllText(remote, "abcdef");
            File.WriteAllText(local, "XYZ");
            service.DownloadResumeAsync(remote, local, null, none).GetAwaiter().GetResult();
            Check(File.ReadAllText(local) == "XYZdef", "DEFECT shorter destination download corrupts result to XYZdef instead of abcdef");

            string dir = Path.Combine(remoteDir, "delete-me");
            Directory.CreateDirectory(Path.Combine(dir, "child"));
            File.WriteAllText(Path.Combine(dir, "child", "leaf"), "fixture");
            service.DeleteAsync(dir, true, none).GetAwaiter().GetResult();
            Check(Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(), "DEFECT recursive delete reports success but selected root directory remains");
            service.DeleteAsync(dir, true, none).GetAwaiter().GetResult();
            Check(Directory.Exists(dir), "DEFECT retry deleting now-empty directory still leaves it present");

            string renamed = Path.Combine(remoteDir, "renamed.txt");
            service.RenameAsync(remote, renamed, none).GetAwaiter().GetResult();
            Check(File.Exists(renamed) && !File.Exists(remote), "rename moves selected file");
            service.ChmodAsync(renamed, 640, none).GetAwaiter().GetResult();
            var info = service.ListDirectoryAsync(remoteDir, none).GetAwaiter().GetResult().Single(f => f.Name == "renamed.txt");
            Console.WriteLine("OBSERVED requested chmod 640 -> " + info.Permissions + "; actual octal=" + Convert.ToString((int)File.GetUnixFileMode(renamed), 8));
            Check(info.Permissions != "rw-r-----", "DEFECT chmod 640 grants different permissions than requested");
            foreach (int requested in new[] { 600, 644, 700, 755, 777 })
            {
                try
                {
                    service.ChmodAsync(renamed, requested, none).GetAwaiter().GetResult();
                    Console.WriteLine("OBSERVED requested chmod " + requested + " -> actual octal=" + Convert.ToString((int)File.GetUnixFileMode(renamed), 8));
                }
                catch (Exception ex) { Console.WriteLine("OBSERVED requested chmod " + requested + " throws " + ex.GetType().Name + ": " + ex.Message); }
            }
            var raw = (Renci.SshNet.SftpClient)typeof(SftpService).GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(service);
            raw.ChangePermissions(renamed, 640);
            Check(Convert.ToString((int)File.GetUnixFileMode(renamed), 8) == "640", "control: SSH.NET ChangePermissions accepts decimal digits 640 directly");
            service.ChmodAsync(renamed, 0, none).GetAwaiter().GetResult();
            info = service.ListDirectoryAsync(remoteDir, none).GetAwaiter().GetResult().Single(f => f.Name == "renamed.txt");
            Check(info.Permissions == "---------", "default UI OctalMode=0 reaches real server as chmod 000");
            raw.ChangePermissions(renamed, 600);
            service.DeleteAsync(renamed, false, none).GetAwaiter().GetResult();
            Check(!File.Exists(renamed), "single-file deletion removes the file");

            string unixName = "..\\outside.txt";
            File.WriteAllText(Path.Combine(remoteDir, unixName), "fixture");
            info = service.ListDirectoryAsync(remoteDir, none).GetAwaiter().GetResult().Single(f => f.Name == unixName);
            Check(info.Name == unixName, "remote POSIX filename containing Windows parent traversal reaches model unchanged");

            string target = Path.Combine(remoteDir, "link-target");
            string link = Path.Combine(remoteDir, "link-dir");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep"), "keep");
            Directory.CreateSymbolicLink(link, target);
            info = service.ListDirectoryAsync(remoteDir, none).GetAwaiter().GetResult().Single(f => f.Name == "link-dir");
            Console.WriteLine("OBSERVED directory symlink IsDirectory=" + info.IsDirectory);
            try { service.DeleteAsync(link, info.IsDirectory, none).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.WriteLine("OBSERVED directory symlink delete throws " + ex.GetType().Name); }
            Check(File.Exists(Path.Combine(target, "keep")), "non-recursive directory symlink deletion did not remove target content");
            string fileTarget = Path.Combine(remoteDir, "file-target");
            string fileLink = Path.Combine(remoteDir, "file-link");
            File.WriteAllText(fileTarget, "keep");
            File.CreateSymbolicLink(fileLink, fileTarget);
            service.DeleteAsync(fileLink, false, none).GetAwaiter().GetResult();
            Console.WriteLine("OBSERVED file symlink delete: targetExists=" + File.Exists(fileTarget) + "; linkEntryExists=" + Directory.EnumerateFileSystemEntries(remoteDir).Contains(fileLink));
            Check(!File.Exists(fileTarget) && Directory.EnumerateFileSystemEntries(remoteDir).Contains(fileLink), "DEFECT deleting file symlink removes its target and leaves a dangling link");
            File.WriteAllText(fileTarget, "keep");
            string renamedLink = Path.Combine(remoteDir, "renamed-link");
            service.RenameAsync(fileLink, renamedLink, none).GetAwaiter().GetResult();
            Console.WriteLine("OBSERVED file symlink rename: targetExists=" + File.Exists(fileTarget) + "; originalLinkEntryExists=" + Directory.EnumerateFileSystemEntries(remoteDir).Contains(fileLink) + "; renamedEntryIsLink=" + ((File.GetAttributes(renamedLink) & FileAttributes.ReparsePoint) != 0));

            string previewName = "audit-" + Guid.NewGuid().ToString("N") + ".txt";
            string firstPath = Path.Combine(remoteDir, previewName);
            string secondFolder = Path.Combine(remoteDir, "preview-other");
            Directory.CreateDirectory(secondFolder);
            string secondPath = Path.Combine(secondFolder, previewName);
            File.WriteAllText(firstPath, "FIRST\n");
            File.WriteAllText(secondPath, "SECOND\n");
            var gateService = DispatchProxy.Create<ISftpService, DownloadGate>();
            var gate = (DownloadGate)(object)gateService;
            gate.Inner = service;
            var firstPreview = SftpEnhancements.PreviewTextFileAsync(gateService, firstPath);
            string secondPreview = SftpEnhancements.PreviewTextFileAsync(service, secondPath).GetAwaiter().GetResult();
            gate.Release.SetResult(true);
            bool collision = false;
            try { firstPreview.GetAwaiter().GetResult(); }
            catch (FileNotFoundException) { collision = true; }
            Check(collision && secondPreview == "SECOND", "DEFECT simultaneous basename previews share temp path; one deletes the other's input");

            string bigPath = Path.Combine(remoteDir, "big-" + previewName);
            File.WriteAllText(bigPath, string.Concat(Enumerable.Repeat(new string('x', 63) + "\n", 131072)));
            gateService = DispatchProxy.Create<ISftpService, DownloadGate>();
            gate = (DownloadGate)(object)gateService;
            gate.Inner = service;
            gate.Release.SetResult(true);
            var preview = SftpEnhancements.PreviewTextFileAsync(gateService, bigPath, 100).GetAwaiter().GetResult();
            Check(gate.DownloadedSize == 8388608 && preview.Length < 7000, "DEFECT 100-line preview downloads full 8388608-byte file before displaying under 7000 chars");
        }

        using (var service = new SftpService())
        {
            service.ConnectAsync(config, credential, new CancellationToken(true)).GetAwaiter().GetResult();
            Check(service.IsConnected, "DEFECT already-cancelled ConnectAsync still establishes connection");
        }
        var disposed = new SftpService();
        disposed.Dispose();
        disposed.ConnectAsync(config, credential, none).GetAwaiter().GetResult();
        disposed.Dispose();
        Check(disposed.IsConnected, "DEFECT connect after dispose resurrects session; second dispose fails to close it");
        disposed.Disconnect();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int stalledPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using (var client = await listener.AcceptTcpClientAsync()) await Task.Delay(700);
        });
        using (var stalled = new SftpService())
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = stalled.ConnectAsync(new ConnectionConfig { Host = "127.0.0.1", Port = stalledPort }, credential, none);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("OBSERVED ConnectAsync synchronous exception=" + ex.GetType().Name + "; caller blocked ms=" + sw.ElapsedMilliseconds);
                Check(sw.ElapsedMilliseconds >= 650, "DEFECT ConnectAsync blocks caller during SSH handshake instead of returning pending Task");
            }
        }
        serverTask.GetAwaiter().GetResult();
        listener.Stop();
        Gdterm.Tests.Sftp.SftpPermissionTests.Run();
        Gdterm.Tests.Sftp.SftpSyncPlannerTests.Run();
        Console.WriteLine("EXISTING_TEST_COUNTS passed=" + Gdterm.Tests.Assert.Passes + " failed=" + Gdterm.Tests.Assert.Failures);
        Check(Gdterm.Tests.Assert.Failures == 0, "existing SFTP pure-function assertions pass despite above defects");
    }
}

public class DownloadGate : DispatchProxy
{
    public ISftpService Inner;
    public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public long DownloadedSize;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "DownloadAsync") return Download(args);
        return method.Invoke(Inner, args);
    }
    private async Task Download(object[] args)
    {
        await Inner.DownloadAsync((string)args[0], (string)args[1], null, (CancellationToken)args[3]);
        DownloadedSize = new FileInfo((string)args[1]).Length;
        await Release.Task;
    }
}
