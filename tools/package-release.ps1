<#
.SYNOPSIS
Builds uniquely identified local test packages, or explicitly approved release packages.

.DESCRIPTION
Default runs keep the source version and add a test identity. Pass -ExpectedVersion
only after release approval to emit canonical release assets and update.json.
Existing artifacts are never overwritten. -PlanOnly checks identities without building.
#>
param(
    [string]$Output = '',
    [string]$ExpectedVersion = '',
    [string]$Notes = '',
    [string]$TestId = '',
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))

$project = Join-Path $root 'MikuN2N.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$baseVersion = ($xml.Project.PropertyGroup | ForEach-Object { $_.BaseVersion } | Where-Object { $_ } | Select-Object -First 1)
$buildNumber = ($xml.Project.PropertyGroup | ForEach-Object { $_.BuildNumber } | Where-Object { $_ } | Select-Object -First 1)
$version = "$baseVersion-$buildNumber"
if ($version -notmatch '^\d+\.\d+\.\d+-\d+$') { throw 'Invalid project release version' }
if ($ExpectedVersion -and $ExpectedVersion -ne $version) {
    throw "Tag $ExpectedVersion does not match project version $version"
}
$release = -not [string]::IsNullOrWhiteSpace($ExpectedVersion)
if ($release -and $TestId) { throw 'TestId is only for local test packages' }
$revision = (& git -C $root rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit' }
$dirty = [bool](& git -C $root status --porcelain --untracked-files=no)
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-g' + $revision + $(if ($dirty) { '-dirty' }) + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
if (-not $release -and -not $TestId) { $TestId = $runId }
if ($TestId -and $TestId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$') { throw 'TestId must be a short filename-safe identifier' }
$identity = if ($release) { $version } else { "$version+test.$TestId" }
if (-not $Output) { $Output = if ($release) { 'dist' } else { "artifacts/test-packages/$TestId" } }
$repository = ($xml.SelectSingleNode('/Project/PropertyGroup/UpdateRepository')).InnerText
if ($release -and $repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'UpdateRepository must be a GitHub owner/name' }
$pageUrl = "https://github.com/$repository/releases/tag/$version"
$downloadUrl = "https://github.com/$repository/releases/download/$version"
$outputDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }))
$names = foreach ($runtime in @('self-contained', 'framework-dependent')) {
    $suffix = if ($runtime -eq 'framework-dependent') { '-framework-dependent' } else { '' }
    $testSuffix = if ($release) { '' } else { "-test-$TestId" }
    "MikuN2N-$version$testSuffix-win-x64$suffix.zip"
}
$metadataName = if ($release) { 'update.json' } else { 'test-build.json' }
foreach ($name in @($names) + @('SHA256SUMS', $metadataName)) {
    if (Test-Path -LiteralPath (Join-Path $outputDirectory $name)) { throw "Refusing to overwrite existing artifact: $name" }
}
$reportDirectory = Join-Path $root "artifacts/package-checks/$runId"
$plan = [ordered]@{ mode = $(if ($release) { 'release' } else { 'test' }); sourceVersion = $version; informationalVersion = $identity; sourceCommit = $revision; sourceDirty = $dirty; testId = $TestId; output = $outputDirectory; packages = @($names); metadata = $metadataName; reports = $reportDirectory; updateRepository = $(if ($release) { $repository } else { 'none' }) }
if ($PlanOnly) { $plan | ConvertTo-Json -Depth 4; return }
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null

# GPLv3 requires the shipped archive to be the corresponding source of the shipped edge;
# the tracked native tree is that source, so refuse to package an archive that drifted.
$nativeReport = Join-Path $reportDirectory 'native-source-validation.json'
python (Join-Path $PSScriptRoot 'package-native-source.py') --validate-only `
    --source (Join-Path $root 'native/n3n-3.4.4') `
    --archive (Join-Path $root 'Runtime/n3n-3.4.4-source.zip') --report $nativeReport
if ($LASTEXITCODE -ne 0) {
    throw 'Runtime/n3n-3.4.4-source.zip does not match native/n3n-3.4.4; regenerate it with tools/package-native-source.py'
}

$required = @(
    'MikuN2N.exe', 'LICENSE.txt', 'build-identity.txt', 'PRIVACY.md', 'UNINSTALL.md', 'CODE_SIGNING.md',
    'Runtime/n3n-edge.exe', 'Runtime/n3n-3.4.4-source.zip', 'Runtime/tap-windows-installer.exe',
    'Runtime/LICENSE-n2n.txt', 'Runtime/LICENSE-tap-windows.txt', 'Runtime/LICENSE-dotnet.txt',
    'Runtime/THIRD-PARTY-NOTICES.txt', 'Runtime/THIRD-PARTY-NOTICES-dotnet.txt', 'Runtime/README.txt',
    'Runtime/README.md', 'Runtime/README.zh.md', 'Runtime/BUILD.md', 'Runtime/BUILD.zh.md'
)
$required += Get-ChildItem -LiteralPath (Join-Path $root 'Runtime/changes') -Filter '*.md' -File |
    ForEach-Object { "Runtime/changes/$($_.Name)" }
