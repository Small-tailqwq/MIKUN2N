# 验证 set_peer_relay 的 MAC 键。
#
# 复现的真实故障：n3n 只从 REGISTER 学到对端的虚拟 IPv4，若某个 peer 条目是先从数据包
# 建立的，它的 dev_addr 永远是 0，按 IP 下发的强制中继就匹配 0 条 —— 本机保持 P2P、
# 对端保持 pSp，延迟在两个值之间横跳。
#
# 本脚本在本机起两个 edge（各占一块 TAP 网卡）互为 peer，然后：
#   用例 A：用正确 IP 下发   -> 基线，应匹配
#   用例 B：用错误 IP + 正确 MAC 下发 -> 这就是 dev_addr 未知的等价场景，
#                                        旧版必然 0，新版应匹配
# 需要管理员权限。
param(
    [string]$TapGuid   = '{TAP-ADAPTER-GUID}',  # TAP-Windows Adapter V9
    [string]$OtherGuid = '{OTHER-TAP-GUID}',  # Netease UU TAP
    [string]$EdgeExe   = (Join-Path $(if ($env:N3N_PATCHED_SOURCE) { $env:N3N_PATCHED_SOURCE } else { '..\n3n-build\n3n-3.4.4-patched' }) 'apps\n3n-edge.exe')
)
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $EdgeExe)) {
    throw "找不到 n3n-edge.exe：$EdgeExe`n请用 -EdgeExe 指定，或设置 `$env:N3N_PATCHED_SOURCE 指向 patched 源码树。"
}
$Pass = 'RELAYVERIFY'

