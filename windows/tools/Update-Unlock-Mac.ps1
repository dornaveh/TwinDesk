param([Parameter(Mandatory=$true)][string]$MacHost, [Parameter(Mandatory=$true)][string]$MacUsername)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$package = Join-Path $root 'dist\TwinDesk-Windows-unlock'
$task = Get-ScheduledTask -TaskName ('TwinDesk-' + $env:USERNAME)
$exe = $task.Actions[0].Execute.Trim('"')
$install = Split-Path $exe
$prefix = [IO.Path]::GetFullPath((Join-Path $root 'dist')) + '\'
if (![IO.Path]::GetFullPath($exe).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($exe) -ine 'TwinDesk.exe' -or $install -ieq $package) { throw 'Unexpected TwinDesk install; no changes made.' }
$pointer = Join-Path $install 'data-directory.txt'
$pointerHash = (Get-FileHash -LiteralPath $pointer).Hash
$data = [IO.File]::ReadAllText($pointer).Trim()
$settingsFile = Join-Path $data 'settings.json'
if (!(Test-Path -LiteralPath $settingsFile)) { throw 'Saved settings were not found.' }
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-hashes.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.PSObject.Properties) {
    $source = [IO.Path]::GetFullPath((Join-Path $package $entry.Name))
    if (!$source.StartsWith([IO.Path]::GetFullPath($package) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected package path.' }
    if ((Get-FileHash -LiteralPath $source).Hash -ine $entry.Value) { throw ('Package mismatch: ' + $entry.Name) }
}
$targets = @(Get-Process TwinDesk -ErrorAction SilentlyContinue)
foreach ($target in $targets) { if ($target.Path -ine $exe) { throw 'TwinDesk is running from another location; no changes made.' } }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $root ('dist\TwinDesk-Windows-before-unlock-' + $stamp)
Copy-Item -LiteralPath $install -Destination $backup -Recurse
$settingsBackup = Join-Path $data ('settings-before-unlock-' + $stamp + '.json')
Copy-Item -LiteralPath $settingsFile -Destination $settingsBackup
$credentialsBefore = @(Get-ChildItem -LiteralPath $data -Filter '*.protected' | Get-FileHash | Select-Object Path,Hash)
$stopped = $false
try {
    $stopped = $true
    foreach ($target in $targets) { Stop-Process -Id $target.Id -Force; if (!$target.WaitForExit(10000)) { throw 'TwinDesk did not exit in time.' } }
    Get-ChildItem -LiteralPath $package | Where-Object Name -ne 'package-hashes.json' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse -Force }
    foreach ($entry in $manifest.PSObject.Properties) { if ((Get-FileHash -LiteralPath (Join-Path $install $entry.Name)).Hash -ine $entry.Value) { throw ('Installed hash mismatch: ' + $entry.Name) } }
    if ((Get-FileHash -LiteralPath $pointer).Hash -ine $pointerHash) { throw 'Data directory pointer changed.' }
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName MacHost -NotePropertyValue $MacHost -Force
    $settings | Add-Member -NotePropertyName MacUsername -NotePropertyValue $MacUsername -Force
    $settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath ($settingsFile + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($settingsFile + '.tmp') -Destination $settingsFile -Force
    foreach ($credential in $credentialsBefore) { if ((Get-FileHash -LiteralPath $credential.Path).Hash -ine $credential.Hash) { throw 'A saved identity file changed.' } }
    $process = Start-Process -FilePath $exe -ArgumentList @('--startup','--data',('"' + $data + '"')) -WorkingDirectory $install -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    $running = Get-Process -Id $process.Id -ErrorAction Stop
    @{installed=$true;updated=(Get-Date).ToString('o');pid=$running.Id;backup=$backup;settingsBackup=$settingsBackup;pairingPreserved=$true;dataPointerPreserved=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'local-data\unlock-mac-install.json') -Encoding UTF8
    'Unlock Mac update installed. Pairing, microphone selection, and startup task preserved.'
} catch {
    $problem = $_.Exception.Message
    if ($stopped) {
        Get-Process TwinDesk -ErrorAction SilentlyContinue | Where-Object Path -IEQ $exe | Stop-Process -Force
        Get-ChildItem -LiteralPath $backup | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $install -Recurse -Force }
        Copy-Item -LiteralPath $settingsBackup -Destination $settingsFile -Force
        Start-Process -FilePath $exe -ArgumentList @('--startup','--data',('"' + $data + '"')) -WorkingDirectory $install -WindowStyle Hidden
    }
    @{installed=$false;error=$problem;backup=$backup} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'local-data\unlock-mac-install.json') -Encoding UTF8
    throw $problem
}
