param([string]$GameDir = "")

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here "steam.ps1")
if (-not $GameDir) { $GameDir = Find-Game }

Write-Host ""
Write-Host "CMS 2018 Co-op uninstaller" -ForegroundColor Cyan
Write-Host ""
if (-not $GameDir) {
    Write-Host "Couldn't find the game through Steam. Nothing was removed." -ForegroundColor Red
    exit 1
}
Write-Host "Game folder: $GameDir"
while (Get-Process cms2018 -ErrorAction SilentlyContinue | Where-Object { -not $_.Path -or $_.Path.StartsWith($GameDir, [System.StringComparison]::OrdinalIgnoreCase) }) {
    Write-Host "The game is running. Close it, then press Enter to continue." -ForegroundColor Yellow
    [void](Read-Host)
}

$others = @(Get-ChildItem (Join-Path $GameDir "BepInEx\plugins") -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin "CMS2018Coop.dll", "mechanic.cmsm" })
foreach ($name in "BepInEx\plugins\CMS2018Coop.dll", "BepInEx\plugins\mechanic.cmsm", "BepInEx\config\fishybeing.cms2018coop.cfg") {
    $path = Join-Path $GameDir $name
    if (Test-Path $path) { Remove-Item $path -Force }
}
if ($others.Count -gt 0) {
    Write-Host "Removed the co-op mod. You have other BepInEx mods, so BepInEx itself was left in place."
} else {
    foreach ($name in "BepInEx", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt") {
        $path = Join-Path $GameDir $name
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Write-Host "Removed the co-op mod and BepInEx. The game is back to normal."
}
exit 0
