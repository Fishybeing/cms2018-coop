@echo off
set "SCRIPT=%~dp0installer\install.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0install.ps1"
if not exist "%SCRIPT%" (
    echo Can't find the installer files. Extract the whole zip first, then run INSTALL.bat from the extracted folder.
    echo.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
pause
