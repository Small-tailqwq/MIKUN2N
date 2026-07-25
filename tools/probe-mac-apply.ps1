# 查明：配置里给了 macaddr 之后，n3n 实际用的是不是那个 MAC？
param(
    [string]$TapGuid = '{TAP-ADAPTER-GUID}',
    [string]$EdgeExe = '<n3n-build>\n3n-3.4.4-patched\apps\n3n-edge.exe'
)
$ErrorActionPreference = 'Continue'
$Session  = 'mikun2n-macprobe'
$ConfPath = "$env:USERPROFILE\n3n\$Session.conf"
$ix = (Get-NetAdapter | Where-Object InterfaceGuid -eq $TapGuid).ifIndex

$b = 1..6 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }
$b[0] = ($b[0] -band 0xFC) -bor 0x02
$mac = ($b | ForEach-Object { '{0:X2}' -f $_ }) -join ':'

"配置里要求的 MAC : $mac"
"运行前网卡 MAC   : $((Get-NetAdapter -InterfaceIndex $ix).MacAddress)"

$conf = @"
[community]
name=mygroup
supernode=vps.example.com:3076

[connection]
description=macprobe
bind=50015

[tuntap]
address_mode=auto
name=$TapGuid
macaddr=$mac

[management]
port=8294
password=P

[logging]
verbose=2
"@
[IO.File]::WriteAllText($ConfPath, $conf, (New-Object Text.UTF8Encoding($false)))

$o = "$env:TEMP\macprobe-o.txt"; $e = "$env:TEMP\macprobe-e.txt"
Remove-Item $o,$e -Force -EA SilentlyContinue
$p = Start-Process $EdgeExe -ArgumentList 'start',$Session -RedirectStandardOutput $o -RedirectStandardError $e -NoNewWindow -PassThru
Start-Sleep 25
if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }
$log = ((Get-Content $o -Raw -EA SilentlyContinue) + "`n" + (Get-Content $e -Raw -EA SilentlyContinue))

"`n--- n3n 输出（MAC 相关）---"
($log -split "`r?`n") | Where-Object { $_ -match 'Open device|created local tap device|auth error|already in use|\[OK\] edge|Reopening|Invalid MAC|registry' } |
    ForEach-Object { "    $($_.Trim())" }

"`n运行后网卡 MAC   : $((Get-NetAdapter -InterfaceIndex $ix).MacAddress)"
"`n--- 判定 ---"
$used = if ($log -match 'created local tap device IPv4: \S+, MAC: (\S+)') { $Matches[1] } else { '(未知)' }
"    n3n 实际使用的 MAC = $used"
if ($used -replace '[:-]','' -ieq ($mac -replace '[:-]','')) { "    [OK] 配置的 MAC 已生效" }
else { "    [X] 配置的 MAC 未生效，n3n 用的还是网卡原 MAC" }

Remove-Item $ConfPath -Force -EA SilentlyContinue
Remove-Item "$env:USERPROFILE\n3n\$Session" -Recurse -Force -EA SilentlyContinue
Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
    Where-Object PrefixOrigin -eq 'Manual' | ForEach-Object { Remove-NetIPAddress -InputObject $_ -Confirm:$false }
""
