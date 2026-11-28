using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Gdterm.Connections;
using Gdterm.Core.Enums;
using Gdterm.Core.Models;
using Gdterm.KeePass.Models;
using Gdterm.UI.Controls;
using Gdterm.UI.Forms;

namespace Gdterm.Tests.Ui
{
    internal static class JumpChainSmoke
    {
        private static T Field<T>(object obj, string name) where T : class =>
            (T)obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);

        internal static ConnectionDialog Scene(bool empty = false, ProtocolType protocol = ProtocolType.SSH)
        {
            var vault = new FakeKeePassService(empty: true);
            var key = vault.CreateEntry(new KeePassEntry { Title = "production-jump-key", Username = "jumpuser",
                SshPrivateKeyData = Encoding.UTF8.GetBytes(Security.SshKeyFixtures.OpenSsh) });
            var cfg = new ConnectionConfig { Host = "target.example.internal", Port = protocol == ProtocolType.RDP ? 3389 : 22,
                Protocol = protocol, JumpChain = new JumpChainConfig { Hops = new List<JumpHop>() } };
            if (!empty)
            {
                cfg.JumpChain.Hops.Add(new JumpHop { Host = "bastion.example.internal", Port = 22, CredentialRefId = key.Id });
                cfg.JumpChain.Hops.Add(new JumpHop { Host = "inner.example.internal", Port = 22, CredentialRefId = key.Id, Username = "admin" });
            }
            var form = new ConnectionDialog(cfg, vault);
            form.Shown += (sender, args) => {
                Application.DoEvents();
                var chain = Field<JumpChainEditorControl>(form, "_jumpChainEditor");
                var body = Field<Panel>(form, "_bodyScroll");
                body.ScrollControlIntoView(chain);
                Application.DoEvents();
                var add = Field<AntdUI.Button>(chain, "_addButton");
                if (!body.RectangleToScreen(body.ClientRectangle).Contains(add.RectangleToScreen(add.ClientRectangle)))
                    throw new InvalidOperationException("Jump actions must be reachable inside the scrolling body");
                if (!empty && Field<AntdUI.Table>(chain, "_table").RowCount != 2)
                    throw new InvalidOperationException("Configured jump rows did not render");
            };
            return form;
        }

