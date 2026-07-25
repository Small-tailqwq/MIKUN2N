# 验证 NetworkTuningService：防火墙放行、专用网络、广播优先级，以及断开后的还原。
# 直接调用编译出的真实服务代码（tune-harness），不是复刻命令。需要管理员权限。
param(
    [string]$TapGuid  = '{TAP-ADAPTER-GUID}',
    [string]$EdgeExe  = '<n3n-build>\n3n-3.4.4-patched\apps\n3n-edge.exe',
    [Parameter(Mandatory)][string]$Harness
)
$ErrorActionPreference = 'Continue'
$Session  = 'mikun2n-tune'
$RuleName = 'MikuN2N 虚拟局域网'

if (-not (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[X] 需要管理员权限" -ForegroundColor Red; exit 1
}
if (Get-Process n3n-edge -EA SilentlyContinue) {
    Write-Host "[X] n3n-edge 正在运行，请先断开 MikuN2N" -ForegroundColor Red; exit 1
}

$adapter = Get-NetAdapter | Where-Object InterfaceGuid -eq $TapGuid
$ix = $adapter.ifIndex

# 干净起点
Remove-NetFirewallRule -DisplayName $RuleName -EA SilentlyContinue
Set-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4 -AutomaticMetric Enabled -EA SilentlyContinue
try { Set-NetConnectionProfile -InterfaceIndex $ix -NetworkCategory Public -EA Stop } catch {}

function Report([string]$tag) {
    $m  = Get-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue
    $pr = Get-NetConnectionProfile -InterfaceIndex $ix -EA SilentlyContinue
    $fw = Get-NetFirewallRule -DisplayName $RuleName -EA SilentlyContinue
    "$tag : metric=$($m.InterfaceMetric)/$($m.AutomaticMetric) 网络位置=$(if($pr){$pr.NetworkCategory}else{'(无)'}) 防火墙规则=$(if($fw){'有'}else{'无'})"
}
Report '起点  '

Write-Host "`n=== 启动 edge 让网卡就绪 ===" -ForegroundColor Cyan
$conf = @"
[community]
name=mygroup
supernode=vps.example.com:3076

[connection]
description=tune-test
bind=50024

[tuntap]
address_mode=auto
name=$TapGuid

[management]
port=8305
password=P

[logging]
verbose=2
"@
$confPath = Join-Path $env:USERPROFILE "n3n\$Session.conf"
[IO.File]::WriteAllText($confPath, $conf, (New-Object Text.UTF8Encoding($false)))
$p = Start-Process $EdgeExe -ArgumentList 'start',$Session `
     -RedirectStandardOutput "$env:TEMP\tune-o.txt" -RedirectStandardError "$env:TEMP\tune-e.txt" `
     -NoNewWindow -PassThru
Start-Sleep 12
$vip = (Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
        Where-Object PrefixOrigin -eq 'Manual').IPAddress
if (-not $vip) {
    Write-Host "[X] 网卡未取得虚拟地址，无法继续" -ForegroundColor Red
    if (-not $p.HasExited) { $p.Kill() }
    exit 1
}
"    虚拟地址 = $vip"

Write-Host "`n=== 调用 NetworkTuningService.ApplyAsync ===" -ForegroundColor Cyan
& $Harness apply $TapGuid $vip
Start-Sleep 2
Report '优化后'

$m   = Get-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4
$pr  = Get-NetConnectionProfile -InterfaceIndex $ix -EA SilentlyContinue
$fw  = Get-NetFirewallRule -DisplayName $RuleName -EA SilentlyContinue
$af  = if ($fw) { $fw | Get-NetFirewallAddressFilter }
$best = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '255.255.255.255/32' -EA SilentlyContinue |
        Select-Object ifIndex, InterfaceAlias,
            @{n='IfMetric';e={(Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4).InterfaceMetric}} |
        Where-Object { (Get-NetAdapter -InterfaceIndex $_.ifIndex -EA SilentlyContinue).Status -eq 'Up' } |
        Sort-Object IfMetric | Select-Object -First 1

Write-Host "`n--- 明细 ---" -ForegroundColor Cyan
"    防火墙规则作用域 = $(if($af){$af.RemoteAddress}else{'(无)'})"
"    防火墙规则动作   = $(if($fw){"$($fw.Action) / $($fw.Direction) / Enabled=$($fw.Enabled)"}else{'(无)'})"
"    255.255.255.255 首选出口 = $($best.InterfaceAlias) (跃点 $($best.IfMetric))"

$okFw      = $fw -and $fw.Action -eq 'Allow' -and $fw.Direction -eq 'Inbound' -and
             $af.RemoteAddress -match '^10\.152\.0\.0'
$okPrivate = $pr -and $pr.NetworkCategory -eq 'Private'
$okMetric  = $m.InterfaceMetric -eq 1
$okWins    = $best.ifIndex -eq $ix

Write-Host "`n=== 断开还原 ===" -ForegroundColor Cyan
if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }   # 模拟异常退出：n3n 自身清理不会执行
Start-Sleep 2
& $Harness restore $TapGuid
Start-Sleep 2
Report '还原后'
$after = Get-NetIPInterface -InterfaceIndex $ix -AddressFamily IPv4
$okRestore = $after.AutomaticMetric -eq 'Enabled'

Write-Host "`n=== 结论 ===" -ForegroundColor Cyan
"    防火墙按虚拟网段放行 : $(if ($okFw)      { 'PASS' } else { 'FAIL' })"
"    网络位置改为专用     : $(if ($okPrivate) { 'PASS' } else { 'FAIL' })"
"    网卡跃点降到 1       : $(if ($okMetric)  { 'PASS' } else { 'FAIL' })"
"    TAP 成为广播首选出口 : $(if ($okWins)    { 'PASS' } else { 'FAIL' })"
"    强杀后仍能还原跃点   : $(if ($okRestore) { 'PASS' } else { 'FAIL' })"
if ($okFw -and $okPrivate -and $okMetric -and $okWins -and $okRestore) {
    Write-Host "    [PASS]" -ForegroundColor Green
} else {
    Write-Host "    [FAIL] 见上" -ForegroundColor Red
}

Remove-Item $confPath -Force -EA SilentlyContinue
Remove-Item (Join-Path $env:USERPROFILE "n3n\$Session") -Recurse -Force -EA SilentlyContinue
Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object PrefixOrigin -eq 'Manual' | ForEach-Object { Remove-NetIPAddress -InputObject $_ -Confirm:$false }
Write-Host ""
