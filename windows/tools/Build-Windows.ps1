$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = Join-Path $root 'dist\TwinDesk-Windows'
& dotnet publish (Join-Path $root 'src\TwinDesk.Windows\TwinDesk.Windows.csproj') -c Release -r win-x64 --self-contained false -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
Copy-Item -LiteralPath (Join-Path $root 'docs\SETUP.html') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
New-Item -ItemType Directory -Force -Path (Join-Path $output 'setup') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tools\Configure-DirectLink.ps1'),(Join-Path $root 'tools\Remove-FileSharing.ps1') -Destination (Join-Path $output 'setup')
Write-Host "Ready: $output\TwinDesk.exe"
