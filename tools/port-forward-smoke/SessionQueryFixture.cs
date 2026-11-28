// Linux adapter for the actual TabActiveSessionQuery. Only WinForms containers and
// unrelated terminal/tool interfaces are replaced; SSH clients and resolver code are real.
using System.Collections.Generic;
namespace System.Windows.Forms { public class TabPage { } }
namespace Gdterm.Core.Enums { public enum ProtocolType { SSH } }
namespace Gdterm.Core.Models
{
    public class ConnectionConfig { public string Id, Name, Host; public Gdterm.Core.Enums.ProtocolType Protocol; }
}
namespace Gdterm.Terminal
{
    public interface ITerminalSession { object TryGetSshClient(); }
    public sealed class ConnectionHealthMonitor { }
    public sealed class QuerySession : ITerminalSession
    {
        public Renci.SshNet.SshClient Client;
        public object TryGetSshClient() => Client;
    }
}
namespace Gdterm.Tools
{
    public interface ISshRemoteSession { }
    public static class SshNetRemoteSession { public static ISshRemoteSession FromClientObject(object c) => null; }
}
namespace Gdterm.UI.Controls
{
    public class TerminalControl
    {
        public Gdterm.Terminal.ITerminalSession Session;
        public Gdterm.Core.Models.ConnectionConfig Config;
        public bool IsDisposed;
        public bool IsConnected => (Session?.TryGetSshClient() as Renci.SshNet.SshClient)?.IsConnected == true;
    }
    public static class SplitPaneControl
    {
        public static TerminalControl FindFirstTerminal(object control) => (control as List<TerminalControl>)?[0];
        public static void CollectTerminals(object control, IList<TerminalControl> into)
        { var list = control as List<TerminalControl>; if (list != null) foreach (var tc in list) into.Add(tc); }
    }
}
namespace Gdterm.UI.Services
{
    public class TabSessionState
    {
        public Gdterm.Core.Models.ConnectionConfig Config;
        public Gdterm.UI.Controls.TerminalControl PrimaryTerminal;
        public object Control;
        public string SessionId;
        public Gdterm.Terminal.ConnectionHealthMonitor HealthMonitor;
    }
}
