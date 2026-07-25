# 验证 TAP 网卡的接口跃点（InterfaceMetric）是否真的被压到物理网卡之下。
#
# 为什么重要：224.0.0.0/4 和 255.255.255.255/32 这两条路由在每块网卡上都存在且
# RouteMetric 都是 256，平局由接口跃点决定。默认情况下物理网卡跃点更低，游戏的
# 房间广播就从物理网卡出去，隧道对面收不到。
#
# n3n 原版虽然有 metric 配置项，但设置 MIB_IPINTERFACE_ROW.Metric 时没有清掉
# UseAutomaticMetric，Windows 会继续按链路速率算跃点，配置值被静默丢弃。
# 需要管理员权限。
param(
    [string]$TapGuid = '{TAP-ADAPTER-GUID}',
    [string]$EdgeExe = '<n3n-build>\n3n-3.4.4-patched\apps\n3n-edge.exe',
    [int]$Metric = 1
)
$ErrorActionPreference = 'Continue'
$Session = 'mikun2n-metric'

if (-not (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[X] 需要管理员权限" -ForegroundColor Red; exit 1
}
if (Get-Process n3n-edge -EA SilentlyContinue) {
    Write-Host "[X] n3n-edge 正在运行，请先断开 MikuN2N" -ForegroundColor Red; exit 1
}

$ix = (Get-NetAdapter | Where-Object InterfaceGuid -eq $TapGuid).ifIndex
function Get-Metric { (Get-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue) }
function Best-BroadcastInterface {
    Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '255.255.255.255/32' -EA SilentlyContinue |
        Select-Object ifIndex, InterfaceAlias,
            @{n='IfMetric';e={(Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4).InterfaceMetric}} |
        Where-Object { (Get-NetAdapter -InterfaceIndex $_.ifIndex -EA SilentlyContinue).Status -eq 'Up' } |
        Sort-Object IfMetric | Select-Object -First 1
}

$before = Get-Metric
"运行前  TAP 跃点 = $($before.InterfaceMetric)  自动跃点 = $($before.AutomaticMetric)"
"运行前  255.255.255.255 首选出口 = $((Best-BroadcastInterface).InterfaceAlias)"

$conf = @"
[community]
name=mygroup
supernode=vps.example.com:3076

[connection]
description=metric-test
bind=50022

[tuntap]
address_mode=auto
name=$TapGuid
metric=$Metric

[management]
port=8303
password=P

[logging]
verbose=2
"@
[IO.File]::WriteAllText("$env:USERPROFILE\n3n\$Session.conf", $conf, (New-Object Text.UTF8Encoding($false)))

$p = Start-Process $EdgeExe -ArgumentList 'start',$Session `
     -RedirectStandardOutput "$env:TEMP\metric-o.txt" -RedirectStandardError "$env:TEMP\metric-e.txt" `
     -NoNewWindow -PassThru
Start-Sleep 12

$during = Get-Metric
$best   = Best-BroadcastInterface
"`n运行中  TAP 跃点 = $($during.InterfaceMetric)  自动跃点 = $($during.AutomaticMetric)"
"运行中  255.255.255.255 首选出口 = $($best.InterfaceAlias) (跃点 $($best.IfMetric))"

if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }
Start-Sleep 3
$after = Get-Metric
"`n退出后  TAP 跃点 = $($after.InterfaceMetric)  自动跃点 = $($after.AutomaticMetric)"

Write-Host "`n=== 结论 ===" -ForegroundColor Cyan
$applied  = $during.InterfaceMetric -eq $Metric
$wins     = $best.ifIndex -eq $ix
$restored = $after.AutomaticMetric -eq 'Enabled'
"    配置的 metric 已生效     : $(if ($applied)  { 'PASS' } else { "FAIL (实际 $($during.InterfaceMetric))" })"
"    TAP 成为广播首选出口     : $(if ($wins)     { 'PASS' } else { "FAIL (仍是 $($best.InterfaceAlias))" })"
"    退出后恢复自动跃点       : $(if ($restored) { 'PASS' } else { 'FAIL' })"
if ($applied -and $wins -and $restored) {
    Write-Host "    [PASS]" -ForegroundColor Green
} else {
    Write-Host "    [FAIL] 见上" -ForegroundColor Red
}

Remove-Item "$env:USERPROFILE\n3n\$Session.conf" -Force -EA SilentlyContinue
Remove-Item "$env:USERPROFILE\n3n\$Session" -Recurse -Force -EA SilentlyContinue
Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object PrefixOrigin -eq 'Manual' | ForEach-Object { Remove-NetIPAddress -InputObject $_ -Confirm:$false }
Write-Host ""