# Debug paths and build scripts can embed a user profile path. Use the same rule as
# tools/package-native-source.py and look inside archives too (the native source zip).
$personalPath = [regex]::new('(?:[a-z]:[\\/]+users[\\/]+(?!public[\\/]|default[\\/])[^\\/\s"<>|*?]+|/home/[^/\s]+/|/Users/[^/\s]+/)', 'IgnoreCase')
function Find-PersonalPath([byte[]]$Bytes) {
    # Latin-1 maps every byte to one char; dropping NULs also exposes UTF-16 text.
    $text = [Text.Encoding]::Latin1.GetString($Bytes)
    foreach ($candidate in @($text, $text.Replace("`0", ''))) {
        $match = $personalPath.Match($candidate)
        if ($match.Success) { return $match.Value }
    }
    return $null
}
Add-Type -AssemblyName System.IO.Compression
function Test-Package([string]$Publish) {
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Publish $file) -PathType Leaf)) { throw "Package is missing $file" }
    }
    # The allowlist also rejects loose assemblies, stale outputs and private profiles.
    foreach ($file in Get-ChildItem -LiteralPath $Publish -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($Publish, $file.FullName).Replace('\', '/')
        if ($relative -notin $required) { throw "Unexpected package file: $relative" }
        if ($relative -ne 'MikuN2N.exe' -and $relative -ne 'build-identity.txt') {
            $source = if ($relative -eq 'LICENSE.txt') { Join-Path $root 'LICENSE' } else { Join-Path $root $relative }
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $source).Hash) {
                throw "Published content differs from source: $relative"
            }
        }
    }
    $productVersion = (Get-Item -LiteralPath (Join-Path $Publish 'MikuN2N.exe')).VersionInfo.ProductVersion
    if ($productVersion -ne $identity) { throw "MikuN2N.exe reports $productVersion, expected $identity" }
    if ((Get-Content -LiteralPath (Join-Path $Publish 'build-identity.txt') -Raw).Trim() -ne $identity) {
        throw 'Published build identity does not match project version'
    }
    foreach ($file in Get-ChildItem -LiteralPath $Publish -File -Recurse) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        if ($file.Extension -eq '.zip') {
            $archive = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($bytes))
            try {
                foreach ($entry in $archive.Entries) {
                    $stream = $entry.Open(); $buffer = [IO.MemoryStream]::new()
                    try {
                        $stream.CopyTo($buffer)
                        if (Find-PersonalPath $buffer.ToArray()) { throw "$($file.Name)!$($entry.FullName) contains a personal path" }
                    } finally { $stream.Dispose(); $buffer.Dispose() }
                }
            } finally { $archive.Dispose() }
        } elseif (Find-PersonalPath $bytes) {
            throw "$($file.Name) contains a personal path"
        }
    }
}

$packages = @()
$checksums = @()
$sizeReports = @()
foreach ($runtime in @('self-contained', 'framework-dependent')) {
    $publish = [IO.Path]::GetFullPath((Join-Path $root "publish/runs/$runId/$runtime"))
    if (Test-Path -LiteralPath $publish) { throw 'Publish run directory already exists' }
    $selfContained = ($runtime -eq 'self-contained').ToString().ToLowerInvariant()
    $updateRepository = if ($release) { $repository } else { 'none' }
    dotnet publish $project -c Release -r win-x64 --self-contained $selfContained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false -p:UpdateRuntime=$runtime `
        -p:InformationalVersion=$identity -p:UpdateRepository=$updateRepository -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($runtime) failed with exit code $LASTEXITCODE" }
    Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -File -Recurse | Remove-Item -Force
    Test-Package $publish

    $suffix = if ($runtime -eq 'framework-dependent') { '-framework-dependent' } else { '' }
    $testSuffix = if ($release) { '' } else { "-test-$TestId" }
    $zipName = "MikuN2N-$version$testSuffix-win-x64$suffix.zip"
    $zipPath = Join-Path $outputDirectory $zipName
    if (Test-Path -LiteralPath $zipPath) { throw "Refusing to overwrite existing artifact: $zipName" }
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zipPath, [IO.Compression.CompressionLevel]::SmallestSize, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $zipName"
    $packageInfo = [ordered]@{
        runtime = $runtime
        name = $zipName
        size = (Get-Item -LiteralPath $zipPath).Length
        sha256 = $hash
    }
    if ($release) { $packageInfo['url'] = "$downloadUrl/$zipName" }
    $packages += $packageInfo
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object {
            [ordered]@{ path = $_.FullName; bytes = $_.Length; compressedBytes = $_.CompressedLength }
        })
        $sizeReports += [ordered]@{
            runtime = $runtime
            name = $zipName
            zipBytes = (Get-Item -LiteralPath $zipPath).Length
            expandedBytes = ($entries | ForEach-Object { $_.bytes } | Measure-Object -Sum).Sum
            exeBytes = (Get-Item -LiteralPath (Join-Path $publish 'MikuN2N.exe')).Length
            files = $entries
        }
    } finally { $archive.Dispose() }
    Write-Host "Packaged $zipName ($((Get-Item -LiteralPath $zipPath).Length) bytes)"
}
[IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS'), ($checksums -join "`n") + "`n")
if ($release) {
    $manifest = [ordered]@{ schema = 1; version = $version; notes = $Notes; pageUrl = $pageUrl; packages = $packages }
} else {
    $manifest = [ordered]@{ mode = 'test'; sourceVersion = $version; informationalVersion = $identity; sourceCommit = $revision; sourceDirty = $dirty; testId = $TestId; packages = $packages }
}
[IO.File]::WriteAllText((Join-Path $outputDirectory $metadataName), ($manifest | ConvertTo-Json -Depth 5) + "`n")
$sizeReport = [ordered]@{
    mode = $plan.mode
    version = $version
    informationalVersion = $identity
    sdk = (& dotnet --version)
    configuration = 'Release'
    runtimeIdentifier = 'win-x64'
    satelliteResourceLanguages = 'zh-Hans'
    singleFileCompression = $false
    zipCompression = 'SmallestSize'
    packages = $sizeReports
}
[IO.File]::WriteAllText((Join-Path $reportDirectory 'package-size-report.json'), ($sizeReport | ConvertTo-Json -Depth 6) + "`n")
Write-Host "Wrote SHA256SUMS and $metadataName; reports: $reportDirectory"
