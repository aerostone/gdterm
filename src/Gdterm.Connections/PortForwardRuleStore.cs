using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using Gdterm.Core.Models;

namespace Gdterm.Connections
{
    /// <summary>Portable rules only; runtime state and SSH credentials are never serialized.</summary>
    public sealed class PortForwardRuleStore
    {
        private readonly string _path;
        public PortForwardRuleStore(string path) { _path = path ?? throw new ArgumentNullException(nameof(path)); }
        public List<PortForwardRule> Load()
        {
            if (!File.Exists(_path)) return new List<PortForwardRule>();
            using (var input = File.OpenRead(_path))
            {
                var rules = (List<PortForwardRule>)new DataContractJsonSerializer(typeof(List<PortForwardRule>)).ReadObject(input);
                Validate(rules);
                return rules;
            }
        }
        public void Save(List<PortForwardRule> rules)
        {
            Validate(rules);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            Directory.CreateDirectory(dir);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(List<PortForwardRule>)).WriteObject(output, rules);
                    output.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static void Validate(List<PortForwardRule> rules)
        {
            if (rules == null) throw new InvalidDataException("Invalid port forwarding rules.");
            var ids = new HashSet<string>();
            foreach (var r in rules)
                if (r == null || string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id) || r.EndpointError() != null)
                    throw new InvalidDataException("Invalid or duplicate port forwarding rule.");
        }
    }
}
