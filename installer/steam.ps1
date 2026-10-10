$gameFolder = "Car Mechanic Simulator 2018"

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath) { $roots += $item.SteamPath }
            if ($item.InstallPath) { $roots += $item.InstallPath }
        } catch {}
    }
    $roots += "C:\Program Files (x86)\Steam", "C:\Program Files\Steam"
    $libraries = @()
    foreach ($root in $roots) {
        $root = $root -replace "/", "\"
        if (-not (Test-Path $root)) { continue }
        $libraries += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($match in Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"') {
                $libraries += ($match.Matches[0].Groups[1].Value -replace "\\\\", "\")
            }
        }
    }
    $libraries | Select-Object -Unique
}

function Find-Game {
    foreach ($library in Get-SteamLibraries) {
        $candidate = Join-Path $library "steamapps\common\$gameFolder"
        if (Test-Path (Join-Path $candidate "cms2018.exe")) { return $candidate }
    }
    return $null
}
