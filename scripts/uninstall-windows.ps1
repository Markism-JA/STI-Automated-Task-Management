$ErrorActionPreference = "Stop"

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\ScholarFlow"
$StartShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\ScholarFlow.lnk"
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "ScholarFlow.lnk"

if (Test-Path $StartShortcut) { Remove-Item -Force $StartShortcut }
if (Test-Path $DesktopShortcut) { Remove-Item -Force $DesktopShortcut }
if (Test-Path $InstallDir) { Remove-Item -Recurse -Force $InstallDir }

Write-Host "Uninstalled ScholarFlow."
