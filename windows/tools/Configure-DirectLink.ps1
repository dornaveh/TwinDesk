# Run after connecting the Ethernet cable to the Mac.
# This script changes only the named Ethernet adapter, creates two named shares,
# and adds scoped firewall allowances. It does not change NTFS permissions.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InterfaceAlias = 'Ethernet',
    [string]$Account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [string]$PcAddress = '192.168.77.1',
    [string]$MacAddress = '192.168.77.2'
)
$ErrorActionPreference = 'Stop'
$adapter = Get-NetAdapter -Name $InterfaceAlias -ErrorAction Stop
if ($adapter.MediaType -ne '802.3') {
    throw 'Select a dedicated wired Ethernet adapter with -InterfaceAlias.'
}
if ($PcAddress -ne '192.168.77.1' -or $MacAddress -ne '192.168.77.2') {
    throw 'This setup currently uses the reviewed 192.168.77.0/30 direct-link network.'
}
# A dedicated /30 has only .1 and .2 as usable host addresses. No gateway or DNS
# is installed, so Wi-Fi remains the internet route on both machines.
$conflict = Get-NetRoute -AddressFamily IPv4 | Where-Object {
    $_.InterfaceIndex -ne $adapter.ifIndex -and $_.DestinationPrefix -eq '192.168.77.0/30'
}
if ($conflict) { throw 'Another interface already routes the proposed direct-link network.' }
$sid = ([System.Security.Principal.NTAccount]::new($Account)).Translate([System.Security.Principal.SecurityIdentifier])
if ($sid.IsWellKnown([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid)) { throw 'Specify your individual Windows account.' }
$existing = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.PrefixOrigin -eq 'Manual' -and $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne $PcAddress })
if ($existing.Count) { throw 'Ethernet already has a different manual address. Review it before running setup.' }
foreach ($drive in @('C','E')) {
    if (-not (Test-Path -LiteralPath ($drive + ':\'))) { throw "Drive $drive is missing." }
    $share = Get-SmbShare -Name ('TwinDesk-' + $drive) -ErrorAction SilentlyContinue
    if ($share -and $share.Path -ne ($drive + ':\')) { throw 'An existing share has a conflicting path.' }
    if ($share) {
        $access = @(Get-SmbShareAccess -Name $share.Name)
        if ($access | Where-Object { $_.AccountName -ne $Account -or $_.AccessControlType -ne 'Allow' -or $_.AccessRight -ne 'Full' }) {
            throw 'An existing TwinDesk share has different permissions. Review it before changing it.'
        }
    }
}
Write-Host 'PC Ethernet: 192.168.77.1 / 255.255.255.252, no gateway, no DNS'
Write-Host 'Mac Ethernet: 192.168.77.2 / 255.255.255.252, no router, no DNS'
Write-Host "Shares: C:\ and E:\, full share access for $Account; existing file permissions still apply."
Write-Host 'New firewall allowances: Ethernet only, from 192.168.77.2, TCP 445 and 48150.'
Write-Host 'Existing Windows sharing/firewall policies remain in effect on other networks.'
if ($PSCmdlet.ShouldProcess('Ethernet and the TwinDesk-C / TwinDesk-E shares', 'Configure direct connection and full-access file shares')) {
    $principal = [System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run setup as Administrator to apply changes. Use -WhatIf for a read-only preview.' }
    if (-not (Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $PcAddress -ErrorAction SilentlyContinue)) {
        New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $PcAddress -PrefixLength 30 | Out-Null
    }
    foreach ($drive in @('C','E')) {
        $name = 'TwinDesk-' + $drive
        if (-not (Get-SmbShare -Name $name -ErrorAction SilentlyContinue)) {
            New-SmbShare -Name $name -Path ($drive + ':\') -FullAccess $Account -EncryptData $true -FolderEnumerationMode AccessBased -CachingMode None -Description 'TwinDesk Mac file access' | Out-Null
        }
    }
    foreach ($port in @(445,48150)) {
        $name = 'TwinDesk-Direct-' + $port
        $old = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
        if ($old) { Remove-NetFirewallRule -Name $name }
        New-NetFirewallRule -Name $name -DisplayName ('TwinDesk direct Ethernet ' + $port) -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -LocalAddress $PcAddress -RemoteAddress $MacAddress -InterfaceAlias $InterfaceAlias -Profile Any | Out-Null
    }
    Write-Host 'Setup complete. In TwinDesk on Windows, choose 192.168.77.1 and copy a fresh pairing code.'
}
