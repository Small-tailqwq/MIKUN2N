# n3n TAP 配置验证。默认测随包的 TAP-Windows Adapter V9。
# 场景 1：冷网卡（Disconnected、无地址）
# 场景 2：目标地址被另一块网卡占用（"The object already exists"）
# 需要管理员权限。

param(
    [string]$TapGuid = '',
    [string]$OtherGuid = '',
    [string]$EdgeExe = (Join-Path $(if ($env:N3N_PATCHED_SOURCE) { $env:N3N_PATCHED_SOURCE } else { '..\n3n-build\n3n-3.4.4-patched' }) 'apps\n3n-edge.exe'),
    [switch]$ConflictScenario
)
. (Join-Path $PSScriptRoot 'TapAdapters.ps1')
$TapGuid = Resolve-TapGuid $TapGuid
$OtherGuid = Resolve-OtherTapGuid $OtherGuid $TapGuid

$ErrorActionPreference = 'Continue'
if (-not (Test-Path $EdgeExe)) {
    throw "找不到 n3n-edge.exe：$EdgeExe`n请用 -EdgeExe 指定，或设置 `$env:N3N_PATCHED_SOURCE 指向 patched 源码树。"
}
$Session  = 'mikun2n-verify'
$ConfPath = "$env:USERPROFILE\n3n\$Session.conf"
$OutFile  = "$env:TEMP\n3n-verify-out.txt"
$ErrFile  = "$env:TEMP\n3n-verify-err.txt"

if (-not (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[X] 需要管理员权限" -ForegroundColor Red; exit 1
}
if (Get-Process n3n-edge -EA SilentlyContinue) {
    Write-Host "[X] n3n-edge 正在运行，请先断开 MikuN2N" -ForegroundColor Red; exit 1
}

function Clear-ManualIPs([int]$ix, [string]$tag) {
    Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
        Where-Object PrefixOrigin -eq 'Manual' |
        ForEach-Object { Write-Host "    [$tag] 移除 $($_.IPAddress)"; Remove-NetIPAddress -InputObject $_ -Confirm:$false }
}

$tap   = Get-NetAdapter | Where-Object InterfaceGuid -eq $TapGuid
$other = Get-NetAdapter | Where-Object InterfaceGuid -eq $OtherGuid
if (-not $tap) { Write-Host "[X] 找不到目标网卡" -ForegroundColor Red; exit 1 }

Write-Host "`n=== 目标网卡 ===" -ForegroundColor Cyan
Write-Host "    [$($tap.ifIndex)] $($tap.Name) | $($tap.InterfaceDescription) | $($tap.Status)"
Write-Host "    二进制 $EdgeExe"
Write-Host "    构建于 $((Get-Item $EdgeExe).LastWriteTime)"

Write-Host "`n=== 准备场景 ===" -ForegroundColor Cyan
Clear-ManualIPs $tap.ifIndex 'TAP'
if ($other) { Clear-ManualIPs $other.ifIndex 'OTHER' }

$expectIp = $null
if ($ConflictScenario -and $other) {
    # 先问一次 supernode 会给哪个 IP，再把它占到另一块网卡上
    Write-Host "    [冲突场景] 先探测 supernode 将分配的地址…"
    $probeConf = @"
[community]
name=$Community
supernode=vps.example.com:3076
[connection]
description=probe
bind=50013
[tuntap]
address_mode=auto
name=$TapGuid
[management]
port=8296
password=P
[logging]
verbose=2
"@
    [IO.File]::WriteAllText($ConfPath, $probeConf, (New-Object Text.UTF8Encoding($false)))
    $p = Start-Process $EdgeExe -ArgumentList 'start',$Session -RedirectStandardOutput $OutFile -RedirectStandardError $ErrFile -NoNewWindow -PassThru
    Start-Sleep 8; if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }
    $probeLog = (Get-Content $OutFile,$ErrFile -Raw -EA SilentlyContinue) -join "`n"
    if ($probeLog -match 'created local tap device IPv4: (\d+\.\d+\.\d+\.\d+)') { $expectIp = $Matches[1] }
    Clear-ManualIPs $tap.ifIndex 'TAP'
    if ($expectIp) {
        Write-Host "    [冲突场景] supernode 会分配 $expectIp，现在把它占到 [$($other.ifIndex)] $($other.Name)"
        netsh interface ip set address "name=$($other.ifIndex)" static $expectIp 255.255.255.0 | Out-Null
        Start-Sleep 2
        Write-Host "    [冲突场景] 占用结果: $((Get-NetIPAddress -InterfaceIndex $other.ifIndex -AddressFamily IPv4 | Where-Object PrefixOrigin -eq 'Manual').IPAddress)"
    } else {
        Write-Host "    [冲突场景] 未能探到地址，退化为普通场景" -ForegroundColor Yellow
    }
}
Write-Host "    目标网卡状态: $((Get-NetAdapter -InterfaceIndex $tap.ifIndex).Status)"

Write-Host "`n=== 运行 ===" -ForegroundColor Cyan
$conf = @"
[community]
name=$Community
supernode=vps.example.com:3076

[connection]
description=verify-test
bind=50012

[tuntap]
address_mode=auto
name=$TapGuid
metric=1

[management]
port=8297
password=VERIFYONLY

[logging]
verbose=2
"@
[IO.File]::WriteAllText($ConfPath, $conf, (New-Object Text.UTF8Encoding($false)))

Remove-Item $OutFile,$ErrFile -Force -EA SilentlyContinue
$proc = Start-Process $EdgeExe -ArgumentList 'start',$Session `
        -RedirectStandardOutput $OutFile -RedirectStandardError $ErrFile -NoNewWindow -PassThru
Start-Sleep 20
if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
$log = ((Get-Content $OutFile -Raw -EA SilentlyContinue) + "`n" + (Get-Content $ErrFile -Raw -EA SilentlyContinue))

Write-Host "`n=== 关键输出 ===" -ForegroundColor Cyan
($log -split "`r?`n") | Where-Object {
    $_ -match 'Open device|Unable to set IP|created local tap device|edge started|\[OK\] edge|ERROR'
} | ForEach-Object { "    $($_.Trim())" }

Write-Host "`n=== 结论 ===" -ForegroundColor Cyan
$gotIp  = $log -match 'created local tap device IPv4'
$gotSn  = $log -match '\[OK\] edge'
$failIp = $log -match 'Unable to set IP address'
if ($ConflictScenario) {
    if ($failIp) { Write-Host "    [符合预期] 地址被占用时 n3n 报 Unable to set IP address（由 MikuN2N 侧清理后重试）" -ForegroundColor Yellow }
    else { Write-Host "    [注意] 冲突场景下竟然成功了，说明冲突未复现" -ForegroundColor Yellow }
} elseif ($gotIp -and $gotSn -and -not $failIp) {
    Write-Host "    [PASS] 冷网卡成功配置 IP 并连上 supernode" -ForegroundColor Green
} else {
    Write-Host "    [FAIL] gotIP=$gotIp gotSupernode=$gotSn failIP=$failIp" -ForegroundColor Red
    Write-Host "    日志: $OutFile / $ErrFile"
}

Write-Host "`n=== 清理 ===" -ForegroundColor Cyan
Clear-ManualIPs $tap.ifIndex 'TAP'
if ($other) { Clear-ManualIPs $other.ifIndex 'OTHER' }
Remove-Item $ConfPath -Force -EA SilentlyContinue
Remove-Item "$env:USERPROFILE\n3n\$Session" -Recurse -Force -EA SilentlyContinue
Write-Host ""