        internal static void Run(string dir, Action<bool, string> check)
        {
            var path = Path.Combine(dir, "jump-key");
            File.WriteAllText(path, Security.SshKeyFixtures.EncryptedOpenSsh, new UTF8Encoding(false));
            foreach (bool save in new[] { false, true })
            {
                var vault = new FakeKeePassService(empty: true);
                var original = new ConnectionConfig { Id = "jump-edit", Host = "target.test", Port = 22, Protocol = ProtocolType.SSH };
                ConnectionConfig result;
                using (var form = new ConnectionDialog(original, vault))
                {
                    form.Show(); Application.DoEvents();
                    form.GetType().GetMethod("ToggleAdvanced", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                    var chain = Field<JumpChainEditorControl>(form, "_jumpChainEditor");
                    chain.UseJumpChain = true; Application.DoEvents();
                    Field<Panel>(form, "_bodyScroll").ScrollControlIntoView(chain);
                    Application.DoEvents();
                    (form.AcceptButton as AntdUI.Button).PerformClick();
                    check(form.Result == null && Field<AntdUI.Label>(form, "_errorLabel").Visible, "enabled-empty-chain-blocks-save");
                    AddHop(form, chain, "first.test", false, path, check);
                    AddHop(form, chain, "second.test", true, path, check);
                    check(vault.CreatedCount == 0 && chain.GetChain().Hops.Count == 2, "hop credentials remain drafts until connection save");
                    check(Field<AntdUI.Table>(chain, "_table").RowCount == 2, "two hop rows actually render in table");
                    Field<AntdUI.Table>(chain, "_table").SelectedIndex = 2;
                    Field<AntdUI.Button>(chain, "_upButton").PerformClick();
                    check(chain.GetChain().Hops[0].Host == "second.test", "move-up changes hop order");
                    Field<AntdUI.Button>(chain, "_downButton").PerformClick();
                    check(chain.GetChain().Hops[0].Host == "first.test", "move-down restores hop order");
                    // Remove/re-add exercises draft deletion and real nested key editor a second time.
                    Field<AntdUI.Table>(chain, "_table").SelectedIndex = 2;
                    chain.GetType().GetMethod("UpdateActions", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chain, null);
                    Field<AntdUI.Button>(chain, "_removeButton").PerformClick();
                    check(chain.GetChain().Hops.Count == 1 && vault.CreatedCount == 0, "remove-hop discards pending credential");
                    AddHop(form, chain, "second.test", true, path, check);
                    if (save)
                    {
                        Field<AntdUI.Input>(form, "_loginPassword").Text = "TargetPassword!5839";
                        vault.FailCreateAt = 3;
                        (form.AcceptButton as AntdUI.Button).PerformClick();
                        check(form.Result == null && vault.CreatedCount == 0 && chain.GetChain().Hops.Count == 2,
                            "partial hop write failure rolls back created entries and retains drafts");
                        vault.FailCreateAt = 0;
                    }
                    ((save ? form.AcceptButton : form.CancelButton) as AntdUI.Button).PerformClick();
                    Application.DoEvents(); result = form.Result;
                    check(original.JumpChain == null && original.Host == "target.test", "editing never mutates original connection");
                    if (!save) check(result == null && vault.CreatedCount == 0, "cancel outer connection discards all hop credentials");
                    else
                    {
                        check(result?.JumpChain?.Hops.Count == 2 && vault.CreatedCount == 3, "retry saves target and both hop credentials exactly once");
                        check(vault.GetEntry(result.CredentialRefId).Password == "TargetPassword!5839", "failed hop save restores target password draft");
                        var hops = result.JumpChain.Hops;
                        check(vault.GetEntry(hops[0].CredentialRefId).Password == "JumpPassword!5839", "password hop binds its own credential");
                        check(Encoding.UTF8.GetString(vault.GetEntry(hops[1].CredentialRefId).SshPrivateKeyData) == Security.SshKeyFixtures.EncryptedOpenSsh,
                            "key hop binds its own encrypted private key");
                    }
                }
                if (!save || result == null) continue;
                {
                    var store = new ConnectionStoreJson(Path.Combine(dir, "jump-connections.json"));
                    var added = store.Add(result);
                    using (var edit = new ConnectionDialog(added, vault))
                    {
                        edit.Show(); Application.DoEvents();
                        Field<AntdUI.Input>(edit, "_nameBox").Text = "renamed chain";
                        (edit.AcceptButton as AntdUI.Button).PerformClick();
                        check(edit.Result != null && edit.Result.JumpChain.Hops.Count == 2, "rename retains complete chain");
                        store.Update(edit.Result);
                    }
                    {
                        var disk = new ConnectionStoreJson(Path.Combine(dir, "jump-connections.json"));
                        var saved = disk.GetById(added.Id);
                        check(saved.Name == "renamed chain" && saved.JumpChain.Hops[1].Host == "second.test"
                            && saved.JumpChain.Hops[1].CredentialRefId == result.JumpChain.Hops[1].CredentialRefId, "reopen disk retains hop order and credential references");
                    }
                }
            }
            File.Delete(path);
        }

        private static void AddHop(Form owner, JumpChainEditorControl chain, string host, bool key, string keyPath, Action<bool, string> check)
        {
            Exception failure = null;
            owner.BeginInvoke((Action)(() => {
                JumpHopEditForm hop = null;
                foreach (Form f in Application.OpenForms) if (f is JumpHopEditForm) hop = (JumpHopEditForm)f;
                try
                {
                    if (hop == null) throw new Exception("Jump host editor did not open");
                    Field<AntdUI.Input>(hop, "_hostBox").Text = host;
                    Field<AntdUI.Input>(hop, "_userBox").Text = key ? "keyuser" : "passworduser";
                    hop.BeginInvoke((Action)(() => {
                        KeePassEntryEditForm credential = null;
                        foreach (Form f in Application.OpenForms) if (f is KeePassEntryEditForm) credential = (KeePassEntryEditForm)f;
                        try
                        {
                            if (credential == null) throw new Exception("Jump credential editor did not open");
                            check(credential.EntryHostname == host, "jump credential editor inherits jump host");
                            if (key)
                            {
                                credential.SelectPrivateKeyAuthentication(); credential.ImportPrivateKey(keyPath);
                                Field<AntdUI.Input>(credential, "_keyPassphraseBox").Text = Security.SshKeyFixtures.Passphrase;
                            }
                            else Field<AntdUI.Input>(credential, "_passwordBox").Text = "JumpPassword!5839";
                            (credential.AcceptButton as AntdUI.Button).PerformClick();
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { if (credential != null && credential.DialogResult != DialogResult.OK) credential.Close(); }
                    }));
                    Field<AntdUI.Button>(hop, "_newButton").PerformClick();
                    if (failure != null) throw failure;
                    (hop.AcceptButton as AntdUI.Button).PerformClick();
                }
                catch (Exception ex) { failure = ex; }
                finally { if (hop != null && hop.DialogResult != DialogResult.OK) hop.Close(); }
            }));
            Field<AntdUI.Button>(chain, "_addButton").PerformClick();
            if (failure != null) throw failure;
        }
    }
}
