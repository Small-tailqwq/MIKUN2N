# 决定性实验：同一个 IPv4 已经挂在另一块网卡上时，netsh 能否把它设到目标网卡？
# 这直接判定 17:36 那次 "Unable to set IP address" 的真正成因。
$ErrorActionPreference = 'Continue'
$UuGuid  = '{OTHER-TAP-GUID}'   # UU TAP
$TapGuid = '{TAP-ADAPTER-GUID}'   # TAP-Windows V9
$Ip = '192.0.2.77'; $Mask = '255.255.255.0'

$uu  = Get-NetAdapter | Where-Object InterfaceGuid -eq $UuGuid
$tap = Get-NetAdapter | Where-Object InterfaceGuid -eq $TapGuid
"UU  = [$($uu.ifIndex)] $($uu.Name)   状态=$($uu.Status)"
"TAP = [$($tap.ifIndex)] $($tap.Name)  状态=$($tap.Status)"

"`n--- 清场 ---"
foreach ($ix in $uu.ifIndex, $tap.ifIndex) {
  Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object { $_.PrefixOrigin -eq 'Manual' } |
    ForEach-Object { "  移除 [$ix] $($_.IPAddress)"; Remove-NetIPAddress -InputObject $_ -Confirm:$false }
}

"`n--- 步骤 A：先把 $Ip 设到 UU 网卡（模拟上次遗留）---"
$a = netsh interface ip set address "name=$($uu.ifIndex)" static $Ip $Mask 2>&1
"  netsh 退出码=$LASTEXITCODE  输出=$($a -join ' ')"
Start-Sleep 2
Get-NetIPAddress -InterfaceIndex $uu.ifIndex -AddressFamily IPv4 |
  ForEach-Object { "  UU 现有: $($_.IPAddress) $($_.PrefixOrigin) $($_.AddressState)" }

"`n--- 步骤 B：再把同一个 $Ip 设到 TAP 网卡（这就是 n3n 当时干的事）---"
$b = netsh interface ip set address "name=$($tap.ifIndex)" static $Ip $Mask 2>&1
"  netsh 退出码=$LASTEXITCODE  输出=$($b -join ' ')"
Start-Sleep 2
Get-NetIPAddress -InterfaceIndex $tap.ifIndex -AddressFamily IPv4 |
  ForEach-Object { "  TAP 现有: $($_.IPAddress) $($_.PrefixOrigin) $($_.AddressState)" }

"`n--- 结论 ---"
if ($LASTEXITCODE -ne 0) { "  地址冲突会让 netsh 返回非零 -> 这就是 17:36 失败的原因" }
else { "  netsh 返回 0，地址冲突不是原因，需另找" }

"`n--- 清理 ---"
foreach ($ix in $uu.ifIndex, $tap.ifIndex) {
  Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object { $_.PrefixOrigin -eq 'Manual' } |
    ForEach-Object { "  移除 [$ix] $($_.IPAddress)"; Remove-NetIPAddress -InputObject $_ -Confirm:$false }
}
