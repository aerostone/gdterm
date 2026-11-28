using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gdterm.Connections;
using Gdterm.Core;
using Gdterm.Core.Models;
using Gdterm.Tunnel;

namespace Gdterm.UI.Services
{
    /// <summary>Application owner. Views may be replaced without releasing rules or running forwards.</summary>
    public sealed class PortForwardWorkspace : IDisposable
    {
        private readonly PortForwardRuleStore _store;
        private readonly Func<string, ISshPortForwardHost> _resolve;
        private readonly Func<IList<ConnectionConfig>> _connections;
        private readonly TrashStore _trash;
        private List<PortForwardRule> _rules;
        private readonly HashSet<string> _deleting = new HashSet<string>();
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();
        private bool _disposed;
        public PortForwardManager Manager { get; }
        public event EventHandler Changed;

        public PortForwardWorkspace(PortForwardRuleStore store, PortForwardManager manager,
            Func<string, ISshPortForwardHost> resolve, Func<IList<ConnectionConfig>> connections, TrashStore trash)
        {
            _store = store; Manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _resolve = resolve; _connections = connections; _trash = trash;
            _rules = _store?.Load() ?? new List<PortForwardRule>();
            Manager.ForwardStatusChanged += OnStatus;
            _trash?.RegisterRestorer("portforward", entry =>
            {
                var rule = entry.Payload as PortForwardRule;
                if (rule == null || _disposed) throw new InvalidOperationException(L10n.T("转发规则无法恢复"));
                if (_rules.Any(r => r.Id == rule.Id)) throw new InvalidOperationException(L10n.T("同名标识的转发规则已经存在"));
                Save(rule);
            });
        }
        public IList<PortForwardRule> Rules => _rules.Select(PortForwardManager.Copy).ToList();
        public IList<ConnectionConfig> Connections => _connections?.Invoke() ?? new List<ConnectionConfig>();
        public bool IsDeleting(string id) => _deleting.Contains(id);
        public string Error(string id) { string error; return _errors.TryGetValue(id, out error) ? error : Manager.GetStatus(id).Error; }
        public bool CanEdit(string id)
        {
            var state = Manager.GetStatus(id);
            return !_disposed && !IsDeleting(id) && !state.Busy && state.State != PortForwardState.Running;
        }
        public void Save(PortForwardRule rule)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PortForwardWorkspace));
            if (rule == null || string.IsNullOrEmpty(rule.Id)) throw new ArgumentException(nameof(rule));
            var error = rule.EndpointError();
            if (error != null) throw new ArgumentException(error);
            if (!CanEdit(rule.Id)) throw new InvalidOperationException(L10n.T("请先停止转发，再编辑规则"));
            var next = _rules.Select(PortForwardManager.Copy).ToList();
            int i = next.FindIndex(r => r.Id == rule.Id);
            if (i < 0) next.Add(PortForwardManager.Copy(rule)); else next[i] = PortForwardManager.Copy(rule);
            _store?.Save(next); // publish only after durable save
            _rules = next; _errors.Remove(rule.Id); Notify();
        }
        public async Task<bool> StartAsync(string id)
        {
            var rule = _rules.Find(r => r.Id == id);
            if (rule == null || _disposed || IsDeleting(id)) return false;
            _errors.Remove(id);
            if (string.IsNullOrEmpty(rule.ConnectionId))
            {
                _errors[id] = L10n.T("请编辑规则并选择所属 SSH 连接"); Notify(); return false;
            }
            try { return await Manager.StartAsync(_resolve?.Invoke(rule.ConnectionId), rule); }
            catch (Exception ex) { _errors[id] = ex.Message; Notify(); return false; }
        }
        public Task<bool> StopAsync(string id) { _errors.Remove(id); return Manager.StopAsync(id); }
        public async Task<bool> DeleteAsync(string id)
        {
            var rule = _rules.Find(r => r.Id == id);
            if (rule == null || _disposed || !_deleting.Add(id)) return false;
            Notify();
            try
            {
                if (!await Manager.StopAsync(id)) throw new InvalidOperationException(Manager.GetStatus(id).Error);
                if (_disposed) return false;
                var next = _rules.Where(r => r.Id != id).Select(PortForwardManager.Copy).ToList();
                _store?.Save(next);
                _rules = next;
                _trash?.Add("portforward", L10n.T("端口转发"), rule.Name ?? rule.Id, PortForwardManager.Copy(rule));
                _errors.Remove(id);
                return true;
            }
            catch (Exception ex) { _errors[id] = ex.Message; return false; }
            finally { _deleting.Remove(id); Notify(); }
        }
        private void OnStatus(string id, bool active) { Notify(); }
        private void Notify() { if (!_disposed) Changed?.Invoke(this, EventArgs.Empty); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Manager.ForwardStatusChanged -= OnStatus;
            Manager.Dispose();
        }
    }
}
