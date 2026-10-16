# Connected layout fixture

From the repository root, build `dotnet build tools/connection-layout-tests/ConnectionLayoutTests.csproj`.
Run `dotnet tools/connection-layout-tests/bin/Debug/net9.0-windows/ConnectionLayoutTests.dll . <output-directory>`.

The fixture compiles the real MainWindow XAML and `MainWindow.ConnectionLayout.cs`.
It uses an isolated App, synthetic connection states, reserved example addresses,
and stubs for unrelated handlers. The compact disconnect action calls a fake shared
handler; no EdgeController, real settings, single-instance mutex, tray or network
setup is constructed. Windows stay offscreen with `ShowActivated=false`.

Checks cover first success, polling, reconnect, manual expansion/collapse, terminal
error, disconnect, later sessions, core identity/metrics, longer full versions at
minimum width, and the About-only GitHub privacy link. Screenshots cover both themes.
This tests layout and event wiring, not a live tunnel or the complete MainWindow startup.
