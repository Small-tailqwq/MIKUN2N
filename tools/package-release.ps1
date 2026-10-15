<#
.SYNOPSIS
Builds both Windows x64 release packages and their static update manifest.

.DESCRIPTION
Publishes self-contained and framework-dependent single-file builds, verifies the
corresponding native source and licenses, and writes two ZIPs, SHA256SUMS and
update.json into -Output. Run with PowerShell 7 and the .NET 9 SDK on Windows.
#>
param(
    [string]$Output = 'dist',
    [string]$ExpectedVersion = '',
    [string]$Notes = ''
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
$repository = ($xml.SelectSingleNode('/Project/PropertyGroup/UpdateRepository')).InnerText
if ($repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'UpdateRepository must be a GitHub owner/name' }
$pageUrl = "https://github.com/$repository/releases/tag/$version"
$downloadUrl = "https://github.com/$repository/releases/download/$version"
$outputDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }))
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

# GPLv3 requires the shipped archive to be the corresponding source of the shipped edge;
# the tracked native tree is that source, so refuse to package an archive that drifted.
$nativeReport = Join-Path $root "artifacts/$version/native-source-validation.json"
python (Join-Path $PSScriptRoot 'package-native-source.py') --validate-only `
    --source (Join-Path $root 'native/n3n-3.4.4') `
    --archive (Join-Path $root 'Runtime/n3n-3.4.4-source.zip') --report $nativeReport
if ($LASTEXITCODE -ne 0) {
    throw 'Runtime/n3n-3.4.4-source.zip does not match native/n3n-3.4.4; regenerate it with tools/package-native-source.py'
}

$required = @(
    'MikuN2N.exe', 'LICENSE.txt', 'build-identity.txt',
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
    if ($productVersion -ne $version) { throw "MikuN2N.exe reports $productVersion, expected $version" }
    if ((Get-Content -LiteralPath (Join-Path $Publish 'build-identity.txt') -Raw).Trim() -ne $version) {
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
    $publish = [IO.Path]::GetFullPath((Join-Path $root "publish/release-$version-$runtime"))
    # Resolve and bound the deletion to this checkout's publish tree before removing anything.
    $publishRoot = [IO.Path]::GetFullPath((Join-Path $root 'publish')) + [IO.Path]::DirectorySeparatorChar
    if (-not $publish.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish directory escapes checkout' }
    if ((Test-Path -LiteralPath $publishRoot) -and (Get-Item -LiteralPath $publishRoot).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to publish through a redirected publish root'
    }
    if (Test-Path -LiteralPath $publish) {
        $resolved = (Resolve-Path -LiteralPath $publish).Path
        if ($resolved -ne $publish -or (Get-Item -LiteralPath $publish).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to remove a redirected publish directory'
        }
        if (Get-ChildItem -LiteralPath $publish -Directory -Recurse | Where-Object { $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) }) {
            throw 'Refusing to remove a publish directory containing redirected subdirectories'
        }
        Remove-Item -LiteralPath $publish -Recurse -Force
    }
    $selfContained = ($runtime -eq 'self-contained').ToString().ToLowerInvariant()
    dotnet publish $project -c Release -r win-x64 --self-contained $selfContained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false -p:UpdateRuntime=$runtime -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($runtime) failed with exit code $LASTEXITCODE" }
    Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -File -Recurse | Remove-Item -Force
    Test-Package $publish

    $suffix = if ($runtime -eq 'framework-dependent') { '-framework-dependent' } else { '' }
    $zipName = "MikuN2N-$version-win-x64$suffix.zip"
    $zipPath = Join-Path $outputDirectory $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $zipPath, [IO.Compression.CompressionLevel]::SmallestSize, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksums += "$hash  $zipName"
    $packages += [ordered]@{
        runtime = $runtime
        name = $zipName
        url = "$downloadUrl/$zipName"
        size = (Get-Item -LiteralPath $zipPath).Length
        sha256 = $hash
    }
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
$manifest = [ordered]@{ schema = 1; version = $version; notes = $Notes; pageUrl = $pageUrl; packages = $packages }
[IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS'), ($checksums -join "`n") + "`n")
[IO.File]::WriteAllText((Join-Path $outputDirectory 'update.json'), ($manifest | ConvertTo-Json -Depth 5) + "`n")
$sizeReport = [ordered]@{
    version = $version
    sdk = (& dotnet --version)
    configuration = 'Release'
    runtimeIdentifier = 'win-x64'
    satelliteResourceLanguages = 'zh-Hans'
    singleFileCompression = $false
    zipCompression = 'SmallestSize'
    packages = $sizeReports
}
[IO.File]::WriteAllText((Join-Path $root "artifacts/$version/package-size-report.json"), ($sizeReport | ConvertTo-Json -Depth 6) + "`n")
Write-Host 'Wrote SHA256SUMS and update.json'
