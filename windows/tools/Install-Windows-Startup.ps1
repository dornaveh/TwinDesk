$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$exe = Join-Path $root 'dist\TwinDesk-Windows\TwinDesk.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build TwinDesk for Windows first.' }
$account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$data = Join-Path $root ('local-data\installed-' + $env:USERNAME)
New-Item -ItemType Directory -Force -Path $data | Out-Null
$oldData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TwinDesk'
foreach ($name in @('settings.json', 'identity.protected')) {
    $destination = Join-Path $data $name
    $source = Join-Path $oldData $name
    if (!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $source)) {
        Copy-Item -LiteralPath $source -Destination $destination
    }
}
# Use the same settings for direct launches and the interactive sign-in task.
[IO.File]::WriteAllText((Join-Path (Split-Path $exe) 'data-directory.txt'), $data)
$action = New-ScheduledTaskAction -Execute $exe -Argument ('--startup --data "' + $data + '"') -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
$trigger.Delay = 'PT15S'
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$taskName = 'TwinDesk-' + $env:USERNAME
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Start TwinDesk in the tray at sign-in and reconnect to the Mac.' -Force | Out-Null
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name TwinDesk -ErrorAction SilentlyContinue
Write-Output "Registered $taskName. Settings: $data"
