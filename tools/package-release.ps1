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

# Debug paths embed the build machine's account name; a release must not carry it.
$account = [Environment]::UserName
if ($account.Length -ge 3) {
    $patterns = foreach ($separator in '\', '/') {
        $text = "Users$separator$account"
        $text
        -join ($text.ToCharArray() | ForEach-Object { "$_`0" })
    }
    foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object Extension -ne '.zip') {
        # Latin-1 maps every byte to one char, so ordinal search finds ASCII and UTF-16 paths alike.
        $content = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
        foreach ($pattern in $patterns) {
            if ($content.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "$($file.Name) contains the local account path Users\$account"
            }
        }
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