if (-not (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[X] 需要管理员权限" -ForegroundColor Red; exit 1
}
if (Get-Process n3n-edge -EA SilentlyContinue) {
    Write-Host "[X] n3n-edge 正在运行，请先断开 MikuN2N" -ForegroundColor Red; exit 1
}

function Clear-ManualIPs([string]$guid) {
    $ix = (Get-NetAdapter | Where-Object InterfaceGuid -eq $guid).ifIndex
    if (-not $ix) { return }
    Get-NetIPAddress -InterfaceIndex $ix -AddressFamily IPv4 -EA SilentlyContinue |
        Where-Object PrefixOrigin -eq 'Manual' |
        ForEach-Object { Remove-NetIPAddress -InputObject $_ -Confirm:$false }
}

function Start-Edge([string]$tag, [string]$guid, [int]$bind, [int]$mgmt) {
    $conf = @"
[community]
name=$Community
supernode=vps.example.com:3076

[connection]
description=$tag
bind=$bind

[tuntap]
address_mode=auto
name=$guid

[management]
port=$mgmt
password=$Pass

[logging]
verbose=2
"@
    [IO.File]::WriteAllText("$env:USERPROFILE\n3n\$tag.conf", $conf, (New-Object Text.UTF8Encoding($false)))
    Start-Process $EdgeExe -ArgumentList 'start',$tag `
        -RedirectStandardOutput "$env:TEMP\$tag-o.txt" -RedirectStandardError "$env:TEMP\$tag-e.txt" `
        -NoNewWindow -PassThru
}

function Invoke-Mgmt([int]$mgmt, [string]$method, $params) {
    $body = @{ jsonrpc = '2.0'; method = $method; id = '1' }
    if ($null -ne $params) { $body.params = $params }
    $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("mikun2n:$Pass"))
    try {
        Invoke-RestMethod -Uri "http://[::1]:$mgmt/v1" -Method Post `
            -Body ($body | ConvertTo-Json -Depth 5 -Compress) -ContentType 'application/json' `
            -Headers @{ Authorization = "Basic $auth" } -TimeoutSec 6
    } catch {
        Write-Host "    管理接口调用失败: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

Clear-ManualIPs $TapGuid
Clear-ManualIPs $OtherGuid

Write-Host "`n=== 启动两个本地 edge ===" -ForegroundColor Cyan
$a = Start-Edge 'relayverify-a' $TapGuid   50020 8301
$b = Start-Edge 'relayverify-b' $OtherGuid 50021 8302
Write-Host "    等待互相发现…"
Start-Sleep 25

try {
    Write-Host "`n=== A 侧看到的 peer ===" -ForegroundColor Cyan
    $edges = (Invoke-Mgmt 8301 'get_edges' $null).result
    $peer  = $edges | Where-Object { $_.desc -eq 'relayverify-b' } | Select-Object -First 1
    if (-not $peer) {
        Write-Host "    [X] A 没有看到 B，无法继续" -ForegroundColor Red
        $edges | ForEach-Object { "    desc=$($_.desc) ip=$($_.ip4addr) mac=$($_.macaddr) mode=$($_.mode)" }
        exit 1
    }
    $peerIp  = ($peer.ip4addr -split '/')[0]
    $peerMac = $peer.macaddr
    Write-Host "    B: ip4addr='$peerIp' macaddr=$peerMac mode=$($peer.mode)"

    Write-Host "`n--- 用例 A：正确 IP，不带 MAC（基线）---" -ForegroundColor Cyan
    $r = Invoke-Mgmt 8301 'set_peer_relay' @($peerIp, $true)
    Write-Host "    匹配条目数 = $($r.result)"
    $caseA = $r.result -ge 1
    Invoke-Mgmt 8301 'set_peer_relay' @($peerIp, $false) | Out-Null

    Write-Host "`n--- 用例 B：错误 IP + 正确 MAC（等价于 dev_addr 未知）---" -ForegroundColor Cyan
    $r = Invoke-Mgmt 8301 'set_peer_relay' @('192.0.2.99', $true, $peerMac)
    Write-Host "    匹配条目数 = $($r.result)"
    $caseB = $r.result -ge 1

    Write-Host "`n--- 用例 C：策略是否真的反映到 get_edges ---" -ForegroundColor Cyan
    Start-Sleep 2
    $after = ((Invoke-Mgmt 8301 'get_edges' $null).result |
              Where-Object { $_.desc -eq 'relayverify-b' } | Select-Object -First 1)
    Write-Host "    mode=$($after.mode) force_relay=$($after.force_relay) punch_state=$($after.punch_state)"
    $caseC = [bool]$after.force_relay

    Write-Host "`n--- 用例 D：用同一个错误 IP + MAC 取消 ---" -ForegroundColor Cyan
    $r = Invoke-Mgmt 8301 'set_peer_relay' @('192.0.2.99', $false, $peerMac)
    Write-Host "    匹配条目数 = $($r.result)"
    Start-Sleep 2
    $after2 = ((Invoke-Mgmt 8301 'get_edges' $null).result |
               Where-Object { $_.desc -eq 'relayverify-b' } | Select-Object -First 1)
    Write-Host "    force_relay=$($after2.force_relay)"
    $caseD = -not [bool]$after2.force_relay

    Write-Host "`n=== 结论 ===" -ForegroundColor Cyan
    "    A 正确IP匹配        : $(if ($caseA) { 'PASS' } else { 'FAIL' })"
    "    B 错误IP+MAC 匹配   : $(if ($caseB) { 'PASS' } else { 'FAIL' })"
    "    C 策略反映到 get_edges: $(if ($caseC) { 'PASS' } else { 'FAIL' })"
    "    D 按 MAC 取消        : $(if ($caseD) { 'PASS' } else { 'FAIL' })"
    if ($caseA -and $caseB -and $caseC -and $caseD) {
        Write-Host "    [PASS] MAC 键可独立于 dev_addr 定位 peer" -ForegroundColor Green
    } else {
        Write-Host "    [FAIL] 见上" -ForegroundColor Red
    }
} finally {
    Write-Host "`n=== 清理 ===" -ForegroundColor Cyan
    foreach ($p in @($a, $b)) { if ($p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit() } }
    foreach ($t in 'relayverify-a','relayverify-b') {
        Remove-Item "$env:USERPROFILE\n3n\$t.conf" -Force -EA SilentlyContinue
        Remove-Item "$env:USERPROFILE\n3n\$t" -Recurse -Force -EA SilentlyContinue
    }
    Clear-ManualIPs $TapGuid
    Clear-ManualIPs $OtherGuid
    Write-Host ""
}
