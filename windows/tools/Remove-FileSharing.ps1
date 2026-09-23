# Removes only the TwinDesk-created shares and firewall rules. Files and the
# Ethernet adapter configuration are left intact.
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
foreach ($drive in @('C','E')) {
    $name = 'TwinDesk-' + $drive
    $share = Get-SmbShare -Name $name -ErrorAction SilentlyContinue
    if ($share -and $share.Path -ne ($drive + ':\')) { throw 'Unexpected share path; stopping.' }
    if ($share -and $PSCmdlet.ShouldProcess($name, 'Remove file share (keep all files)')) { Remove-SmbShare -Name $name -Force }
}
foreach ($name in @('TwinDesk-Direct-445','TwinDesk-Direct-48150')) {
    if ((Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) -and $PSCmdlet.ShouldProcess($name, 'Remove firewall allowance')) { Remove-NetFirewallRule -Name $name }
}
