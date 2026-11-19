$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExeSrc = Join-Path $ScriptDir "ScholarFlow.exe"
if (-not (Test-Path $ExeSrc)) {
    $ExeSrc = Join-Path (Join-Path (Split-Path -Parent $ScriptDir) "dist\win-x64") "ScholarFlow.exe"
}
if (-not (Test-Path $ExeSrc)) {
    Write-Error "ScholarFlow.exe not found. Run publish.sh first or extract the full Windows release zip."
    exit 1
}

$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\ScholarFlow"
$InstalledExe = Join-Path $InstallDir "ScholarFlow.exe"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force $ExeSrc $InstalledExe

# Add to User PATH if not already present
$UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ([string]::IsNullOrWhiteSpace($UserPath)) {
    [Environment]::SetEnvironmentVariable("Path", $InstallDir, "User")
} elseif (-not ($UserPath.Split(';') -contains $InstallDir)) {
    [Environment]::SetEnvironmentVariable("Path", "$UserPath;$InstallDir", "User")
}

$WshShell = New-Object -ComObject WScript.Shell

# Start Menu Shortcut
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$StartShortcut = $WshShell.CreateShortcut((Join-Path $StartMenuDir "ScholarFlow.lnk"))
$StartShortcut.TargetPath = $InstalledExe
$StartShortcut.WorkingDirectory = $InstallDir
$StartShortcut.Description = "ScholarFlow - Academic Task & Study Manager"
$StartShortcut.IconLocation = "$InstalledExe,0"
$StartShortcut.Save()

# Desktop Shortcut
$DesktopDir = [Environment]::GetFolderPath("Desktop")
$DesktopShortcut = $WshShell.CreateShortcut((Join-Path $DesktopDir "ScholarFlow.lnk"))
$DesktopShortcut.TargetPath = $InstalledExe
$DesktopShortcut.WorkingDirectory = $InstallDir
$DesktopShortcut.Description = "ScholarFlow - Academic Task & Study Manager"
$DesktopShortcut.IconLocation = "$InstalledExe,0"
$DesktopShortcut.Save()

Write-Host "Installed ScholarFlow:"
Write-Host "  Executable : $InstalledExe"
Write-Host "  Start Menu : $(Join-Path $StartMenuDir 'ScholarFlow.lnk')"
Write-Host "  Desktop    : $(Join-Path $DesktopDir 'ScholarFlow.lnk')"
