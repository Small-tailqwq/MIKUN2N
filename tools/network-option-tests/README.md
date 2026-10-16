# Network option checks

From the repository root:

```powershell
dotnet build tools/network-option-tests/NetworkOptionTests.csproj
dotnet tools/network-option-tests/bin/Debug/net9.0-windows/NetworkOptionTests.dll . <output-directory>
```

The fixture uses the production model, JSON serialization and native configuration
builder. It invokes only the disabled UPnP early-return path, on objects created
without the EdgeController constructor. The enabled network setup path is never run.
The managed scheduling gate and native metric-0 behavior are checked against source.
There is no application startup, personal settings access, edge launch or system
network change. Live Windows/router behavior is outside these checks.

For diagnostic disclosure metadata only, run the offline fixture with
`--policy-only <empty-output-directory>`; it uses the real recorder with consent
disabled, a forbidden transport factory and temporary files. It does not run the
full rolling-file or updater suites.
