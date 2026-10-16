param(
    [string]$Baseline = 'ac89d00',
    [string]$OutputDirectory = '',
    [string]$PackagePath = '',
    [switch]$VersionOnly,
    [string]$TemporaryRoot = ([IO.Path]::GetTempPath())
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) {
    $testId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/update-tests/legacy-$testId"
}
$evidenceRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$testRoot = Join-Path ([IO.Path]::GetFullPath($TemporaryRoot)) ('mikun2n-legacy-update-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$gitInfo = [Diagnostics.ProcessStartInfo]::new('git')
$gitInfo.WorkingDirectory = $repositoryRoot
$gitInfo.UseShellExecute = $false
$gitInfo.RedirectStandardOutput = $true
$gitInfo.ArgumentList.Add('show')
$gitInfo.ArgumentList.Add("${Baseline}:Services/UpdateService.cs")
$gitProcess = [Diagnostics.Process]::Start($gitInfo)
$original = $gitProcess.StandardOutput.ReadToEnd()
$gitProcess.WaitForExit()
if ($gitProcess.ExitCode -ne 0) { throw 'Cannot read baseline updater source.' }
$sourceSnapshot = Join-Path $evidenceRoot 'UpdateService.before.cs.txt'
[IO.File]::WriteAllText($sourceSnapshot, $original, $utf8)
$needle = 'private static readonly HttpClient Http = CreateClient();'
if (($original.Split($needle).Length - 1) -ne 1) { throw 'Expected exactly one HTTP injection point.' }
$injected = $original.Replace($needle, 'private static readonly HttpClient Http = OfflineTransport.Client;')
[IO.File]::WriteAllText((Join-Path $testRoot 'UpdateService.cs'), $injected, $utf8)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LegacyUpdateTests.cs.txt') -Destination (Join-Path $testRoot 'Program.cs')
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <InformationalVersion>99.0.0-2</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>
  <ItemGroup><AssemblyMetadata Include="UpdateRepository" Value="fixture/updater" /></ItemGroup>
</Project>
'@
[IO.File]::WriteAllText((Join-Path $testRoot 'LegacyUpdaterTests.csproj'), $project, $utf8)
$sourceHash = (Get-FileHash -LiteralPath $sourceSnapshot -Algorithm SHA256).Hash.ToLowerInvariant()
$injectedHash = (Get-FileHash -LiteralPath (Join-Path $testRoot 'UpdateService.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
$details = [ordered]@{
    baseline = $Baseline
    sourceSha256 = $sourceHash
    injectedSourceSha256 = $injectedHash
    injection = "$needle -> private static readonly HttpClient Http = OfflineTransport.Client;"
    testRoot = $testRoot
    package = $PackagePath
    originalBinaryEndToEnd = $false
    realClientOrTunnelStarted = $false
    realHttpRequests = $false
    versionOnly = [bool]$VersionOnly
    fixtureVersion = '99.0.0-2'
    productionProjectVersionChanged = $false
}
$details | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'legacy-validation.json') -Encoding utf8
$log = Join-Path $evidenceRoot 'legacy-tests.log'
"Baseline=$Baseline SourceSHA256=$sourceHash TestRoot=$testRoot`nOnly production-source change: $($details.injection)" | Set-Content -LiteralPath $log -Encoding utf8
$testArguments = @($testRoot, $(if ($VersionOnly) { '--version-only' } else { $PackagePath }))
& dotnet run --project (Join-Path $testRoot 'LegacyUpdaterTests.csproj') -- $testArguments 2>&1 |
    Tee-Object -FilePath $log -Append
if ($LASTEXITCODE -ne 0) { throw "Legacy updater tests failed ($LASTEXITCODE); see $log" }
$details['passed'] = $true
$details | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'legacy-validation.json') -Encoding utf8
Write-Output "LEGACY EVIDENCE: $evidenceRoot"
