param([string]$GameDir = "", [switch]$DetectOnly)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here "steam.ps1")
$payload = Join-Path $here "mod"

function Pick-Folder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Couldn't find the game. Pick the folder that has cms2018.exe in it."
    if ($dialog.ShowDialog() -eq "OK") { return $dialog.SelectedPath }
    return $null
}

Write-Host ""
Write-Host "CMS 2018 Co-op installer" -ForegroundColor Cyan
Write-Host ""

if (-not $GameDir) { $GameDir = Find-Game }
if ($DetectOnly) {
    if ($GameDir) { Write-Host "Found the game at: $GameDir" } else { Write-Host "Game not found" }
    exit 0
}
if (-not $GameDir) {
    Write-Host "Couldn't find Car Mechanic Simulator 2018 through Steam, pick its folder in the window that opened."
    $GameDir = Pick-Folder
}
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir "cms2018.exe"))) {
    Write-Host "That folder doesn't have cms2018.exe in it. Nothing was installed." -ForegroundColor Red
    exit 1
}
Write-Host "Game folder: $GameDir"

if (-not (Test-Path (Join-Path $payload "BepInEx\plugins\CMS2018Coop.dll"))) {
    Write-Host "The 'mod' folder is missing. Extract the whole zip first, then run INSTALL.bat from the extracted folder." -ForegroundColor Red
    exit 1
}

while (Get-Process cms2018 -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path.StartsWith($GameDir, [System.StringComparison]::OrdinalIgnoreCase) }) {
    Write-Host "The game is running. Close it, then press Enter to continue." -ForegroundColor Yellow
    [void](Read-Host)
}

try {
    Copy-Item -Path (Join-Path $payload "*") -Destination $GameDir -Recurse -Force
    Copy-Item -Path (Join-Path $payload ".doorstop_version") -Destination $GameDir -Force -ErrorAction SilentlyContinue
} catch {
    Write-Host "Copying failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "If the game is in Program Files, right-click INSTALL.bat and pick 'Run as administrator'."
    exit 1
}

$ok = (Test-Path (Join-Path $GameDir "winhttp.dll")) -and (Test-Path (Join-Path $GameDir "BepInEx\plugins\CMS2018Coop.dll")) -and (Test-Path (Join-Path $GameDir "BepInEx\plugins\mechanic.cmsm"))
if (-not $ok) {
    Write-Host "Some files didn't make it. Your antivirus may have removed winhttp.dll, allow it and run this again." -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "Installed. Start the game from Steam and look for the 'CMS 2018 Co-op' box in the top-left." -ForegroundColor Green
exit 0
