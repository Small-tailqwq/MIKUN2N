# 验证「固定 MAC 快速重连会被 supernode 拒绝，换随机 MAC 能立刻逃脱」这条恢复路径。
# 这是 MikuN2N 里 _useRotatingMac 兜底所依赖的机制。需要管理员权限。
param(
    [string]$TapGuid = '',
    [string]$EdgeExe = (Join-Path $PSScriptRoot '..\native\n3n-3.4.4\apps\n3n-edge.exe')
)
. (Join-Path $PSScriptRoot 'TapAdapters.ps1')
$TapGuid = Resolve-TapGuid $TapGuid
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $EdgeExe)) {
    throw "找不到 n3n-edge.exe：$EdgeExe`n请用 -EdgeExe 指定，或先在 native/n3n-3.4.4 里构建 edge。"
}
$Session  = 'mikun2n-macrot'
$ConfPath = "$env:USERPROFILE\n3n\$Session.conf"

function Run-Edge([string]$mac, [int]$secs, [string]$tag) {
    $macLine = if ($mac) { "macaddr=$mac" } else { "" }
    $conf = @"
[community]
name=$Community
supernode=vps.example.com:3076

[connection]
description=macrot-$tag
bind=50014

[tuntap]
address_mode=auto
name=$TapGuid
$macLine

[management]
port=8295
password=P

[logging]
verbose=2
"@
    [IO.File]::WriteAllText($ConfPath, $conf, (New-Object Text.UTF8Encoding($false)))
    $o = "$env:TEMP\macrot-o.txt"; $e = "$env:TEMP\macrot-e.txt"
    Remove-Item $o,$e -Force -EA SilentlyContinue
    $p = Start-Process $EdgeExe -ArgumentList 'start',$Session -RedirectStandardOutput $o -RedirectStandardError $e -NoNewWindow -PassThru
    Start-Sleep $secs
    if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }
    return ((Get-Content $o -Raw -EA SilentlyContinue) + "`n" + (Get-Content $e -Raw -EA SilentlyContinue))
}

Write-Host "`n=== 第 1 跑：用网卡固定 MAC 注册，然后立刻杀掉 ===" -ForegroundColor Cyan
$log1 = Run-Edge $null 10 'first'
if ($log1 -match '\[OK\] edge') { Write-Host "    连上了 supernode（注册已占用）" -ForegroundColor Green }
else { Write-Host "    第一跑没连上，后续结论无效" -ForegroundColor Red }

Write-Host "`n=== 第 2 跑：立刻用同一个固定 MAC 重连（预期被拒）===" -ForegroundColor Cyan
$log2 = Run-Edge $null 14 'same'
$rejected = $log2 -match 'already in use or not released'
$ok2 = $log2 -match '\[OK\] edge'
Write-Host "    出现 already-in-use = $rejected ；连上 = $ok2"

Write-Host "`n=== 第 3 跑：立刻改用随机 MAC 重连（预期成功=恢复路径成立）===" -ForegroundColor Cyan
$b = 1..6 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }
$b[0] = ($b[0] -band 0xFC) -bor 0x02
$mac = ($b | ForEach-Object { '{0:X2}' -f $_ }) -join ':'
Write-Host "    随机 MAC = $mac"
$log3 = Run-Edge $mac 20 'rot'
$ok3 = $log3 -match '\[OK\] edge'
$rej3 = $log3 -match 'already in use or not released'
Write-Host "    连上 = $ok3 ；仍被拒 = $rej3"

Write-Host "`n=== 结论 ===" -ForegroundColor Cyan
if ($rejected -and $ok3) {
    Write-Host "    [PASS] 固定 MAC 快速重连确会被拒，换随机 MAC 可立即恢复 -> 兜底机制成立" -ForegroundColor Green
} elseif (-not $rejected) {
    Write-Host "    [注意] 固定 MAC 重连没有被拒，说明该场景未复现" -ForegroundColor Yellow
} else {
    Write-Host "    [FAIL] 换了随机 MAC 仍未连上，兜底机制不成立" -ForegroundColor Red
}

Remove-Item $ConfPath -Force -EA SilentlyContinue
Remove-Item "$env:USERPROFILE\n3n\$Session" -Recurse -Force -EA SilentlyContinue
Get-NetIPAddress -InterfaceIndex (Get-NetAdapter | Where-Object InterfaceGuid -eq $TapGuid).ifIndex -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object PrefixOrigin -eq 'Manual' | ForEach-Object { Remove-NetIPAddress -InputObject $_ -Confirm:$false }
Write-Host ""
