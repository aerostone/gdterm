using System;
using System.Collections.Generic;
using System.IO;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;

namespace Gdterm.Tests.Connections
{
    public static class ConnectionStoreJsonTests
    {
        public static void Run()
        {
            Console.WriteLine("[test] ConnectionStoreJson");
            var dir = Path.Combine(Path.GetTempPath(), "gdterm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "connections.json");

            try
            {
                var store = new ConnectionStoreJson(path);

                // empty file → empty list
                var empty = store.LoadAll();
                Assert.Equal(0, empty.Count, "LoadAll empty when missing");

                var cfg = new ConnectionConfig
                {
                    Name = "lab-ssh",
                    Protocol = ProtocolType.SSH,
                    Host = "10.0.0.1",
                    Port = 22,
                    Username = "root",
                    GroupPath = "prod/web"
                };
                var added = store.Add(cfg);
                Assert.True(!string.IsNullOrEmpty(added.Id), "Add assigns Id");

                var loaded = store.LoadAll();
                Assert.Equal(1, loaded.Count, "one connection after Add");
                Assert.Equal("lab-ssh", loaded[0].Name, "name round-trip");
                Assert.Equal("10.0.0.1", loaded[0].Host, "host round-trip");
                Assert.Equal(ProtocolType.SSH, loaded[0].Protocol, "protocol round-trip");

                // no password field in JSON (credential ref only)
                var json = File.ReadAllText(path);
                Assert.NotContains(json, "Password", "connections.json must not contain Password key");
                Assert.NotContains(json, "password", "connections.json must not contain password key");

                var byId = store.GetById(added.Id);
                Assert.True(byId != null && byId.Host == "10.0.0.1", "GetById works");

                // metadata + serial round-trip（0.1.119 前 metadata 只写不读、serial 完全不落盘）
                added.Metadata = new Dictionary<string, string>
                {
                    { "notes", "备注含 \"引号\" 与 \\ 反斜杠" },
                    { "rdp_colordepth", "32" },
                    { "rdp_engine", "auto" }
                };
                added.Serial = new SerialConfig
                {
                    PortName = "COM3",
                    BaudRate = 115200,
                    DataBits = 8,
                    StopBits = System.IO.Ports.StopBits.One,
                    Parity = System.IO.Ports.Parity.None
                };
                store.Update(added);

                var reloaded = store.GetById(added.Id);
                Assert.True(reloaded != null, "reload after Update");
                Assert.True(reloaded.Metadata != null && reloaded.Metadata.Count == 3, "metadata survives save/load");
                Assert.Equal("32", reloaded.Metadata["rdp_colordepth"], "metadata value round-trip");
                Assert.True(reloaded.Serial != null && reloaded.Serial.PortName == "COM3"
                    && reloaded.Serial.BaudRate == 115200, "serial config survives save/load");

                store.Delete(added.Id);
                Assert.Equal(0, store.LoadAll().Count, "Delete removes connection");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
