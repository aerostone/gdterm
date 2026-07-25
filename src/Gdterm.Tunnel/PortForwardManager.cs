using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gdterm.Core.Models;
using Renci.SshNet;

namespace Gdterm.Tunnel
{
    /// <summary>
    /// 端口转发管理器——可视化管理本地/远程/动态端口转发
    /// </summary>
    public class PortForwardManager : IDisposable
    {
        private readonly Dictionary<string, ForwardedPort> _activeForwards = new Dictionary<string, ForwardedPort>();
        private readonly object _lock = new object();

        public event Action<string, bool> ForwardStatusChanged; // ruleId, isActive

        /// <summary>启动本地转发</summary>
        public bool StartLocal(SshClient client, PortForwardRule rule)
        {
            if (client == null || !client.IsConnected) return false;
            lock (_lock)
            {
                if (_activeForwards.ContainsKey(rule.Id)) return false;
                try
                {
                    var port = new ForwardedPortLocal(rule.LocalHost, (uint)rule.LocalPort, rule.RemoteHost, (uint)rule.RemotePort);
                    client.AddForwardedPort(port);
                    port.Start();
                    _activeForwards[rule.Id] = port;
                    ForwardStatusChanged?.Invoke(rule.Id, true);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>启动远程转发</summary>
        public bool StartRemote(SshClient client, PortForwardRule rule)
        {
            if (client == null || !client.IsConnected) return false;
            lock (_lock)
            {
                if (_activeForwards.ContainsKey(rule.Id)) return false;
                try
                {
                    var port = new ForwardedPortRemote(rule.RemoteHost, (uint)rule.RemotePort, rule.LocalHost, (uint)rule.LocalPort);
                    client.AddForwardedPort(port);
                    port.Start();
                    _activeForwards[rule.Id] = port;
                    ForwardStatusChanged?.Invoke(rule.Id, true);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>停止转发</summary>
        public bool Stop(SshClient client, string ruleId)
        {
            lock (_lock)
            {
                ForwardedPort port;
                if (!_activeForwards.TryGetValue(ruleId, out port)) return false;
                try
                {
                    port.Stop();
                    client?.RemoveForwardedPort(port);
                    _activeForwards.Remove(ruleId);
                    ForwardStatusChanged?.Invoke(ruleId, false);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>检查端口是否可用</summary>
        public static bool IsPortAvailable(int port, string host = "127.0.0.1")
        {
            try
            {
                var listener = new TcpListener(IPAddress.Parse(host), port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch { return false; }
        }

        /// <summary>找一个可用端口</summary>
        public static int FindAvailablePort(int startFrom = 10000)
        {
            for (int port = startFrom; port < 65535; port++)
            {
                if (IsPortAvailable(port)) return port;
            }
            return -1;
        }

        public bool IsActive(string ruleId)
        {
            lock (_lock) { return _activeForwards.ContainsKey(ruleId); }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var kvp in _activeForwards)
                {
                    try { kvp.Value.Stop(); kvp.Value.Dispose(); } catch { }
                }
                _activeForwards.Clear();
            }
        }
    }
}
