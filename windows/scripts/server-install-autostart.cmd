@echo off
setlocal

set "SERVER_EXE=%~1"
if "%SERVER_EXE%"=="" set "SERVER_EXE=%ProgramFiles%\RentalSystem\Server\RentalServer.exe"

if not exist "%SERVER_EXE%" (
    echo RentalServer.exe not found: "%SERVER_EXE%"
    exit /b 2
)

schtasks /Create /TN "RentalServer" /SC ONLOGON /TR "\"%SERVER_EXE%\"" /RL LIMITED /F
exit /b %ERRORLEVEL%
