# Resolves TAP adapters by driver description at run time. Instance GUIDs differ on every
# machine, so the verification scripts accept one explicitly or find it here.

function Resolve-TapGuid([string]$Guid) {
    if ($Guid) { return $Guid }
    $adapter = Get-NetAdapter -IncludeHidden |
        Where-Object InterfaceDescription -like 'TAP-Windows Adapter V9*' |
        Sort-Object ifIndex | Select-Object -First 1
    if (-not $adapter) { throw '找不到 TAP-Windows Adapter V9 网卡，请先安装 TAP 驱动，或用 -TapGuid 指定。' }
    return $adapter.InterfaceGuid
}

# A second TAP-style adapter (another TAP-Windows instance or a third-party TAP driver)
# used by scripts that need a competing adapter.
function Resolve-OtherTapGuid([string]$Guid, [string]$Exclude) {
    if ($Guid) { return $Guid }
    $adapter = Get-NetAdapter -IncludeHidden |
        Where-Object { $_.InterfaceDescription -like '*TAP*' -and $_.InterfaceGuid -ne $Exclude } |
        Sort-Object ifIndex | Select-Object -First 1
    if (-not $adapter) { throw '需要第二块 TAP 类网卡，请用 -OtherGuid 指定。' }
    return $adapter.InterfaceGuid
}
