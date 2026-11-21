@echo off
setlocal

if /i "%~1"=="/uninstall" (
    schtasks /Delete /TN "RentalClient" /F >nul 2>nul
    exit /b 0
)

set "CLIENT_EXE=%~1"
if "%CLIENT_EXE%"=="" set "CLIENT_EXE=%ProgramFiles%\RentalSystem\Client\RentalClient.Windows.exe"

if not exist "%CLIENT_EXE%" (
    echo RentalClient.Windows.exe not found: "%CLIENT_EXE%"
    exit /b 2
)

set "RENTAL_CLIENT_EXE=%CLIENT_EXE%"
rem Launch ~5 s after logon (was 20 s). The client paints its local lock before
rem any network wait and retries discovery every 5 s, so launching earlier just
rem shows the lock sooner; keep a few seconds so the EXE isn't racing a
rem half-initialized diskless logon (NIC not enumerated, shell still loading).
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$exe=$env:RENTAL_CLIENT_EXE;" ^
  "$action=New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path -Parent $exe);" ^
  "$trigger=New-ScheduledTaskTrigger -AtLogOn;" ^
  "$trigger.Delay='PT5S';" ^
  "$settings=New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries;" ^
  "Register-ScheduledTask -TaskName 'RentalClient' -Action $action -Trigger $trigger -Settings $settings -Description 'Starts the RentalSystem Windows kiosk client at logon with a small crash watchdog.' -Force | Out-Null"

exit /b %ERRORLEVEL%
