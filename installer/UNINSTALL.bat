@echo off
set "SCRIPT=%~dp0installer\uninstall.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0uninstall.ps1"
if not exist "%SCRIPT%" (
    echo Can't find the uninstaller files. Extract the whole zip first, then run UNINSTALL.bat from the extracted folder.
    echo.
    pause
    exit /b 1
)
choice /M "Remove the CMS 2018 co-op mod"
if errorlevel 2 exit /b
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
pause
