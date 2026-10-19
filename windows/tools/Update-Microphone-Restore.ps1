$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$package = Join-Path $root 'dist\TwinDesk-Windows-microphone-restore'
$report = Join-Path $root 'local-data\microphone-restore-install.json'
$taskName = 'TwinDesk-' + $env:USERNAME
$task = Get-ScheduledTask -TaskName $taskName
$exe = $task.Actions[0].Execute.Trim('"')
$install = Split-Path $exe
$expectedPrefix = [IO.Path]::GetFullPath((Join-Path $root 'dist')) + '\'
if (![IO.Path]::GetFullPath($exe).StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($exe) -ine 'TwinDesk.exe') { throw "Unexpected TwinDesk installation: $exe" }
if (!(Test-Path -LiteralPath $exe)) { throw "TwinDesk executable not found: $exe" }
$location = Join-Path $install 'data-directory.txt'
if (!(Test-Path -LiteralPath $location)) { throw 'Installed TwinDesk has no data-directory.txt; no changes made.' }
$data = [IO.File]::ReadAllText($location).Trim()
if (!(Test-Path -LiteralPath (Join-Path $data 'settings.json'))) { throw 'Saved TwinDesk settings were not found; no changes made.' }
$hashes = Get-Content -LiteralPath (Join-Path $package 'package-hashes.json') -Raw | ConvertFrom-Json
foreach ($file in $hashes.PSObject.Properties) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $package $file.Name) -Algorithm SHA256).Hash
    if ($actual -ine $file.Value) { throw "Package verification failed: $($file.Name). No changes made." }
}
$targets = @(Get-Process TwinDesk -ErrorAction SilentlyContinue)
foreach ($target in $targets) {
    if ($target.Path -ine $exe) { throw "TwinDesk is running from another installation: $($target.Path). No changes made." }
}
$backup = Join-Path $root ('dist\TwinDesk-Windows-before-microphone-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
Copy-Item -LiteralPath $install -Destination $backup -Recurse
$stopped = $false
try {
    Write-Output 'Updating TwinDesk. The Mac connection will reconnect briefly.'
    foreach ($target in $targets) { Stop-Process -Id $target.Id -Force; $target.WaitForExit(10000) | Out-Null }
    $stopped = $true
    Get-ChildItem -LiteralPath $package | Where-Object Name -ne 'package-hashes.json' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse -Force
    }
    foreach ($file in $hashes.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $install $file.Name) -Algorithm SHA256).Hash -ine $file.Value) {
            throw "Installed file verification failed: $($file.Name)"
        }
    }
    # Keep the same executable path, startup task, pairing and saved settings.
    Start-Process -FilePath $exe -ArgumentList @('--startup', '--data', ('"' + $data + '"')) -WorkingDirectory $install -WindowStyle Hidden
    Start-Sleep -Seconds 4
    $running = @(Get-Process TwinDesk -ErrorAction SilentlyContinue | Where-Object Path -IEQ $exe)
    if (!$running.Count) { throw 'Updated TwinDesk did not remain running.' }
    @{ installed = $true; updated = (Get-Date).ToString('o'); pid = @($running.Id); backup = $backup } |
        ConvertTo-Json | Set-Content -LiteralPath $report -Encoding UTF8
    Write-Output 'TwinDesk updated. Your saved microphone will restore automatically at startup.'
    Write-Output ('Backup: ' + $backup)
} catch {
    $problem = $_.Exception.Message
    if ($stopped) {
        Get-Process TwinDesk -ErrorAction SilentlyContinue | Where-Object Path -IEQ $exe | Stop-Process -Force
        Get-ChildItem -LiteralPath $backup | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse -Force }
        Start-Process -FilePath $exe -ArgumentList @('--startup', '--data', ('"' + $data + '"')) -WorkingDirectory $install -WindowStyle Hidden
    }
    @{ installed = $false; updated = (Get-Date).ToString('o'); error = $problem } |
        ConvertTo-Json | Set-Content -LiteralPath $report -Encoding UTF8
    throw $problem
}
