# Tray exit regression fixture

Policy UI checks: run the fixture with `--policy-ui <repository-root> <output-directory>`.
These compile the real Settings and consent windows; main-window XAML is previewed with handlers removed.


From the repository root, build `dotnet build tools/tray-exit-tests/TrayExitTests.csproj`.
Run `dotnet tools/tray-exit-tests/bin/Debug/net9.0-windows/TrayExitTests.dll <scenario> <repository-root> <empty-output-directory>`.

Scenarios: `clean-settings`, `about`, `dirty-save`, `dirty-discard`, `dirty-cancel`,
`invalid-save`, `connected-cancel`, `connected-exit`, `cleanup-failure`,
`nested-dialog`, `repeat-exit`. Each run writes a JSON result.

The fixture compiles the production `MainWindow.Exit.cs` and Settings window XAML
and code behind. It runs real WPF modal frames and Application shutdown with an
isolated App, fake edge and tray, controlled prompt choices, and simulated settings
application completion. Windows stay offscreen with `ShowActivated=false`.
It does not execute the production App startup, create its mutex or activation
event, read/write personal settings, register Shell notifications, or use network.
It verifies control flow and cleanup ordering, not a live connection or real Shell clicks.
