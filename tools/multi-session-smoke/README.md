# Multi-session command regression

Run python3 tools/multi-session-smoke/run.py with .NET 8 and OpenSSH installed.
Uses a private loopback sshd and temporary keys; does not change system SSH configuration.
GDT_NUGET_SOURCE selects an existing package cache. GDT_DOTNET overrides dotnet.

Links production executor, broadcast manager, recorder, readiness detector and interface.
Pure behavior cases also run in Gdterm.Tests on net462. Real SSH cases cover stdout/stderr,
exit status, Unicode/multiline, cancellation, timeout, disconnected targets and client reuse.
The connection/credential DTO stubs are unused in these paths. This does not prove native UI
rendering: Windows UiSmoke/MultiSessionUiSmoke and the page matrix cover that separately.
