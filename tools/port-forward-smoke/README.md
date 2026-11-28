# Port forwarding regression

Run `python3 tools/port-forward-smoke/run.py` with .NET 8 and OpenSSH available.
Use `GDT_NUGET_SOURCE` for an offline package directory and `GDT_DOTNET` for an explicit SDK path.

The fixture generates temporary SSH keys and starts a loopback-only sshd with its own configuration. It does not change the system SSH service, authorized_keys or known_hosts. On OpenSSH versions supporting PerSourcePenalties, that option is disabled only in this fixture because data probes intentionally read an SSH banner without authenticating.

Production `PortForwardManager`, `PortForwardRuleStore`, `PortForwardRule` and `TabActiveSessionQuery` sources are compiled directly. Local, remote and SOCKS5 listeners transfer real TCP data. Checks cover original SSH ownership, disconnected/replaced clients, batch start/stop, pending startup cancellation, disposal races, validation and persistence.

`SessionQueryFixture.cs` replaces WinForms containers and unrelated terminal/tool interfaces so the production session resolver can run on Linux against real SSH clients. These resolver checks do not constitute Windows UI execution. Windows form submission, persistence failures, batch selection, panel replacement, trash restoration and layout are exercised separately by `PortForwardUiSmoke` and `UiPageCatalog` in Gdterm.Tests.
