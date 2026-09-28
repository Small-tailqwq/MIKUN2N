<#
.SYNOPSIS
Builds the self-contained release package that the in-app updater installs.

.DESCRIPTION
Publishes MikuN2N for win-x64, checks that the package carries the patched edge,
its corresponding source and every bundled license, and writes
MikuN2N-<version>-win-x64.zip plus SHA256SUMS into -Output. Upload both files to the
GitHub release named after the version tag; the updater refuses packages without a
checksum or whose MikuN2N.exe reports a different version than the tag.
#>
param(
    [string]$Output = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'MikuN2N.csproj'
[xml]$xml = Get-Content -LiteralPath $project -Raw
$baseVersion = ($xml.Project.PropertyGroup | ForEach-Object { $_.BaseVersion } | Where-Object { $_ } | Select-Object -First 1)
$buildNumber = ($xml.Project.PropertyGroup | ForEach-Object { $_.BuildNumber } | Where-Object { $_ } | Select-Object -First 1)
$version = "$baseVersion-$buildNumber"

# GPLv3 requires the shipped archive to be the corresponding source of the shipped edge;
# the tracked native tree is that source, so refuse to package an archive that drifted.
$nativeReport = Join-Path $root "artifacts/$version/native-source-validation.json"
python (Join-Path $PSScriptRoot 'package-native-source.py') --validate-only `
    --source (Join-Path $root 'native/n3n-3.4.4') `
    --archive (Join-Path $root 'Runtime/n3n-3.4.4-source.zip') --report $nativeReport
if ($LASTEXITCODE -ne 0) {
    throw 'Runtime/n3n-3.4.4-source.zip does not match native/n3n-3.4.4; regenerate it with tools/package-native-source.py'
}

$publish = Join-Path $root "publish/release-$version"
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Get-ChildItem -LiteralPath $publish -Filter '*.pdb' -Recurse | Remove-Item -Force

$required = @(
    'MikuN2N.exe', 'LICENSE.txt', 'build-identity.txt',
    'Runtime/n3n-edge.exe', 'Runtime/n3n-3.4.4-source.zip', 'Runtime/tap-windows-installer.exe',
    'Runtime/LICENSE-n2n.txt', 'Runtime/LICENSE-tap-windows.txt', 'Runtime/LICENSE-dotnet.txt',
    'Runtime/THIRD-PARTY-NOTICES.txt', 'Runtime/THIRD-PARTY-NOTICES-dotnet.txt', 'Runtime/README.txt'
)
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Package is missing $file" }
}
if (Test-Path -LiteralPath (Join-Path $publish 'Runtime/edge.exe')) {
    throw 'Legacy Runtime/edge.exe has no corresponding source and must not be published'
}
$productVersion = (Get-Item -LiteralPath (Join-Path $publish 'MikuN2N.exe')).VersionInfo.ProductVersion
if ($productVersion -ne $version) { throw "MikuN2N.exe reports $productVersion, expected $version" }

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
foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($file.Extension -eq '.zip') {
        $archive = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($bytes))
        try {
            foreach ($entry in $archive.Entries) {
                $stream = $entry.Open(); $buffer = [IO.MemoryStream]::new()
                try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
                if ($found = Find-PersonalPath $buffer.ToArray()) {
                    throw "$($file.Name)!$($entry.FullName) contains a personal path: $found"
                }
            }
        } finally { $archive.Dispose() }
    } elseif ($found = Find-PersonalPath $bytes) {
        throw "$($file.Name) contains a personal path: $found"
    }
}

$outputDirectory = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$zipName = "MikuN2N-$version-win-x64.zip"
$zipPath = Join-Path $outputDirectory $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS'), "$hash  $zipName`n")
Write-Host "Packaged $zipName"
Write-Host "SHA-256  $hash"
